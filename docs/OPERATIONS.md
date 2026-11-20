# Operations: production, backup, staging, diagnostics and releases

This system uses **Streamer.bot persisted variables as production state**.
No separate database or daemon is installed. The standalone Python tools do
not have access to the Streamer.bot process; they operate on exported JSON files.

## Production storage map (schema 5)

### Twitch user variables (persisted)

- ci_total — lifetime check-in count
- ci_streak — consecutive-stream streak
- ci_last_stream — last logical stream checked in
- ci_reward_last_stream — immutable per-stream redemption anti-duplicate marker
- ci_last_redemption_id — most recently accepted Twitch redemption ID
- ci_badge — earned milestone badge
- ci_wheel_earned — monotonic highest number of earned game credits
- ci_wheel_claimed — cumulative credits consumed; NEVER reset to zero
- ci_wheel_pending_json — a recovery record for an in-progress game submission

### Global variables (persisted)

- ci_stream_key, ci_stream_id, ci_stream_count, ci_last_checkin_at_ms — stream/session state
- ci_overlay_json — derived OBS leaderboard snapshot
- ci_schema_version — data model revision, currently 5
- ci_wheel_legacy_migrated — migration completion flag
- ci_wheel_entries_json — legacy archive (read only after migration)
- ci_wheel_month_YYYY_MM — JSON arrays of game submissions (including soft-deleted entries); these enforce the **three submissions per viewer per Eastern Time calendar month** limit
- ci_wheel_audit_json — wheel moderator edits (approve/reject/rename/remove/restore)
- ci_wheel_spin_result_YYYY_MM — authoritative saved result for each drawn month (persisted, immutable until broadcaster reset)
- ci_wheel_spin_overlay_json — transient OBS wheel presentation data; expires after ~18 seconds
- ci_wheel_spin_audit_json — draw/reset history
- ci_wheel_spin_last_announced_id — guard against duplicate winner chat announcements
- ci_events_month_YYYY_MM — check-in/admin/game event audit history (new events only)
- ci_diagnostics_json, ci_reconciliation_json — last machine-readable health reports

Historical v3 attendance does **not** have a per-check-in audit trail; initial
v3 counters are authoritative baseline at upgrade. Events recorded after the
upgrade can be reconciled against the exported baseline.

## Actions to add

Keep all core actions in the shared BLOCKING queue Community Checkins. Manual
maintenance actions should also be assigned to this queue.

| Action file | Manual / triggered | Purpose |
| --- | --- | --- |
| 01–07 | See INSTALLATION.md | Runtime stream check-ins, chat queries and wheel submissions |
| 08-migrate-legacy-wheel.cs | Manual once, no trigger | Mark/convert legacy wheel data and set schemaVersion 5 |
| 09-export-data.cs | Manual OR private local timed event | Export state, SHA-256 sidecar, retain 30 most recent local snapshots |
| 10-import-data.cs | Manual only | Validate import; safe dry-run default, explicit apply/overwrite switches |
| 11-debug-admin.cs | Manual only | Edit REAL viewer records while offline, opt-in and dry-run by default |
| 12-diagnostics.cs | Manual OR private local timed event | Read-only checks and JSON findings |
| 13-wheel-admin.cs | Twitch command: !wheeladmin | Mod/broadcaster rename/remove/restore wheel entries; audit logging |
| 14-reconcile.cs | Manual OR private local timed event | Read-only ledger-versus-credit report |
| 15-diagnostic-command.cs | Twitch command: !checkindiag | Restricted moderator diagnostic chat summary |
| 16-month-rollover.cs | Local timed trigger (e.g. daily 00:10 Eastern) | Safely regenerate prior-month approved wheel archive, with pending/rejected counts |
| 17-wheel-spin.cs | Moderator/broadcaster command: !spinwheel | Secure one-time draw and publish animation state for a past month |
| 18-wheel-reveal.cs | Private local timed trigger every ~2 seconds | At-most-once Twitch chat announcement after the 10-second animation |

Do not attach a Twitch chat trigger to any backup, restore, migration, debug,
diagnostic, or reconciliation action. A timed event is appropriate only for
export and read-only reports.

**Admin wheel permissions:** In Streamer.bot create !wheeladmin as Starts With,
allowed groups Moderator/broadcaster only. The action also verifies caller
authority. Available subcommands:

- !wheeladmin list [YYYY-MM] — up to three IDs in chat, all IDs in local log
- !wheeladmin approve ID — approve a pending submission for the visible wheel
- !wheeladmin reject ID — reject a game; the viewer can submit a replacement in the same month without another credit
- !wheeladmin rename ID New Game Name — edit a game title without spending another credit
- !wheeladmin remove ID — soft-delete from public wheel; preserve historical entry and spent credit
- !wheeladmin restore ID — restore a soft-deleted entry to current wheel

Submitted games start pending and are hidden from `!wheel list` and the spin-ready archive until approved. Case-insensitive, whitespace-normalized duplicate titles are blocked. Soft-deleted and rejected slots still count toward the three-per-month limit; a rejected game may be replaced during the **same calendar month** without using another credit, and replacement records share the original slot ID. Unused credits carry forward. This intentionally does **not** refund credits for game removals. Refunds need
a separately audited administrative policy.

## Month rollover and wheel archival

On the 1st of the month, the date-scoped ledger automatically switches to the new Eastern calendar month. The original month stays on each pending transaction and is used during recovery, even after midnight or daylight-saving changes. Incoming Twitch message IDs are checked against **all monthly partitions**, so a retried message cannot spend again after rollover.

Set up `16-month-rollover.cs` as a local timed action on the shared **Blocking** queue, ideally daily at **00:10 Eastern** (also run once when enabling it). It recomputes an idempotent `ci_wheel_archive_YYYY_MM` JSON snapshot for the previous month, containing **only approved, non-removed games** plus counts of pending and rejected submissions. It does **not** silently pick a winner or interact with the OBS overlay. The moderator can export the approved month with `tools/export-wheel.py` for their preferred spinning tool. For example: `python tools/export-wheel.py backup.json 2026-11` after exporting a fresh backup. The script also accepts a single monthly JSON array or the `ci_wheel_archive_YYYY_MM` JSON object exported separately. It refuses to generate a final spin CSV while pending submissions remain unless explicitly overridden with `--allow-pending`. If a late moderation decision changes an older month, manually re-run the archival action with `targetMonth=YYYY-MM`.

Do not draw winners while an archive still reports pending games. Month-end exports should be backed up with other `ci_` globals.

## Live monthly wheel drawing (separate OBS overlay)

Install **CI - Spin Wheel** from `17-wheel-spin.cs` with `!spinwheel` Starts With matching, restricted to Moderator/broadcaster. Install **CI - Wheel Reveal** from `18-wheel-reveal.cs` as a local timed action every approximately two seconds (in the Community Checkins Blocking queue, not a chat command). Add `overlay/game-wheel-overlay.html` to OBS as a second **Local File Browser Source**, **1920×1080**, above gameplay. Leave it enabled: transparent unless a spin is active. It does not modify the existing check-in overlay.

`!spinwheel` loads the previous Eastern Time calendar month; `!spinwheel YYYY-MM` chooses another **past** month. It validates all entries, refuses to draw if any remain pending, uses `RandomNumberGenerator.GetInt32` to select a uniform random entry, **saves the result before animating**, and publishes the OBS state. Each approved submission has one equal chance. Once drawn, the month cannot be spun again accidentally. The wheel spins about **10 seconds**, reveals the saved winner for ~8 seconds, then hides. The separate timed reveal action posts the winning game in chat after the animation. No OBS animations or additional overlays appear at other times.

`!spinwheel replay YYYY-MM` replays **the same winner** without charging credits or reannouncing in chat. `!spinwheel hide` hides immediately. Only the broadcaster can issue `!spinwheel reset YYYY-MM RESET-YYYY-MM` to clear the saved result and allow a new draw; the prior result and reset are retained in an audit trail. Take a backup before resetting.

The overlay reads `ci_wheel_spin_overlay_json` using the same localhost Streamer.bot WebSocket endpoint as the existing check-in overlay. Do not expose unauthenticated WebSocket control over the network. This spin action is not tied to viewers spending credits; the wheel consumes only approved submissions already recorded in the monthly ledger.

**Testing:** JavaScript geometry, hidden/expiry behavior and valid saved results are checked through the Node test runner in GitHub Actions. Stubbed C# compilation covers the action scripts. A live Streamer.bot + OBS acceptance test is still required; see [STREAMERBOT-ACCEPTANCE.md](STREAMERBOT-ACCEPTANCE.md).

## Backups

The export action writes checkins-TIMESTAMP.json and the corresponding
checkins-TIMESTAMP.json.sha256 in the local Documents/vtuber-checkins-backups
directory. It retains the most recent 30 snapshot pairs. Copy a periodic
snapshot to separate secure storage; local rotation is not an off-machine backup.

For automatic snapshots, add a Streamer.bot **scheduled/timed action trigger**
to the *export* action at an off-stream time. The schedule is configured in
the Streamer.bot UI; no service timer is bundled in this repository. Verify
that the action has permission to write its local backup directory.

At least once after each upgrade, verify a JSON snapshot:

    python tools/checkin-ops.py verify "C:/Users/USER/Documents/vtuber-checkins-backups/checkins-YYYYMMDD-HHMMSS-fff.json"

The validator checks the hash when the .sha256 sidecar is next to the file,
schema, identity types and supported per-user keys. A good checksum does not
guarantee that the data was semantically correct before exporting.

To compare ledger data against counters:

    python tools/checkin-ops.py reconcile backup.json --output reconcile.json

Return codes: 0 = clean, 2 = findings, 1 = invalid input. A v3 snapshot may
show expected high-water warnings; these should be reviewed as migration
tasks, not silently overwritten.

Generate a read-only restore plan:

    python tools/checkin-ops.py restore-plan backup.json --output restore-plan.json

Restoration into another Streamer.bot instance uses 10-import-data.cs and
requires a *separate destination backup*, dry-run validation and explicit
opt-in. For in-place upgrades, do **not** import: keep the existing persisted
state and the export as rollback protection.

## Real offline debug versus sandbox

### Real data edits (manual Streamer.bot action)

11-debug-admin.cs lets you view/set/add/remove/reset/streak a specific numeric
Twitch user ID without an active stream. It never fabricates a Channel Points
redemption. DebugEnabled=false and DryRun=true by default. Edits are persistent
and auditable through Streamer.bot logs. Disable the action when finished.

### Fully isolated synthetic sandbox (Python)

The following never touches Streamer.bot variables or Twitch:

    python tools/checkin-ops.py sandbox init --file .sandbox/demo.json
    python tools/checkin-ops.py sandbox stream --file .sandbox/demo.json
    python tools/checkin-ops.py sandbox checkin --file .sandbox/demo.json --viewer test_alice
    python tools/checkin-ops.py sandbox set --file .sandbox/demo.json --viewer test_alice --value 20
    python tools/checkin-ops.py sandbox game --file .sandbox/demo.json --viewer test_alice --title "Stardew Valley"
    python tools/checkin-ops.py sandbox reset --file .sandbox/demo.json --viewer test_alice
    python tools/checkin-ops.py sandbox status --file .sandbox/demo.json

For **real C# end-to-end testing**, use a separate portable Streamer.bot
installation with a separate state directory and test Twitch bot/account.
Copy only sanitized fixture values. The Python sandbox is a rules model, not
an emulator of Streamer.bot's C# runtime and OAuth/OBS integrations.

## Local admin dashboard

Start it with a **verified, private** export JSON. The dashboard binds only to `127.0.0.1` and prints a random access-token URL:

    python tools/admin-dashboard.py --backup "C:/Users/USER/Documents/vtuber-checkins-backups/checkins-YYYYMMDD-HHMMSS-fff.json"

It lets you view check-in totals/credits and monthly wheel entries, stage total/streak edits, and approve/reject games. Its **Save staged JSON + SHA-256** operation creates a **new** snapshot under `.sandbox/staged/` while leaving the original unchanged. A new file is never applied automatically. To update production, first export/backup live Streamer.bot, manually inspect the staged changes, copy the staged JSON and checksum to the import action's expected `import.json` path, run import dry-run, then explicitly apply. Importing overwrites configured records and can conflict with changes made during the stream: do it only when the bot is stopped or Twitch submissions are disabled. Audit events are included with the staged backup. The dashboard is **not a live API connection** and is not a network-accessible moderator portal.

## Diagnostics and reconciliation

Run 12-diagnostics.cs manually (or schedule read-only checks) when stream
commands appear wrong. It reports invalid globals, bad OBS JSON, negative
counters, duplicate wheel IDs, and unresolved pending submissions. Its
JSON output is in ci_diagnostics_json; details go to the local Streamer.bot
log, not Twitch chat.

Run 14-reconcile.cs to compare counts of unique surviving game entries to
earned/spent credit counters. Findings go into ci_reconciliation_json. Both
actions are read-only except for saving their report variables.

If a pending game submission exists, a viewer can run !addgame with no title to
retry recovery. Do not manually zero pending records without matching the
wheel entry and consumed credit first.

## Configuration: single version-controlled source

config/checkins.json is the canonical source for milestone thresholds, badges
and intended command registry. Changing a milestone requires:

    python tools/sync-config.py
    python tools/sync-config.py --check

This rewrites milestone arrays in the three standalone C# actions and validates
the registry. It does NOT create commands in your Streamer.bot application.
Keep the UI commands/triggers matched to the generated command registry.

## Versioned migration plan

Migration definitions are recorded in `config/migrations.json`. To preview the path from an existing **v3** backup without modifying data:

    python tools/plan-migration.py backup.json --from-version 3

Or use `--from-version 4` for deployed v4. The JSON export schemaVersion (currently 1) and application data `ci_schema_version` (currently 5) are different version numbers.

## CI and releases

A separate C# compile job uses documented API-shaped stubs and the .NET SDK to catch source-level errors, but it does **not** compile against Streamer.bot's real assemblies. Live C# compile and Twitch redemption testing remain necessary.

Python unit tests cover the credit accounting model, JSON backup verification,
reconciliation and synthetic sandbox. GitHub Actions also checks milestone
synchronization and script syntax.

Version tags such as v5.0.0 produce a release package on GitHub containing
actions, overlay, docs, configuration and offline tools. No viewer-specific
data or secrets belong in a release.

## Release checklist — mandatory before production merge

1. Export all persisted v3 data; verify the JSON hash and sample Twitch user totals.
2. Reconcile the snapshot; note any unmatched historical entries.
3. Run the legacy wheel migration once in the shared BLOCKING queue.
4. Compile each standalone C# action inside the exact Streamer.bot version used on stream.
5. Test correct reward-trigger arguments and duplicate redemption refund/fulfillment behavior.
6. Test !checkins, !checkinboard, !wheel, !addgame and !wheeladmin with correct permissions, including pending approval/rejection, duplicate-title denial and no-cost replacement.
   Test message redelivery after the Eastern month boundary and validate the nightly archive excludes pending games.
7. Test crashes/recovery in a separate Streamer.bot instance; verify no duplicate wheel slots.
8. Verify OBS WebSocket response and the 60-second timer.
9. Run diagnostics/reconciliation, export another snapshot and store it off-machine.
   Test a separate OBS wheel source with one past-month approved fixture, replay (same winner), hide, winner announcement and broadcaster-only reset; verify no effects on check-in graphics.
10. Only then merge/deploy and enable Channel Points redemption for viewers.

## Known limitations

Streamer.bot global-variable writes are independent. A journal helps recover
interrupted wheel submissions, but cannot make cross-variable changes fully
atomic. Audit history is month-partitioned JSON and begins only after this
refactor; reconstructing pre-v5 streams requires existing aggregate counters.
Monthly archives are **prepared for a separate spinning tool**; no winners are chosen by this project. The localhost dashboard operates on exported snapshots, not on live Streamer.bot state.
The backup's user-variable inventory includes the known keys from this repo:
custom per-user variables require explicitly extending the exporter/importer.
