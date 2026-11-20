"""Ensure final wheel exports include approved games only and block pending ones."""
import csv
import importlib.util
import json
import pathlib
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("wheel_export", ROOT/"tools"/"export-wheel.py")
wheel = importlib.util.module_from_spec(spec)
spec.loader.exec_module(wheel)


class WheelExportTests(unittest.TestCase):
    def test_legacy_approval_accepts_null_blank_and_missing_status(self):
        rows = [{"month": "2026-11", "game": "Missing"},
                {"month": "2026-11", "game": "Null", "status": None},
                {"month": "2026-11", "game": "Blank", "status": "  "}]
        self.assertEqual(wheel.eligible_entries(rows, "2026-11"), rows)

    def test_backup_with_journal_but_no_ledger_row_is_not_final(self):
        source = {"format": "vtuber-checkins-backup", "globals": {}, "users": [
            {"vars": {"ci_wheel_pending_json": json.dumps({"entry": {"month": "2026-11"}})}}]}
        with self.assertRaisesRegex(ValueError, "unfinished game submissions"):
            wheel.eligible_entries(source, "2026-11")
        self.assertEqual(wheel.eligible_entries(source, "2026-10"), [])

    def setUp(self):
        self.entries = [
            {"id": "a", "game": "Good Game", "user": "alice", "date": "2026-11-21",
             "month": "2026-11", "status": "approved"},
            {"id": "b", "game": "Waiting", "user": "bob", "date": "2026-11-22",
             "month": "2026-11", "status": "pending"},
            {"id": "c", "game": "Rejected", "user": "carl", "date": "2026-11-23",
             "month": "2026-11", "status": "rejected"},
            {"id": "d", "game": "Removed", "user": "dana", "date": "2026-11-24",
             "month": "2026-11", "removed": True},
            {"id": "e", "game": "Old Game", "user": "erin", "date": "2026-10-01",
             "month": "2026-10"}
        ]

    def test_pending_denies_final_export(self):
        with self.assertRaisesRegex(ValueError, "pending"):
            wheel.eligible_entries(self.entries, "2026-11")

    def test_approved_only_export(self):
        with tempfile.TemporaryDirectory() as temp:
            path = pathlib.Path(temp) / "wheel.csv"
            self.assertEqual(wheel.export(self.entries, "2026-11", path, allow_pending=True), 1)
            with path.open(encoding="utf-8-sig") as file:
                rows = list(csv.DictReader(file))
            self.assertEqual([x["game"] for x in rows], ["Good Game"])

    def test_snapshot_and_archive_formats(self):
        data = {"format": "vtuber-checkins-backup",
                "globals": {"ci_wheel_month_2026_11": json.dumps(self.entries)}}
        self.assertEqual(len(wheel.eligible_entries(data, "2026-11", True)), 1)
        archive = {"month": "2026-11", "pending": 0,
                   "eligible": [{"game": "A", "user": "alice", "date": "2026-11-21"}]}
        self.assertEqual(len(wheel.eligible_entries(archive, "2026-11")), 1)
        archive["pending"] = 1
        with self.assertRaises(ValueError):
            wheel.eligible_entries(archive, "2026-11")

    def test_csv_injection_escape(self):
        self.assertEqual(wheel.safe_field("=cmd()"), "'=cmd()")
        self.assertEqual(wheel.safe_field("Game"), "Game")


if __name__ == "__main__":
    unittest.main()
