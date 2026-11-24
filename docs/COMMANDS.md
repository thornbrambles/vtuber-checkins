# Chat commands and Channel Points redemption

Moderators can run `!checkinadmin` and `!wheeladmin` in the channel chat between broadcasts. Streamer.bot must be running and connected to Twitch.

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

The **🌿 Stream Check-in** Channel Points redemption is separate from chat commands. Chat output stays short:

| Event | Example |
| --- | --- |
| Successful check-in | `🌿 @viewer checked in! 12 total check-ins, a 4-stream streak, and 8 people checked in this stream.` |
| Badge earned | `🏆 @viewer got the Sapling badge!` |
| Wheel credit earned | `🎮 @viewer earned a game pick! 1 left. Use !addgame <title>.` |
| Duplicate redemption | `@viewer, you're already checked in! Your points were refunded.` |
| Stream not initialized | `@viewer, check-ins aren't open yet. Your points were refunded.` |

If cancellation fails, the viewer is asked to contact a moderator instead of being told a refund succeeded. A replayed redemption event does not post another check-in message.

## Viewer chat commands

| Command | Access | Action | Effect |
| --- | --- | --- | --- |
| `!checkinhelp` | Everyone | `19-viewer-help.cs` | Lists the enabled viewer commands for this project and explains how to redeem a check-in |
| `!checkins` | Everyone | `04-mycheckins.cs` | Your lifetime total, streak, all-time rank, milestone badge, whether you checked in this stream, next milestone and available wheel credits |
| `!checkinboard` | Everyone | `03-leaderboard.cs` | Top five viewers by lifetime check-ins; chat message only |
| `!wheel` | Everyone | `07-wheel-commands.cs` | Earned/used/available game submission credits |
| `!wheel credits` | Everyone | `07-wheel-commands.cs` | Same as `!wheel` |
| `!wheel list` | Everyone | `07-wheel-commands.cs` | Up to five **approved** current-month games; pending/rejected entries are hidden |
| `!addgame Stardew Valley` | Everyone | `06-game-submit.cs` | Submit a title for moderator approval, using one earned credit and one of 3 monthly slots; replace a rejected title without another credit |
| `!addgame` (no title) | Everyone | `06-game-submit.cs` | Recover an interrupted previous submission, if any; otherwise display usage |

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

## Spinning the monthly game wheel

`!spinwheel` (Starts With; Moderator/broadcaster only) loads the **previous** Eastern calendar month and starts a saved, randomized, one-winner animation in the separate OBS game-wheel Browser Source. It will **refuse** to draw if any game still needs approval. Each approved submission counts once. The result is permanently recorded until an explicit broadcaster-only reset; repeated spin requests do not pick a second winner.

| Command | Effect |
| --- | --- |
| `!spinwheel` | Spin the previous month’s approved game submissions |
| `!spinwheel 2026-10` | Spin the specified **past** month |
| `!spinwheel replay 2026-10` | Replay the existing winner without a new selection or chat announcement |
| `!spinwheel hide` | Hide the wheel immediately, without altering the draw |
| `!spinwheel reset 2026-10 RESET-2026-10` | **Broadcaster only**: retain an audit event, clear the saved draw and allow a new spin |

Install `17-wheel-spin.cs` as **CI - Spin Wheel** and `18-wheel-reveal.cs` as **CI - Wheel Reveal** with a local timed trigger every ~2 seconds. The latter posts the winning game after the animation, not before it. The wheel overlay is separate from check-in OBS graphics.

## Diagnostics command

`!checkindiag` (Exact; Moderator/broadcaster only) runs the `CI - Diagnostics` read-only action inline and posts a short status summary in Twitch chat. Full findings are stored privately in `ci_diagnostics_json` and the Streamer.bot log. It is also usable when the bot is connected but the stream is offline.

## Wheel moderator commands

`!wheeladmin` (Starts With; Moderator/broadcaster only) is implemented by `13-wheel-admin.cs`. Subcommands: `!wheeladmin list [YYYY-MM]`, **`!wheeladmin approve ENTRY_ID`**, **`!wheeladmin reject ENTRY_ID`**, `!wheeladmin rename ENTRY_ID New Title`, `!wheeladmin remove ENTRY_ID`, `!wheeladmin restore ENTRY_ID`. A rejected game can be replaced during the **same calendar month** with another `!addgame Title` without a new credit or monthly slot. Removals are soft-deletes and do not refund credits. All edits are recorded in `ci_wheel_audit_json`.

## Access and matching settings

| Command registration | Matching | Permissions |
| --- | --- | --- |
| `!checkins` | Exact | Everyone |
| `!checkinboard` | Exact | Everyone |
| `!checkinhelp` | Exact | Everyone |
| `!wheel` | Starts With | Everyone |
| `!addgame` | Starts With | Everyone |
| `!checkinadmin` | Starts With | Moderator + broadcaster |
| `!wheeladmin` | Starts With | Moderator + broadcaster |
| `!spinwheel` | Starts With | Moderator + broadcaster |
| `!checkindiag` | Exact | Moderator + broadcaster |

The source files implement the behavior, but they **do not provision the UI command registrations or triggers**.

## Storage and rewards

- Shared stream state: `ci_stream_id`, `ci_stream_count`, etc. (persisted globals)
- Each Twitch user: `ci_total`, `ci_streak`, `ci_last_stream`, `ci_badge`, `ci_wheel_earned`, `ci_wheel_claimed` (persisted user variables)
- Monthly entries: `ci_wheel_month_YYYY_MM` (persisted JSON array per month)
- Wheel draw result: `ci_wheel_spin_result_YYYY_MM`, animated state: `ci_wheel_spin_overlay_json`, audit: `ci_wheel_spin_audit_json`
- Legacy wheel archive: `ci_wheel_entries_json` (read-only after migration)

Each viewer can use at most **3 game picks per Eastern Time calendar month**. Unused earned credits carry over, and the monthly allowance resets with the new month. Removing or rejecting a submitted game does **not** reopen that month's slot or refund its credit. New submissions start **pending** and require moderator approval before appearing in `!wheel list` or the spin-ready archive. Rejected submissions remain replaceable at no extra cost within their original month. Duplicate titles in the same month are rejected (case-insensitive, collapsing whitespace). `!wheel` displays credits saved and monthly picks remaining.

Available credits = `max(0, ci_wheel_earned - ci_wheel_claimed)`. Pending submissions are saved per user as `ci_wheel_pending_json` for crash recovery. Run the one-time migration action before the new wheel commands are enabled. `ci_wheel_earned` is the highest number of ten-check-in entitlements ever earned and never decreases. Historical redemptions never become available again after an admin reset.

## No other chat commands

The repo does not currently implement a separate `!checkin`, `!rank`, `!gamedelete`, `!wheel reset`, `!wheel export`, or web-admin UI command. The exporter is a **separate Python script**; not a Twitch chat command.
