#!/usr/bin/env python3
"""Output read-only versioned migration instructions based on an exported snapshot.

Usage: python tools/plan-migration.py BACKUP.json --from-version 3
"""
import argparse
import importlib.util
import json
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location("checkin_ops", ROOT/"tools"/"checkin-ops.py")
ops=importlib.util.module_from_spec(spec)
spec.loader.exec_module(ops)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("backup")
    parser.add_argument("--from-version",type=int,choices=[3,4])
    args=parser.parse_args()
    data=ops.load_backup(args.backup)
    manifest=json.loads((ROOT/"config"/"migrations.json").read_text(encoding="utf-8"))
    source=args.from_version
    if source is None:
        stored=data["globals"].get("ci_schema_version")
        if stored not in (3,4):
            parser.error("Specify --from-version 3 or 4; older deployments might not have ci_schema_version")
        source=stored
    matches=[m for m in manifest["migrations"] if m["from"]==source and m["to"]==manifest["targetSchema"]]
    if len(matches)!=1:
        parser.error("No migration plan for that source version")
    plan=matches[0]
    legacy=data["globals"].get("ci_wheel_entries_json")
    info={
        "from":source,
        "to":manifest["targetSchema"],
        "migrationId":plan["id"],
        "manualAction":plan["action"],
        "requiresBackup":plan["requiresBackup"],
        "legacyWheelPresent":bool(legacy),
        "viewerCount":len(data["users"]),
        "steps":[
            "Back up full Streamer.bot data and verify exported snapshot",
            "Disable legacy !game command and any live redemptions during migration",
            "Execute migration action once in blocking queue",
            "Verify ci_wheel_legacy_migrated=true and ci_schema_version=5",
            "Deploy updated actions and validate counts with !checkins, !wheel, !checkindiag",
            "Run CI tests plus a live Streamer.bot compile and test offline"
        ],
        "writesPerformed":False
    }
    print(json.dumps(info,indent=2))


if __name__=="__main__":
    main()
