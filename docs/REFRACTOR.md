# v5 reward accounting and wheel storage refactor

## Why it changed

v4 calculated credits as `floor(current total / 10) - claimed`, and reset `claimed` to zero when a moderator reset check-ins. This could make prior wheel submissions eligible again even though their original history remained. All months were also stored in one `ci_wheel_entries_json` string, requiring the entire archive to be re-written for each entry.

## Refactored invariants

1. `ci_wheel_earned`: per-user lifetime **high-water number of credits**. Never reduce it on `set`, `remove`, or `reset`. At a check-in or admin edit, set it to at least `floor(total / 10)`, and at least `ci_wheel_claimed`.
2. `ci_wheel_claimed`: used credits; never clear during an admin reset.
3. Available credits = `max(0, earned - claimed)`.
4. New wheel entries go to a persisted global variable named `ci_wheel_month_YYYY_MM`. This contains a JSON array of entries for that month only.
5. Preserve the old `ci_wheel_entries_json` archive unchanged and mark migration with `ci_wheel_legacy_migrated`. Both `!wheel` and `!game` refuse operations until the marker is set by migration.
6. All actions and the one-time migration must run in the same **blocking action queue**.
7. `!game` persists `ci_wheel_pending_json` on the viewer before applying the ledger and credit writes. Replaying an interrupted transaction is idempotent by entry ID, and repeated Twitch `msgId` events do not consume extra credits. Run `!game` with no game title to recover an interrupted entry.
8. A Channel Points redemption also records `ci_reward_last_stream` and `ci_last_redemption_id`; admin resets cannot cause a second redemption during the same logical stream. The same event ID can be retried for fulfillment without refunding a recorded check-in.

### Reset example

- Viewer reaches 30 check-ins: three credits earned.
- Viewer uses two credits: one remains.
- Admin resets their check-in total to zero.
- Earned stays three, used stays two: **one still available**, not three.
- When the viewer eventually returns to 30 check-ins, the system doesn't reissue credits. The next new entitlement is at a new high-water count, 40.

## Upgrade / migration (IMPORTANT)

**Back up your Streamer.bot data first.** Do not replace v4 scripts or submit any new games until you migrate history. Existing v4 submissions must be imported into monthly keys or `!wheel list` won't show them.

1. Confirm the Community Checkins queue is **Blocking** and existing actions use it.
2. Create the one-time action `CI - Migrate Legacy Wheel`; paste `src/actions/08-migrate-legacy-wheel.cs`.
3. Assign it to Community Checkins and **do not add any Twitch/chat trigger**.
4. Run it manually once. Check the Streamer.bot log for “Wheel migration complete” or “no legacy entries”.
5. Verify monthly global variables `ci_wheel_month_YYYY_MM` were populated, that the old `ci_wheel_entries_json` still exists, and that `ci_wheel_legacy_migrated` is true.
6. Replace the action bodies in **02, 04, 05, 06 and 07** with the updated files from this branch. Compile each. `!game` and `!wheel` will refuse operations if migration was not completed.
7. Keep the other actions, Twitch commands and OBS overlay unchanged. Test `!checkins`, `!wheel`, `!wheel list` and `!game` with a known test viewer.

The migration is idempotent for entries with stable `id` values, but a failed or interrupted run must be investigated before trying again. The action does not delete the old global array.

### What cannot be reconstructed perfectly

If a moderator already reset a user's credits under v4, the prior high-water lifetime total may have been lost. Migration can recover a **lower bound** from surviving game entries and current variables, but cannot reconstruct unknown historical check-in totals. Review affected viewers manually before assigning extra rewards.

## Monthly CSV exports

Copy the JSON value of `ci_wheel_month_YYYY_MM` from Streamer.bot to a UTF-8 JSON file. Then:

```bash
python tools/export-wheel.py wheel-2026-11.json 2026-11
```

The exporter produces `wheel-2026-11.csv` with `game,user,date`. No server is required; the monthly wheel is still held by Streamer.bot.

## Remaining limitations

- The `ci_wheel_pending_json` recovery journal handles typical interruption between credit and ledger writes. A crash, filesystem corruption or failed persistent save still requires inspection; Streamer.bot does not offer multi-variable atomic transactions. Check any pending journal before manual edits.
- The one-time migration can reconstruct the number of surviving game entries, but cannot infer lost historical check-in maxima or game entries absent from the legacy archive.
- A manually increased total can advance the high-water reward count. This is intentional, but administrative changes should be auditable.
- `!wheel list` only shows the first five entries; export JSON for the full list.
- The C# scripts are designed to be pasted into Streamer.bot actions; they have not been compiled against a live installation.
