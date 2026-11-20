# Chat commands and Channel Points redemption

This document describes the commands present **in the repository**. These are not automatically created in your running Streamer.bot instance: you must configure each command and action trigger in the Streamer.bot UI.

## Check in — Twitch Channel Points reward (not a chat command)

- Viewer action: redeem **🌿 Stream Check-in**
- Trigger: Twitch Channel Reward > Reward Redemption (specific reward only)
- Action: `CI - Viewer Check-in` / `02-checkin.cs`
- Effect: once per logical stream, add one lifetime check-in, advance/reset consecutive-stream streak, update badge and OBS snapshot, attempt to fulfill redemption.
- Duplicate: no new attendance; attempt refund.
- OBS: a successful redemption makes the leaderboard visible for 60 seconds.
- `!checkin` is **not** part of v4+; remove any old command trigger.

## Channel Points redemption messages

The **🌿 Stream Check-in** redemption is separate from chat commands. Its viewer-facing messages make the outcome explicit:

| Event | Example chat response |
| --- | --- |
| First check-in | `✅ 🌿 @viewer — Welcome to the Grove! Check-in #1 • a new streak started • 1 viewer(s) checked in this stream. Your next step: !checkins for your progress.` |
| Repeat attendance, new stream | `✅ 🌿 @viewer — You're checked in! Check-in #12 • 4-stream streak • 8 viewer(s) checked in this stream. Your next step: !checkins for your progress.` |
| Badge milestone | `🏆 Milestone unlocked! @viewer earned the Sapling badge for 10 total check-ins!` |
| New game credit | `🎮 Game wheel credit earned! @viewer now has 1 available. Submit a game with !game Game Title.` |
| Duplicate redemption | Says **no extra check-in recorded** and reports whether cancellation succeeded |
| Before stream initialization | Says **check-in NOT recorded** and reports whether cancellation succeeded |

If Twitch fulfillment fails after the check-in has already been recorded, the action logs a warning for the moderator instead of encouraging another redemption. Replayed redemption events do not announce a second successful check-in.

**Refund distinction:** A successful Streamer.bot cancellation call means the cancellation request succeeded. The action cannot independently prove when Twitch returns the points to the viewer's balance.

## Viewer chat commands

| Command | Access | Action | Effect |
| --- | --- | --- | --- |
| `!checkins` | Everyone | `04-mycheckins.cs` | Your lifetime total, streak, all-time rank, milestone badge, whether you checked in this stream, next milestone and available wheel credits |
| `!checkinboard` | Everyone | `03-leaderboard.cs` | Top five viewers by lifetime check-ins; chat message only |
| `!wheel` | Everyone | `07-wheel-commands.cs` | Earned/used/available game submission credits |
| `!wheel credits` | Everyone | `07-wheel-commands.cs` | Same as `!wheel` |
| `!wheel list` | Everyone | `07-wheel-commands.cs` | Up to five current-month game entries with game, user, date and total entry count |
| `!game Stardew Valley` | Everyone | `06-game-submit.cs` | Spend one available credit; save the game, username, Twitch user ID, date, month and entry ID in the current month's persisted ledger |
| `!game` (no title) | Everyone | `06-game-submit.cs` | Recover an interrupted previous submission, if any; otherwise display usage |

## Moderator and broadcaster commands

The only admin command is `!checkinadmin`, with these subcommands. Configure **Starts With** matching and **Moderator** permission in Streamer.bot. The C# script also checks moderator/broadcaster status.

| Command | Effect |
| --- | --- |
| `!checkinadmin view @viewer` | Read user's current total, streak, last stream, badge and available wheel credits |
| `!checkinadmin set @viewer 25` | Set check-in total to 25; recalculate badge and refresh OBS leaderboard |
| `!checkinadmin add @viewer 5` | Increase lifetime total by five |
| `!checkinadmin remove @viewer 3` | Reduce lifetime total by three (floor of zero) |
| `!checkinadmin streak @viewer 10` | Set a viewer's streak to ten |
| `!checkinadmin reset @viewer` | Reset check-in total, streak and current attendance, but **preserve earned credits, used credits and historical game entries**; no second Channel Points redemption in the same stream |

All the admin edits are logged. Manually increasing totals can unlock additional lifetime wheel entitlements when crossing a new high-water milestone; decreasing and increasing back to previous totals won't issue extra entitlements.

## Diagnostics command

`!checkindiag` (Exact; Moderator/broadcaster only) runs the `CI - Diagnostics` read-only action inline and posts a short status summary in Twitch chat. Full findings are stored privately in `ci_diagnostics_json` and the Streamer.bot log. It is also usable when the bot is connected but the stream is offline.

## Wheel moderator commands

`!wheeladmin` (Starts With; Moderator/broadcaster only) is implemented by `13-wheel-admin.cs`. Subcommands: `!wheeladmin list [YYYY-MM]`, `!wheeladmin rename ENTRY_ID New Title`, `!wheeladmin remove ENTRY_ID`, `!wheeladmin restore ENTRY_ID`. Removals are soft-deletes and do not refund credits. All edit operations are recorded in `ci_wheel_audit_json`.

## Access and matching settings

| Command registration | Matching | Permissions |
| --- | --- | --- |
| `!checkins` | Exact | Everyone |
| `!checkinboard` | Exact | Everyone |
| `!wheel` | Starts With | Everyone |
| `!game` | Starts With | Everyone |
| `!checkinadmin` | Starts With | Moderator + broadcaster |
| `!wheeladmin` | Starts With | Moderator + broadcaster |
| `!checkindiag` | Exact | Moderator + broadcaster |

The source files implement the behavior, but they **do not provision the UI command registrations or triggers**.

## Storage and rewards

- Shared stream state: `ci_stream_id`, `ci_stream_count`, etc. (persisted globals)
- Each Twitch user: `ci_total`, `ci_streak`, `ci_last_stream`, `ci_badge`, `ci_wheel_earned`, `ci_wheel_claimed` (persisted user variables)
- Monthly entries: `ci_wheel_month_YYYY_MM` (persisted JSON array per month)
- Legacy wheel archive: `ci_wheel_entries_json` (read-only after migration)

Available credits = `max(0, ci_wheel_earned - ci_wheel_claimed)`. Pending submissions are saved per user as `ci_wheel_pending_json` for crash recovery. Run the one-time migration action before the new wheel commands are enabled. `ci_wheel_earned` is the highest number of ten-check-in entitlements ever earned and never decreases. Historical redemptions never become available again after an admin reset.

## No other chat commands

The repo does not currently implement a separate `!checkin`, `!rank`, `!gamedelete`, `!wheel reset`, `!wheel export`, or web-admin UI command. The exporter is a **separate Python script**; not a Twitch chat command.
