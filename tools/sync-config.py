#!/usr/bin/env python3
"""Synchronize generated milestone arrays and verify command definitions.

config/checkins.json is the source of truth for milestone values.
  python tools/sync-config.py          # rewrite C# milestone arrays
  python tools/sync-config.py --check  # CI: detect divergence, do not write

Commands must still be registered in the Streamer.bot UI manually.
"""
import argparse
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
CONFIG = ROOT / "config/checkins.json"
ACTIONS = ["02-checkin.cs", "04-mycheckins.cs", "05-admin-checkins.cs"]
INT_RE = re.compile(r"(private\s+static\s+readonly\s+int\[\]\s+Thresholds\s*=\s*\{).*?(\};)", re.S)
STR_RE = re.compile(r"(private\s+static\s+readonly\s+string\[\]\s+Badges\s*=\s*\{).*?(\};)", re.S)


def render(text, milestones):
    counts = ", ".join(str(item["count"]) for item in milestones)
    names = ",\n        ".join(json.dumps(item["badge"], ensure_ascii=False) for item in milestones)
    text, n = INT_RE.subn(lambda m: m.group(1) + " " + counts + " " + m.group(2), text)
    if n != 1:
        raise ValueError("Expected exactly one Thresholds array")
    text, n = STR_RE.subn(lambda m: m.group(1) + "\n        " + names + "\n    " + m.group(2), text)
    if n != 1:
        raise ValueError("Expected exactly one Badges array")
    return text


def run(check=False):
    cfg = json.loads(CONFIG.read_text(encoding="utf-8"))
    milestones = cfg["milestones"]
    counts = [m["count"] for m in milestones]
    if len(counts) != len(set(counts)) or counts != sorted(counts) or min(counts) <= 0:
        raise ValueError("Milestones must be distinct ascending positive counts")
    commands = cfg["commands"]
    names = [c["name"] for c in commands]
    if len(names) != len(set(names)):
        raise ValueError("Duplicate chat command names")
    if cfg["reward"]["creditsEvery"] != 10:
        raise ValueError("C# credit reward currently assumes every 10 check-ins")
    for cmd in commands:
        if not (ROOT / "src/actions" / cmd["action"]).exists():
            raise ValueError("Missing command action " + cmd["action"])
        if cmd["access"] not in ("everyone", "moderator") or cmd["match"] not in ("exact", "starts-with"):
            raise ValueError("Invalid command permissions or match")
    changed = []
    for name in ACTIONS:
        path = ROOT / "src/actions" / name
        original = path.read_text(encoding="utf-8")
        desired = render(original, milestones)
        if desired != original:
            changed.append(name)
            if not check:
                path.write_text(desired, encoding="utf-8")
    if check and changed:
        raise ValueError("Generated C# arrays out of sync: " + ", ".join(changed))
    return changed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    try:
        changed = run(args.check)
        print("Config synced" if changed else "Config verified")
        return 0
    except (ValueError, KeyError, OSError) as exc:
        print("Config error: " + str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
