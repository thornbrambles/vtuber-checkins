import concurrent.futures
import http.client
import json
import pathlib
import tempfile
import threading
import unittest
from unittest import mock
from urllib.parse import urlencode

from test_review_workflow import ROOT, dashboard, fixture


class DashboardRegressionTests(unittest.TestCase):
    def test_pending_checkin_prevents_staging_conflicting_count_edits(self):
        data = fixture()
        data["globals"]["ci_checkin_pending_json"] = "{}"
        original = json.dumps(data, sort_keys=True)
        with self.assertRaisesRegex(dashboard.ops.CheckinError, "pending check-in"):
            dashboard.edit_user(data, "1234", 0, 0)
        self.assertEqual(json.dumps(data, sort_keys=True), original)

    def test_zero_total_clears_importable_attendance_and_badge_once(self):
        data = fixture()
        data["globals"].update(ci_stream_id=7, ci_stream_count=1)
        data["users"][0]["vars"].update(ci_last_stream=7, ci_reward_last_stream=7, ci_badge="Forest Friend")
        dashboard.edit_user(data, "1234", 0, 12)
        values = data["users"][0]["vars"]
        self.assertEqual(values["ci_last_stream"], 0)
        self.assertEqual(values["ci_reward_last_stream"], 7)
        self.assertEqual(values["ci_streak"], 0)
        self.assertEqual(values["ci_badge"], "")
        self.assertEqual((values["ci_wheel_earned"], values["ci_wheel_claimed"]), (5, 1))
        self.assertEqual(data["globals"]["ci_stream_count"], 0)
        dashboard.edit_user(data, "1234", 0, 0)
        self.assertEqual(data["globals"]["ci_stream_count"], 0)
        with tempfile.TemporaryDirectory() as temp:
            saved = dashboard.export_snapshot(data, pathlib.Path(temp) / "original.json", temp)
            restored = dashboard.ops.load_backup(saved)
            self.assertEqual(restored["users"][0]["vars"]["ci_badge"], "")
            self.assertEqual(restored["users"][0]["vars"]["ci_last_stream"], 0)

    def test_badges_follow_config_without_removing_positive_attendance(self):
        cfg = json.loads((ROOT / "config/checkins.json").read_text(encoding="utf-8"))
        for milestone in cfg["milestones"]:
            data = fixture()
            data["users"][0]["vars"]["ci_last_stream"] = 7
            dashboard.edit_user(data, "1234", milestone["count"], 2)
            self.assertEqual(data["users"][0]["vars"]["ci_badge"], milestone["badge"])
            self.assertEqual(data["users"][0]["vars"]["ci_last_stream"], 7)

    def test_overlapping_http_edits_are_serialized_and_both_saved(self):
        ready, first_copy, release_first, second_copy = (threading.Event() for _ in range(4))
        captured, copies = [], []
        real_server, real_copy = dashboard.HTTPServer, dashboard.copy.deepcopy

        def server_factory(*args):
            server = real_server(*args)
            captured.append(server)
            ready.set()
            return server

        def pause_copy(value, memo=None):
            staged = real_copy(value, memo)
            if isinstance(value, dict) and value.get("format") == "vtuber-checkins-backup":
                copies.append(True)
                if len(copies) == 1:
                    first_copy.set()
                    if not release_first.wait(5):
                        raise RuntimeError("Test did not release first edit")
                else:
                    second_copy.set()
            return staged

        def post(port, path, fields):
            connection = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
            try:
                connection.request("POST", path, urlencode(dict(token="test-token", **fields)),
                                   {"Content-Type": "application/x-www-form-urlencoded"})
                response = connection.getresponse()
                body = response.read()
                self.assertEqual(response.status, 200, body)
            finally:
                connection.close()

        with tempfile.TemporaryDirectory() as temp:
            snapshot = pathlib.Path(temp) / "original.json"
            snapshot.write_text(json.dumps(fixture()), encoding="utf-8")
            with mock.patch.object(dashboard, "HTTPServer", side_effect=server_factory), \
                 mock.patch.object(dashboard.secrets, "token_urlsafe", return_value="test-token"), \
                 mock.patch.object(dashboard.copy, "deepcopy", side_effect=pause_copy), \
                 mock.patch("builtins.print"):
                server_thread = threading.Thread(target=dashboard.serve, args=(snapshot, temp, 0), daemon=True)
                server_thread.start()
                self.assertTrue(ready.wait(5))
                server = captured[0]
                port = server.server_address[1]
                try:
                    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
                        first = pool.submit(post, port, "/edit", {"userId": "1234", "total": 60, "streak": 1})
                        if not first_copy.wait(5):
                            first.result(timeout=1)
                            self.fail("First request never staged an edit")
                        second = pool.submit(post, port, "/edit", {"userId": "9999", "total": 20, "streak": 1})
                        try:
                            self.assertFalse(second_copy.wait(0.5), "Second edit copied stale state while first was in progress")
                        finally:
                            release_first.set()
                        first.result(timeout=5)
                        second.result(timeout=5)
                    post(port, "/save", {})
                    saved = dashboard.ops.load_backup(next(pathlib.Path(temp).glob("staged-*.json")))
                    self.assertEqual([u["vars"]["ci_total"] for u in saved["users"]], [60, 20])
                    events = [json.loads(v) for k, v in saved["globals"].items() if k.startswith("ci_events_month_")]
                    self.assertEqual(sum(len(e) for e in events), 2)
                finally:
                    release_first.set()
                    server.shutdown()
                    server_thread.join(timeout=5)
