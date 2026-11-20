#!/usr/bin/env python3
"""Export approved games from a raw ledger, backup, or monthly archive."""
import argparse
import csv
import json
import re
from pathlib import Path

def eligible_entries(source, month, allow_pending=False):
    if isinstance(source, list):
        entries = source
    elif isinstance(source, dict) and source.get("format") == "vtuber-checkins-backup":
        stored = source["globals"].get("ci_wheel_month_" + month.replace("-", "_"))
        entries = json.loads(stored) if stored else []
    elif isinstance(source, dict) and source.get("month") == month and isinstance(source.get("eligible"), list):
        if source.get("pending", 0) and not allow_pending:
            raise ValueError("The archive contains unreviewed games; finish moderation first")
        return source["eligible"]
    else:
        raise ValueError("Expected raw monthly JSON, a check-in backup, or a wheel archive")
    if not isinstance(entries, list) or any(not isinstance(e, dict) for e in entries):
        raise ValueError("The monthly ledger must contain an array of entries")
    if not allow_pending and any(e.get("month") == month and e.get("status") == "pending"
                                 and not e.get("removed") and not e.get("replacedBy") for e in entries):
        raise ValueError("Some game submissions are still pending approval")
    return [e for e in entries if e.get("month") == month and not e.get("removed")
            and not e.get("replacedBy") and e.get("status", "approved") == "approved"]

def safe_field(value):
    text = str(value or "")
    return "'" + text if text.lstrip().startswith(("=", "+", "-", "@", "\t", "\r")) else text

def export(source, month, output, allow_pending=False):
    rows = eligible_entries(source, month, allow_pending)
    with Path(output).open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=["game", "user", "date"])
        writer.writeheader()
        for entry in rows:
            writer.writerow({key: safe_field(entry.get(key)) for key in ("game", "user", "date")})
    return len(rows)

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("file")
    p.add_argument("month")
    p.add_argument("--output")
    p.add_argument("--allow-pending", action="store_true")
    args = p.parse_args()
    if not re.fullmatch(r"\d{4}-(0[1-9]|1[0-2])", args.month):
        p.error("Month must be YYYY-MM")
    try:
        source = json.loads(Path(args.file).read_text(encoding="utf-8-sig"))
        output = args.output or f"wheel-{args.month}.csv"
        count = export(source, args.month, output, args.allow_pending)
    except (ValueError, OSError, KeyError) as exc:
        p.error(str(exc))
    print(f"Exported {count} approved games to {output}")

if __name__ == "__main__":
    main()
