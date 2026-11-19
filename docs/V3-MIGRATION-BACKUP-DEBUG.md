# Migration from deployed v3 / data backup / offline debug

This is the path for a **deployed v3** Channel Points check-in setup. **You do not need to install v4.** The newer repository code can read the same persisted `ci_*` variables.

**Two separate kinds of backup:**
- Streamer.bot's standard Export/Import includes configured actions, triggers, commands, and queues, **not a complete export of all persistent check-in records**.
- `09-export-data.cs` exports **this project's** persisted Twitch viewer variables and all persisted global variable names beginning `ci_`. It does not export all unrelated Streamer.bot variables, Twitch rewards, bot configuration, OBS scenes, or third-party account credentials.

## 1. Back up before doing anything

1. Stop Twitch redemptions temporarily, and make a normal backup/copy of the entire Streamer.bot installation/data folder while closed.
2. Use Streamer.bot's regular Export interface to separately export your actions and configuration.
3. In the deployed **v3** install, create a new manual action named **CI - Export Data** in the `Community Checkins` blocking queue. Paste the entire `src/actions/09-export-data.cs` and compile.
4. Give it **no triggers**. Manually run the action once.
5. A timestamped JSON file is written under your Windows **Documents/vtuber-checkins-backups/** folder. See the Streamer.bot log for the exact path and counts.
6. Copy the JSON to a **private**, protected location. It includes Twitch user IDs and chat usernames. Never push the JSON to your public GitHub repository.
7. Confirm it has `format`, `schemaVersion`, `globals`, and `users`. Inspect viewer totals before continuing.

**Exporter inventory**

Per-viewer integers: `ci_total`, `ci_streak`, `ci_last_stream`, `ci_wheel_claimed`, `ci_wheel_earned`, `ci_reward_last_stream`.

Per-viewer strings: `ci_badge`, `ci_last_redemption_id`, `ci_wheel_pending_json`.

Every persisted **global** variable beginning `ci_` is enumerated dynamically using `GetGlobalVarValues(true)`; that includes stream tracking, legacy wheel data and monthly wheel partitions if present.

The inventory covers the known versions of this project. If you added custom per-user variable names to your own v3 scripts, add them to the exporter and importer lists before relying on the JSON as complete.

## 2. Moving to another Streamer.bot instance (optional)

**If upgrading in the same Streamer.bot installation, DO NOT import the JSON.** Existing persisted data is already in place. Keep the JSON as a rollback backup.

For a different or freshly installed instance:

1. Configure and test your bot connection, but **do not allow viewer redemptions while restoring**.
2. Create the `Community Checkins` **Blocking** queue.
3. Create the manual **CI - Import Data** action from `10-import-data.cs` and assign it to that queue. **No triggers.**
4. Copy your exported JSON to `Documents/vtuber-checkins-backups/import.json` on the destination Windows account.
5. Run the import action with its defaults: `ApplyImport=false`, `OverwriteExisting=false`. This **validates only** and writes no data.
6. Inspect the Streamer.bot log for expected viewer counts, variables, and existing-data conflicts.
7. Take a destination backup. To actually restore, edit `ApplyImport=true` and compile again. If existing data needs replacement, also set `OverwriteExisting=true` **only after reviewing both backups**.
8. Manually run once. Check the final log for success, then immediately set `ApplyImport=false` again and recompile.
9. Verify several `ci_total` values from the JSON match the destination. Run `CI - Export Data` on the destination to compare.
10. Restore actions and triggers and then move to the v5 migration steps.

The importer validates the JSON structure, supported field types, user IDs, and conflicts **before writing**. It does not delete data absent from the JSON. Variable writes are not a database transaction: if the application crashes during import, restore from the original backup after investigating the failure.

## 3. Migrate from v3 to the refactored wheel ledger

1. Keep the v3 export JSON as a permanent, private reference.
2. Confirm the `Community Checkins` queue is **Blocking**.
3. Create **CI - Migrate Legacy Wheel** with `08-migrate-legacy-wheel.cs` (manual-only, no triggers).
4. Temporarily disable any `!game` command if your setup already has one.
5. Manually run the migration action. In v3 there may be **no wheel entries**; that is normal. The action sets `ci_wheel_legacy_migrated=true` and leaves user totals and streaks unchanged.
6. Inspect the log. Only then replace the runtime actions with the PR version; compile them individually.
7. If upgrading directly from v3, create the `!game` and `!wheel` commands; these are not part of the original v3 Channel Points workflow.
8. Run the manual export again to confirm the migrated state, then test the commands offline.
9. Re-enable live Channel Points redemptions only when test results look correct.

**Important:** You do **not** need to run the v4 ledger first. The migration action is designed to handle an empty legacy wheel archive.

## 4. Modify viewer stats when not live

Create a new manual-only action **CI - Debug Admin** from `src/actions/11-debug-admin.cs`. Assign it to `Community Checkins`; **never attach a chat command or any Twitch trigger**.

Edit the constants at the top of the C# code:

```csharp
private const bool DebugEnabled = true;
private const bool DryRun = true;
private const string TargetUserId = "12345678"; // actual numeric Twitch user ID
private const string Operation = "set";
private const int Value = 25;
```

Compile. Use **Run Action** manually. With `DryRun=true`, it logs the proposed change and writes nothing. Confirm the correct Twitch ID and values. Set `DryRun=false`, recompile, and run manually again to apply.

Supported operations: `view`, `set`, `add`, `remove`, `streak`, `reset`. No chat commands, Twitch stream, or Channel Points redemption is necessary.

**This edits real persisted user records.** Debugging does **not** create fake Channel Points redemptions or increment the current stream number. Earned and spent wheel-credit records are preserved. The `ci_reward_last_stream` anti-duplicate marker remains unchanged.

When finished, put the constants back to `DebugEnabled=false` and `DryRun=true`, compile/save, and disable the entire debug action.

## 5. What can be tested offline?

- Inspect any viewer's persisted totals and credits via the debug action.
- Preview edits without writing; then set/add/remove/reset totals or change streaks.
- Verify credit high-water behavior with `!checkins` / `!wheel` in Twitch chat if the bot is connected, without being live.
- Export current state and compare it against an earlier backup.
- Test `!game Game Title` in chat while offline if the wheel migration flag is set and the viewer has available credits. It **spends a real credit** and adds a real entry, so use a dedicated test account or make a backup first.

**Not supported without a stream:** a genuine Channel Points redemption and a true consecutive-stream check-in. Manual debug editing is intentionally not a synthetic stream event.

## 6. Known limitations and recovery

- Streamer.bot persists globals/user variables locally. The backup JSON covers this project's known user variable names and `ci_` globals, not every other Streamer.bot feature.
- The **Import Data** script defaults to refusing conflicts; do not force overwrite casually.
- Streamer.bot variable writes are not atomic across every user, so a crash while restoring can leave partial state.
- Avoid synced folders and public repositories for raw viewer backup files.
- If v3 used different variable names from the current source, compare its original scripts first and extend the field inventory before running the exporter.
- The debug action is *not* exposed to moderators in chat and therefore does not depend on a stream being active.

## 7. Validation checklist

- [ ] A backup of your entire Streamer.bot directory exists
- [ ] An export JSON exists, with plausible counts of Twitch users
- [ ] Restore dry-run validation passes
- [ ] The original v3 totals and streaks are preserved
- [ ] `ci_wheel_legacy_migrated` is true after the migration action
- [ ] Debug preview shows the correct Twitch user ID
- [ ] Debug changes are only applied manually
- [ ] Debug mode is disabled again
- [ ] Channel Points check-in, `!checkins`, `!wheel`, and OBS verified before the next live stream
