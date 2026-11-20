"""Tests for REAL Python snapshot/sandbox/reconciliation tooling (not C# runtime)."""
import importlib.util
import json
import pathlib
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("checkin_ops", ROOT / "tools/checkin-ops.py")
ops = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ops)


def sample():
    return {
        "format": "vtuber-checkins-backup",
        "schemaVersion": 1,
        "globals": {
            "ci_stream_id": 10,
            "ci_stream_count": 1,
            "ci_wheel_legacy_migrated": True,
            "ci_wheel_month_2026_11": json.dumps([
                {"id": "entry-1", "userId": "12345", "game": "A", "month": "2026-11", "removed": False},
                {"id": "entry-2", "userId": "12345", "game": "B", "month": "2026-11", "removed": True}
            ])
        },
        "users": [{
            "userId": "12345", "userName": "Example",
            "vars": {"ci_total": 20, "ci_wheel_earned": 2, "ci_wheel_claimed": 2}
        }]
    }


class SnapshotTests(unittest.TestCase):
    def test_valid_backup(self):
        self.assertEqual(ops.check_backup(sample()), (1, 4))

    def test_duplicate_twitch_id_rejected(self):
        data = sample()
        data["users"].append(data["users"][0].copy())
        with self.assertRaises(ops.CheckinError):
            ops.check_backup(data)

    def test_debug_global_rejected(self):
        data = sample()
        data["globals"]["ci_debug_enabled"] = True
        with self.assertRaises(ops.CheckinError):
            ops.check_backup(data)

    def test_bad_user_variable_type_rejected(self):
        data = sample()
        data["users"][0]["vars"]["ci_total"] = "twenty"
        with self.assertRaises(ops.CheckinError):
            ops.check_backup(data)

    def test_legacy_and_monthly_entries_are_deduplicated(self):
        data = sample()
        entries = json.loads(data["globals"]["ci_wheel_month_2026_11"])
        data["globals"]["ci_wheel_entries_json"] = json.dumps(entries)
        report = ops.reconcile(data)
        self.assertEqual(report["wheelEntryCount"], 2)
        self.assertEqual(report["findings"], [])

    def test_missing_credit_claim_detected(self):
        data = sample()
        data["users"][0]["vars"]["ci_wheel_claimed"] = 1
        report = ops.reconcile(data)
        self.assertTrue(any("surviving game submissions" in f for f in report["findings"]))

    def test_sidecar_checksum_mismatch_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path = pathlib.Path(temp) / "checkins.json"
            path.write_text(json.dumps(sample()), encoding="utf-8")
            pathlib.Path(str(path) + ".sha256").write_text("0" * 64 + "  checkins.json\n")
            with self.assertRaises(ops.CheckinError):
                ops.load_backup(path)

    def test_restore_plan_is_read_only(self):
        plan = ops.restore_plan(sample())
        self.assertFalse(plan["writesPerformed"])
        self.assertEqual(plan["usersToRestore"], 1)


class SandboxTests(unittest.TestCase):
    def run_sandbox(self, operation, path, viewer=None, value=None, title=None, force=False):
        class Args:
            pass
        a = Args()
        a.operation = operation
        a.file = str(path)
        a.viewer = viewer
        a.value = value
        a.title = title
        a.force = force
        ops.sandbox_main(a)
        return json.loads(path.read_text(encoding="utf-8"))

    def test_no_live_data_and_duplicate_checkin(self):
        with tempfile.TemporaryDirectory() as temp:
            path = pathlib.Path(temp) / "sandbox.json"
            self.run_sandbox("init", path)
            self.run_sandbox("stream", path)
            x = self.run_sandbox("checkin", path, viewer="test_alice")
            self.assertEqual(x["users"]["test_alice"]["total"], 1)
            with self.assertRaises(ops.CheckinError):
                self.run_sandbox("checkin", path, viewer="test_alice")

    def test_wheel_credit_after_reset(self):
        with tempfile.TemporaryDirectory() as temp:
            path = pathlib.Path(temp) / "sandbox.json"
            self.run_sandbox("init", path)
            self.run_sandbox("set", path, viewer="test_alice", value=20)
            self.run_sandbox("game", path, viewer="test_alice", title="Stardew Valley")
            x = self.run_sandbox("reset", path, viewer="test_alice")
            self.assertEqual(x["users"]["test_alice"]["earned"], 2)
            self.assertEqual(x["users"]["test_alice"]["spent"], 1)

    def test_production_snapshot_not_accepted_as_sandbox(self):
        with tempfile.TemporaryDirectory() as temp:
            path = pathlib.Path(temp) / "live-backup.json"
            path.write_text(json.dumps(sample()), encoding="utf-8")
            with self.assertRaises(ops.CheckinError):
                self.run_sandbox("stream", path)


if __name__ == "__main__":
    unittest.main()
