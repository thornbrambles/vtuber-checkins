#!/usr/bin/env python3
"""Validate a VTuber Check-ins v3+ exported backup without modifying it.

Usage: python tools/validate-backup.py path/to/checkins-YYYYMMDD-HHMMSS.json

The backup contains private Twitch user IDs and names; do not commit or upload
it to public services.
"""
import json
import sys
from pathlib import Path

INT_FIELDS = {
    "ci_total", "ci_streak", "ci_last_stream", "ci_wheel_claimed",
    "ci_wheel_earned", "ci_reward_last_stream"
}
STRING_FIELDS = {
    "ci_badge", "ci_last_redemption_id", "ci_wheel_pending_json"
}


def validate(data):
    if not isinstance(data, dict) or data.get("format") != "vtuber-checkins-backup":
        raise ValueError("wrong backup format")
    if type(data.get("schemaVersion")) is not int or data["schemaVersion"] != 1:
        raise ValueError("unsupported schema version")
    globals_ = data.get("globals")
    users = data.get("users")
    if not isinstance(globals_, dict) or not isinstance(users, list):
        raise ValueError("globals must be an object, users must be an array")
    for key, value in globals_.items():
        if not isinstance(key, str) or not key.startswith("ci_"):
            raise ValueError("unexpected global key")
        if value is not None and not isinstance(value, (str, int, bool)):
            raise ValueError(f"global {key} has non-scalar type")
        if key == "ci_debug_enabled":
            raise ValueError("debug setting should not be imported")
    ids = set()
    count = 0
    for user in users:
        if not isinstance(user, dict) or not isinstance(user.get("vars"), dict):
            raise ValueError("malformed user record")
        uid = user.get("userId")
        if not isinstance(uid, str) or not uid.isdecimal() or uid in ids:
            raise ValueError("missing, invalid, or duplicate Twitch user ID")
        ids.add(uid)
        for name, value in user["vars"].items():
            if value is None:
                continue
            if name in INT_FIELDS:
                if type(value) is not int or not 0 <= value <= 1_000_000_000:
                    raise ValueError(f"invalid value for {name}")
            elif name in STRING_FIELDS:
                if not isinstance(value, str):
                    raise ValueError(f"invalid string for {name}")
            else:
                raise ValueError(f"unknown user variable {name}")
            count += 1
    return len(globals_), len(users), count


def main():
    if len(sys.argv) != 2:
        sys.exit("Usage: python tools/validate-backup.py BACKUP.json")
    path = Path(sys.argv[1])
    if path.stat().st_size > 20 * 1024 * 1024:
        sys.exit("Backup exceeds importer size limit (20 MiB)")
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        globals_n, users_n, vars_n = validate(data)
    except (ValueError, OSError, json.JSONDecodeError) as exc:
        sys.exit(f"Invalid backup: {exc}")
    print(f"Valid backup: {users_n} Twitch users, {vars_n} user variables, {globals_n} ci_ globals")
    print("Validation only; no Streamer.bot variables were modified.")


if __name__ == "__main__":
    main()
