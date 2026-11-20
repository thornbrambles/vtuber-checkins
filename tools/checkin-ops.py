#!/usr/bin/env python3
"""Offline tools for exported Streamer.bot VTuber check-in snapshots.

No network access, Twitch API, or production Streamer.bot mutations.
Usage:
  python tools/checkin-ops.py verify Documents/vtuber-checkins-backups/checkins-....json
  python tools/checkin-ops.py reconcile backup.json --output report.json
  python tools/checkin-ops.py restore-plan backup.json --output restore-plan.json
  python tools/checkin-ops.py sandbox init --file .sandbox/local.json
  python tools/checkin-ops.py sandbox stream --file .sandbox/local.json
  python tools/checkin-ops.py sandbox checkin --file .sandbox/local.json --viewer alice
  python tools/checkin-ops.py sandbox set --file .sandbox/local.json --viewer alice --value 10
  python tools/checkin-ops.py sandbox game --file .sandbox/local.json --viewer alice --title "Stardew Valley"
  python tools/checkin-ops.py sandbox status --file .sandbox/local.json
"""
from __future__ import annotations

import argparse
import copy
import datetime as dt
import hashlib
import json
import pathlib
import re
import sys
from collections import Counter, defaultdict
from importlib.util import module_from_spec, spec_from_file_location

wheel_spec = spec_from_file_location("wheel_export", pathlib.Path(__file__).with_name("export-wheel.py"))
wheel_export = module_from_spec(wheel_spec)
wheel_spec.loader.exec_module(wheel_export)
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

SCHEMA_VERSION = 1
BACKUP_FORMAT = "vtuber-checkins-backup"
MONTHLY_GAME_LIMIT = 3
USER_INTS = {
    "ci_total", "ci_streak", "ci_last_stream", "ci_wheel_claimed",
    "ci_wheel_earned", "ci_reward_last_stream"
}
USER_STRINGS = {"ci_badge", "ci_last_redemption_id", "ci_wheel_pending_json"}
GLOBAL_INTS = {"ci_stream_id", "ci_stream_count", "ci_schema_version"}
GLOBAL_LONGS = {"ci_last_checkin_at_ms"}
GLOBAL_BOOLS = {"ci_wheel_legacy_migrated"}
MONTH_KEY = re.compile(r"ci_wheel_month_(\d{4})_(0[1-9]|1[0-2])$")
VIEWER_NAME = re.compile(r"^[A-Za-z][A-Za-z0-9_-]{1,30}$")


class CheckinError(ValueError):
    pass


def check_backup(data):
    """Validate user identities and value types before any reconciliation."""
    if not isinstance(data, dict):
        raise CheckinError("Backup must be JSON object")
    if data.get("format") != BACKUP_FORMAT or type(data.get("schemaVersion")) is not int or data["schemaVersion"] != SCHEMA_VERSION:
        raise CheckinError("Wrong backup format or schema version")
    globals_ = data.get("globals")
    users = data.get("users")
    if not isinstance(globals_, dict) or not isinstance(users, list):
        raise CheckinError("globals must be object and users must be array")
    for key, value in globals_.items():
        if not isinstance(key, str) or not key.startswith("ci_") or key == "ci_debug_enabled":
            raise CheckinError("Invalid global variable name: " + str(key))
        if value is None:
            continue
        if key in GLOBAL_INTS or key in GLOBAL_LONGS:
            if type(value) is not int or value < 0:
                raise CheckinError("Global must be nonnegative integer: " + key)
        elif key in GLOBAL_BOOLS:
            if type(value) is not bool:
                raise CheckinError("Global must be bool: " + key)
        elif not isinstance(value, str):
            raise CheckinError("Global must be string: " + key)
    ids = set()
    for user in users:
        if not isinstance(user, dict) or not isinstance(user.get("vars"), dict):
            raise CheckinError("Malformed user record")
        uid = user.get("userId")
        if not isinstance(uid, str) or not uid.isascii() or not uid.isdigit() or uid in ids:
            raise CheckinError("Invalid or duplicate numeric Twitch userId")
        ids.add(uid)
        for key, value in user["vars"].items():
            if value is None:
                continue
            if key in USER_INTS:
                if type(value) is not int or value < 0 or value > 1_000_000_000:
                    raise CheckinError("Invalid integer user variable " + key)
            elif key in USER_STRINGS:
                if not isinstance(value, str):
                    raise CheckinError("Invalid string user variable " + key)
            else:
                raise CheckinError("Unknown user variable " + key)
    return len(users), len(globals_)


def load_backup(path):
    p = pathlib.Path(path)
    if not p.is_file() or p.stat().st_size > 20 * 1024 * 1024:
        raise CheckinError("Missing file or backup is over the 20 MiB limit")
    payload = p.read_bytes()
    checksum_path = pathlib.Path(str(p) + ".sha256")
    if checksum_path.exists():
        first = checksum_path.read_text(encoding="ascii").strip().split()
        if not first or not re.fullmatch(r"[a-fA-F0-9]{64}", first[0]) or first[0].lower() != hashlib.sha256(payload).hexdigest():
            raise CheckinError("SHA-256 checksum mismatch")
    try:
        data = json.loads(payload.decode("utf-8-sig"))
    except (ValueError, UnicodeDecodeError) as exc:
        raise CheckinError("Malformed JSON: " + str(exc)) from exc
    check_backup(data)
    return data


def wheel_entries(globals_):
    """Yield deduplicated entry IDs from both old archive and monthly partitions."""
    all_entries = {}
    problems = []
    for key, raw in globals_.items():
        if key != "ci_wheel_entries_json" and not MONTH_KEY.fullmatch(key):
            continue
        if not raw:
            continue
        try:
            entries = json.loads(raw)
        except (ValueError, TypeError):
            problems.append(f"{key}: not valid JSON")
            continue
        if not isinstance(entries, list):
            problems.append(f"{key}: expected JSON array")
            continue
        for position, entry in enumerate(entries):
            if not isinstance(entry, dict):
                problems.append(f"{key}[{position}]: entry not object")
                continue
            identifier = entry.get("id")
            if not isinstance(identifier, str) or not identifier:
                # Legacy entries sometimes have missing IDs. Keep a stable
                # synthetic ID scoped to the *source* and row position.
                identifier = f"{key}:{position}"
            if identifier in all_entries:
                existing = all_entries[identifier]
                if (existing.get("game"), existing.get("userId")) != (entry.get("game"), entry.get("userId")):
                    problems.append(f"Conflicting entry ID across ledgers: {identifier}")
                continue
            all_entries[identifier] = entry
    return list(all_entries.values()), problems


def reconcile(data):
    """Read-only consistency report. This does not repair or edit any state."""
    users = data["users"]
    globals_ = data["globals"]
    entries, findings = wheel_entries(globals_)
    slots = {(str(entry.get("userId")), str(entry.get("slotId") or entry.get("id")))
             for entry in entries if entry.get("userId")}
    counts = Counter(uid for uid, slot in slots)
    # An older ledger entry without status is approved. Rejected/replaced
    # records still consume one historical slot, not an extra credit.
    approved_titles = {}
    for entry in entries:
        if entry.get("removed") or entry.get("replacedBy") or wheel_export.entry_status(entry) != "approved":
            continue
        title = " ".join(str(entry.get("game", "")).split()).casefold()
        key = (entry.get("month"), title)
        if key in approved_titles:
            findings.append(f"{entry.get('month')}: duplicate approved game title: {title}")
        approved_titles[key] = entry.get("id")
    ids = {u["userId"] for u in users}
    user_rows = []
    for u in users:
        uid = u["userId"]
        values = u["vars"]
        total = values.get("ci_total", 0) or 0
        earned = values.get("ci_wheel_earned", 0) or 0
        spent = values.get("ci_wheel_claimed", 0) or 0
        entry_count = counts.get(uid, 0)
        if earned < spent:
            findings.append(f"{uid}: spent credits ({spent}) exceed earned credits ({earned})")
        # Some v3 backups may have no high-water counter yet. Report as an
        # expected migration step, not corruption.
        if earned < total // 10:
            findings.append(f"{uid}: earned credits ({earned}) below current total/10 ({total//10}); initialize high-water")
        if entry_count > spent:
            findings.append(f"{uid}: {entry_count} surviving game submissions exceed used credits ({spent}); migration/recovery needed")
        pending = values.get("ci_wheel_pending_json")
        if pending:
            findings.append(f"{uid}: unfinished pending submission; recover before accepting more games")
        user_rows.append({
            "userId": uid, "userName": u.get("userName", ""),
            "total": total, "earned": earned, "spent": spent,
            "available": max(0, earned - spent), "ledgerEntries": entry_count
        })
    for uid in counts.keys() - ids:
        findings.append(f"{uid}: game submissions present, but no saved viewer record")
    for e in entries:
        if not e.get("game") or not e.get("userId") or not e.get("month"):
            findings.append("Wheel entry missing game/userId/month: " + str(e.get("id")))
    return {
        "format": "vtuber-checkins-reconciliation",
        "schemaVersion": 1,
        "viewerCount": len(users), "wheelEntryCount": len(entries),
        "findings": findings, "users": user_rows
    }


def restore_plan(data):
    """Describe the restore; intentionally never generate commands that write live data."""
    return {
        "format": "vtuber-checkins-restore-plan",
        "schemaVersion": 1,
        "globalsToRestore": sorted(data["globals"].keys()),
        "usersToRestore": len(data["users"]),
        "userVariablesToRestore": sum(len(u["vars"]) for u in data["users"]),
        "writesPerformed": False,
        "note": "Use manual action 10-import-data.cs, dry-run first, after taking a destination backup."
    }


def sandbox_main(args):
    """Standalone synthetic model, separate from Streamer.bot persisted variables."""
    p = pathlib.Path(args.file)
    if args.operation == "init":
        if p.exists() and not args.force:
            raise CheckinError("Sandbox file exists; pass --force only if you intend to replace it")
        p.parent.mkdir(parents=True, exist_ok=True)
        state = {"format": "vtuber-checkins-sandbox", "stream": 0, "users": {}, "wheel": []}
    else:
        if not p.exists():
            raise CheckinError("Sandbox not initialized. Run sandbox init first")
        state = json.loads(p.read_text(encoding="utf-8"))
        if state.get("format") != "vtuber-checkins-sandbox":
            raise CheckinError("Refusing to write a file without sandbox format marker")
    op = args.operation
    if op == "stream":
        state["stream"] += 1
    elif op in {"checkin", "game", "approve", "reject", "set", "reset"}:
        name = args.viewer or ""
        if not VIEWER_NAME.fullmatch(name):
            raise CheckinError("Sandbox viewer should be a synthetic name, e.g. test_alice")
        viewer = state["users"].setdefault(name, {
            "total": 0, "lastStream": 0, "streak": 0, "earned": 0, "spent": 0
        })
        if op == "checkin":
            if state["stream"] <= 0:
                raise CheckinError("Start a synthetic stream first")
            if viewer["lastStream"] == state["stream"]:
                raise CheckinError("Duplicate check-in rejected")
            viewer["streak"] = viewer["streak"] + 1 if viewer["lastStream"] == state["stream"] - 1 else 1
            viewer["lastStream"] = state["stream"]
            viewer["total"] += 1
        elif op == "set":
            if args.value is None or args.value < 0:
                raise CheckinError("set requires --value nonnegative")
            viewer["total"] = args.value
            if args.value == 0:
                viewer["streak"] = 0
                viewer["lastStream"] = 0
        elif op == "reset":
            viewer["total"] = viewer["streak"] = viewer["lastStream"] = 0
        elif op in {"game", "approve", "reject"}:
            now = dt.datetime.fromisoformat(args.at.replace("Z", "+00:00")) if getattr(args, "at", None) else dt.datetime.now(dt.timezone.utc)
            if now.tzinfo is None:
                raise CheckinError("--at needs a timezone offset")
            try:
                eastern = ZoneInfo("America/New_York")
            except ZoneInfoNotFoundError:
                eastern = dt.timezone.utc
                print("WARNING: IANA timezone data unavailable; using UTC in sandbox", file=sys.stderr)
            month = now.astimezone(eastern).strftime("%Y-%m")
            if op in {"approve", "reject"}:
                matches = [entry for entry in state["wheel"] if entry["user"] == name and
                           entry["month"] == month and entry.get("status") == "pending"]
                if not matches:
                    raise CheckinError("No pending game for this viewer/month")
                matches[0]["status"] = "approved" if op == "approve" else "rejected"
            else:
                if not args.title or not 2 <= len(args.title) <= 100:
                    raise CheckinError("game needs a title between 2 and 100 characters")
                current = [entry for entry in state["wheel"] if entry["month"] == month]
                rejected = next((entry for entry in current if entry["user"] == name and
                                 entry.get("status") == "rejected" and not entry.get("replacedBy")), None)
                normalized = " ".join(args.title.split()).casefold()
                if any(" ".join(entry["game"].split()).casefold() == normalized and
                       wheel_export.entry_status(entry) in ("approved", "pending") and
                       not entry.get("removed") for entry in current):
                    raise CheckinError("Duplicate game title this month")
                slots = {entry.get("slotId", entry["id"]) for entry in current if entry["user"] == name}
                if rejected is None and len(slots) >= MONTHLY_GAME_LIMIT:
                    raise CheckinError("Monthly game submission limit reached; credits carry over")
                if rejected is None and viewer["earned"] <= viewer["spent"]:
                    raise CheckinError("No game credits available")
                identifier = f"sandbox-{len(state['wheel'])+1}"
                if rejected:
                    rejected["replacedBy"] = identifier
                else:
                    viewer["spent"] += 1
                state["wheel"].append({
                    "id": identifier,
                    "slotId": rejected.get("slotId", rejected["id"]) if rejected else identifier,
                    "user": name, "game": args.title, "month": month,
                    "status": "pending", "utc": now.isoformat()
                })
        viewer["earned"] = max(viewer["earned"], viewer["spent"], viewer["total"] // 10)
    if op != "status":
        p.write_text(json.dumps(state, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(state, indent=2, sort_keys=True))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for operation in ("verify", "reconcile", "restore-plan"):
        cmd = sub.add_parser(operation)
        cmd.add_argument("backup")
        if operation != "verify":
            cmd.add_argument("--output")
    sandbox = sub.add_parser("sandbox")
    sandbox.add_argument("operation", choices=["init", "stream", "checkin", "set", "reset", "game", "status"])
    sandbox.add_argument("--file", default=".sandbox/checkins.json")
    sandbox.add_argument("--viewer")
    sandbox.add_argument("--value", type=int)
    sandbox.add_argument("--title")
    sandbox.add_argument("--at", help="Synthetic ISO time with timezone (e.g. 2026-12-01T00:02:00-05:00)")
    sandbox.add_argument("--force", action="store_true")
    args = parser.parse_args(argv)
    try:
        if args.command == "sandbox":
            sandbox_main(args)
            return 0
        data = load_backup(args.backup)
        if args.command == "verify":
            print(f"Verified backup: {len(data['users'])} viewers, {len(data['globals'])} globals; checksum checked if sidecar present")
            return 0
        result = reconcile(data) if args.command == "reconcile" else restore_plan(data)
        serialized = json.dumps(result, indent=2) + "\n"
        if args.output:
            pathlib.Path(args.output).write_text(serialized, encoding="utf-8")
        else:
            print(serialized)
        return 2 if args.command == "reconcile" and result["findings"] else 0
    except (CheckinError, ValueError, OSError) as exc:
        print("ERROR: " + str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
