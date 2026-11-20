"""Regression tests for monthly wheel policy."""
import importlib.util
import json
import pathlib
import tempfile
import types
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


ops = load("checkin_ops_test", "tools/checkin-ops.py")
dashboard = load("admin_dashboard_test", "tools/admin-dashboard.py")


def fixture():
    return {
        "format": "vtuber-checkins-backup",
        "schemaVersion": 1,
        "globals": {
            "ci_wheel_month_2026_11": json.dumps([
                {"id": "entry-1", "slotId": "entry-1", "userId": "1234",
                 "user": "alice", "game": "Game One", "month": "2026-11",
                 "status": "pending"},
                {"id": "entry-2", "slotId": "entry-2", "userId": "9999",
                 "user": "bob", "game": "Game Two", "month": "2026-11",
                 "status": "approved"}
            ])
        },
        "users": [
            {"userId": "1234", "userName": "alice",
             "vars": {"ci_total": 50, "ci_streak": 4, "ci_wheel_earned": 5, "ci_wheel_claimed": 1}},
            {"userId": "9999", "userName": "bob",
             "vars": {"ci_total": 10, "ci_wheel_earned": 1, "ci_wheel_claimed": 1}}
        ]
    }


class MonthPolicyTests(unittest.TestCase):
    def run_game(self, state_path, operation, viewer, title=None, at="2026-11-30T23:55:00-05:00"):
        args = types.SimpleNamespace(
            file=str(state_path), operation=operation, viewer=viewer,
            title=title, at=at, value=None, force=False
        )
        ops.sandbox_main(args)
        return json.loads(state_path.read_text(encoding="utf-8"))

    def test_three_picks_november_then_new_month(self):
        with tempfile.TemporaryDirectory() as temp:
            path = pathlib.Path(temp) / "sandbox.json"
            self.run_game(path, "init", "test_alice")
            args = types.SimpleNamespace(
                file=str(path), operation="set", viewer="test_alice",
                title=None, at=None, value=50, force=False
            )
            ops.sandbox_main(args)
            for title in ("Title One", "Title Two", "Title Three"):
                self.run_game(path, "game", "test_alice", title)
            with self.assertRaisesRegex(ops.CheckinError, "Monthly game submission limit"):
                self.run_game(path, "game", "test_alice", "Title Four")
            state = self.run_game(path, "game", "test_alice", "Title Four",
                                  at="2026-12-01T00:01:00-05:00")
            self.assertEqual(state["users"]["test_alice"]["spent"], 4)
            self.assertEqual(len(state["wheel"]), 4)

    def test_duplicate_titles_case_space_insensitive(self):
        with tempfile.TemporaryDirectory() as temp:
            path = pathlib.Path(temp) / "sandbox.json"
            self.run_game(path, "init", "test_alice")
            args = types.SimpleNamespace(
                file=str(path), operation="set", viewer="test_alice",
                title=None, at=None, value=30, force=False
            )
            ops.sandbox_main(args)
            self.run_game(path, "game", "test_alice", "Stardew Valley")
            with self.assertRaisesRegex(ops.CheckinError, "Duplicate"):
                self.run_game(path, "game", "test_alice", "stardew   valley")

    def test_reject_replace_does_not_spend_extra_credit(self):
        with tempfile.TemporaryDirectory() as temp:
            path = pathlib.Path(temp) / "sandbox.json"
            self.run_game(path, "init", "test_alice")
            ops.sandbox_main(types.SimpleNamespace(
                file=str(path), operation="set", viewer="test_alice",
                title=None, at=None, value=10, force=False
            ))
            self.run_game(path, "game", "test_alice", "Bad Game")
            self.run_game(path, "reject", "test_alice")
            state = self.run_game(path, "game", "test_alice", "Good Game")
            self.assertEqual(state["users"]["test_alice"]["spent"], 1)
            self.assertEqual(state["wheel"][0]["replacedBy"], state["wheel"][1]["id"])
            self.assertEqual(state["wheel"][0]["slotId"], state["wheel"][1]["slotId"])

    def test_replacements_count_once_in_reconciliation(self):
        data = fixture()
        entries = json.loads(data["globals"]["ci_wheel_month_2026_11"])
        entries[0].update({"status": "rejected", "replacedBy": "entry-3"})
        entries.append({"id": "entry-3", "slotId": "entry-1", "userId": "1234",
                        "game": "Different Game", "month": "2026-11", "status": "pending"})
        data["globals"]["ci_wheel_month_2026_11"] = json.dumps(entries)
        report = ops.reconcile(data)
        alice = next(u for u in report["users"] if u["userId"] == "1234")
        self.assertEqual(alice["ledgerEntries"], 1)
        self.assertFalse(any("exceed used" in x for x in report["findings"]))


if __name__ == "__main__":
    unittest.main()
