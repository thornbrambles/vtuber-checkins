# Installation — VTuber Check-ins v5

**Upgrading from deployed v3?** Begin with [V3-MIGRATION-BACKUP-DEBUG.md](V3-MIGRATION-BACKUP-DEBUG.md). You can migrate directly from v3; export existing state before changing actions.

**Upgrading from v4?** Read [REFRACTOR.md](REFRACTOR.md) and run the one-time wheel migration **before installing the new v5 wheel scripts**. The original all-month wheel JSON must be migrated or old entries will not appear in monthly lists.

**Manual-only maintenance actions:** `09-export-data.cs` backs up this project's persistent data, `10-import-data.cs` validates and restores on explicit opt-in, and `11-debug-admin.cs` previews/applies offline edits with an explicit enable flag. **Never attach chat or Twitch triggers to these actions.**

**All commands and permissions:** [COMMANDS.md](COMMANDS.md).

A local-only admin dashboard for private backup files is available at `tools/admin-dashboard.py`. It stages modifications into new importable JSON with checksums; it is **not directly connected to Streamer.bot**. Read [OPERATIONS.md](OPERATIONS.md) and [STREAMERBOT-ACCEPTANCE.md](STREAMERBOT-ACCEPTANCE.md) before using it.

This repository contains standalone Streamer.bot C# action bodies and an OBS HTML overlay. It is **not** an importable Streamer.bot package. Follow the steps in order. Back up Streamer.bot before changing an existing setup.

## Requirements

- Current Streamer.bot with the Twitch broadcaster/bot accounts connected and authorized
- Twitch Channel Points enabled on the broadcaster account
- OBS Studio and Streamer.bot running on the same Windows PC (default overlay setup)
- A Twitch broadcaster account able to create Channel Points rewards
- Permissions to create Streamer.bot actions, triggers, commands, and start its WebSocket server

## 1. Back up before installing

Make a Streamer.bot backup/export, and record any existing persisted user/global variables. Do not delete the existing variables. This v4 setup uses variable names beginning with ci_.

## 2. Create the blocking queue

In Streamer.bot go to Actions & Queues, create a queue called Community Checkins and set it to Blocking. Assign **all seven runtime** actions below to that same queue. This serializes check-ins, admin edits, wheel submissions and overlay-state writes.

## 3. Create the Channel Points reward

In Twitch Channel Points / Streamer.bot's Twitch reward manager, create a custom reward:

- Name: 🌿 Stream Check-in
- Suggested cost: 1 Channel Point
- Require viewer to enter text: No
- Per-user per-stream limit: 1, where supported
- Channel-wide redemption limit: None
- Redemption Skips Queue: **OFF** (leave redemptions pending for explicit fulfillment/refund)

The reward name is cosmetic. Select the *specific reward* by its identity in the redemption trigger; **do not use Any Reward**. Avoid an additional automated fulfillment action attached to the same reward.

Important: Twitch's native one-per-stream limit can prevent another redemption after an administrator resets that person's records in the same stream.

## 4. Create actions and install C# code

Under Actions & Queues > Actions, create each action below, assigned to the Community Checkins blocking queue. Add Core > C# > Execute C# Code and paste the **entire contents** of the indicated file into its own action. Compile and save each.

| Streamer.bot action | File | Trigger |
| --- | --- | --- |
| CI - Start Stream | src/actions/01-stream-start.cs | Twitch > Channel > Stream Online |
| CI - Viewer Check-in | src/actions/02-checkin.cs | Twitch > Channel Reward > Reward Redemption (select 🌿 Stream Check-in) |
| CI - Leaderboard | src/actions/03-leaderboard.cs | Core > Commands > Command Triggered (!checkinboard) |
| CI - My Check-ins | src/actions/04-mycheckins.cs | Core > Commands > Command Triggered (!checkins) |
| CI - Admin Check-ins | src/actions/05-admin-checkins.cs | Core > Commands > Command Triggered (!checkinadmin) |
| CI - Game Submit | src/actions/06-game-submit.cs | Core > Commands > Command Triggered (!addgame) |
| CI - Wheel Commands | src/actions/07-wheel-commands.cs | Core > Commands > Command Triggered (!wheel) |
| CI - Wheel Admin | src/actions/13-wheel-admin.cs | Core > Commands > Command Triggered (!wheeladmin) |
| CI - Diagnostics | src/actions/12-diagnostics.cs | Manual (no trigger; called by CI - Diagnostic Command) |
| CI - Diagnostic Command | src/actions/15-diagnostic-command.cs | Core > Commands > Command Triggered (!checkindiag) |
| CI - Month Rollover | src/actions/16-month-rollover.cs | Off-stream local timed trigger, daily at ~00:10 Eastern; no chat/Twitch trigger |
| CI - Spin Wheel | src/actions/17-wheel-spin.cs | Core > Commands > Command Triggered (!spinwheel), mod/broadcaster only |
| CI - Wheel Reveal | src/actions/18-wheel-reveal.cs | Local timed trigger every ~2 seconds; no chat/Twitch trigger |

**Review flow:** New `!addgame Title` entries are **pending**, do not appear in `!wheel list`, and consume one credit/one of three monthly slots. Mods use `!wheeladmin list`, then `!wheeladmin approve ID` or `reject ID`. Rejected submissions can be replaced with another `!addgame Title` within the same calendar month without another credit or slot. Check [COMMANDS.md](COMMANDS.md).

**Monthly rollover:** `CI - Month Rollover` rebuilds the previous month's approved-only archive in persisted `ci_wheel_archive_YYYY_MM`. It does not pick a winner or alter the OBS overlay. See [OPERATIONS.md](OPERATIONS.md) for CSV export and scheduling.

Trigger names/locations vary with Streamer.bot version. Use the corresponding Twitch stream-online, reward-redemption and command-trigger events if menu labels differ.

Do **not** attach the old !checkin chat command to CI - Viewer Check-in. Its code expects rewardId and redemptionId and will reject ordinary command events.

## 5. Create chat commands

In Commands, create and enable these exact names, selecting the intended action via a Command Triggered trigger:

| Command | Matching | Access | Purpose |
| --- | --- | --- | --- |
| !checkins | Exact / Basic | Everyone | User's total, streak, rank, badge, wheel credits |
| !checkinboard | Exact / Basic | Everyone | Top five lifetime attendance |
| !checkinadmin | Starts With / Basic | Moderator + broadcaster | Inspect/edit/reset someone else's values |
| !addgame | Starts With / Basic | Everyone | Submit title after command |
| !wheel | Starts With / Basic | Everyone | Credits or list subcommand |
| !wheeladmin | Starts With / Basic | Moderator + broadcaster | Moderate wheel entries by ID |
| !spinwheel | Starts With / Basic | Moderator + broadcaster | Spin, replay and hide the approved previous-month wheel |
| !checkindiag | Exact / Basic | Moderator + broadcaster | Run diagnostics and summarize in chat |

Command settings are independent from action trigger creation. For each command ensure the proper *Command Triggered* trigger exists on its corresponding action and points at the matching command.

**Permissions:** Restrict !wheeladmin and !checkindiag to moderators/broadcaster too. `CI - Diagnostics` must use exactly this action name so `!checkindiag` can invoke it.

**Permissions:** Restrict !checkinadmin to Twitch moderators and the broadcaster in the command's permissions editor. The C# action also rejects actors who are not broadcaster or moderator. Do not leave an empty allow-list if your version treats that as Everyone.

### Viewer commands

- !checkins — personal total, consecutive streams, rank, badge, current attendance, and available game credits
- !checkinboard — top five lifetime attendance
- !wheel or !wheel credits — unused game submission credits
- !wheel list — up to five entries in the current month's wheel
- !addgame Stardew Valley — add a game to the current month using one credit
- !addgame (without a title) — recover a pending, interrupted submission and display usage

### Moderator commands

- !checkinadmin view @viewer — inspect counts
- !checkinadmin set @viewer 25 — set lifetime total to 25
- !checkinadmin add @viewer 5 — add five lifetime check-ins
- !checkinadmin remove @viewer 3 — subtract three
- !checkinadmin streak @viewer 7 — set streak to seven
- !checkinadmin reset @viewer — reset lifetime total, streak, current attendance but not already earned or spent wheel credits

Administrative changes re-evaluate badges and republish leaderboard state. Historical game entries already submitted are not deleted by a reset. The audit messages are written to Streamer.bot's log.

## 6. Enable local WebSocket server

In Streamer.bot, go to Servers/Clients > WebSocket Server:

- Enabled: Yes; Auto Start: Yes
- Host/address: 127.0.0.1
- Port: 8080
- Endpoint: /
- Local use only: do not expose 8080 to the public Internet

The included overlay assumes its local WebSocket connection does not require an authentication challenge. If you enable WebSocket authentication, update the overlay integration rather than exposing credentials in a public repository.

## 7. Install OBS overlay

In OBS Studio add a **Browser Source**, check **Local File** and point to overlay/checkin-overlay.html. Set the browser source to 1920 x 1080. Keep OBS and Streamer.bot on the same PC unless you also configure a secure reachable WebSocket URL.

The leaderboard starts invisible. After a **successful** check-in redemption, it fades in, stays visible for 60 seconds, then fades out. Another successful redemption restarts the minute. Duplicate redemptions, admin changes and chat-only requests do not restart the timer. Milestone toast notifications are separate.

The overlay polls the persisted global variable ci_overlay_json using Streamer.bot's WebSocket GetGlobal request and uses ci_last_checkin_at_ms to determine its visibility deadline.

### Separate on-demand game wheel in OBS

Add a **second Browser Source** from `overlay/game-wheel-overlay.html` as a **Local File**, at **1920×1080**. Position it above gameplay in the source order. Leave the source enabled: its HTML starts transparent and remains invisible until `!spinwheel` runs. No changes are needed to `overlay/checkin-overlay.html`.

Register `!spinwheel` (Starts With, **Moderator + broadcaster**) with **CI - Spin Wheel**. Add `src/actions/18-wheel-reveal.cs` as **CI - Wheel Reveal**, configured to run every approximately **2 seconds** using a **local timed trigger**, in the same Blocking queue. This posts the winning game to Twitch chat after the animation finishes. If you prefer an OBS-only reveal, omit the timed reveal trigger.

Run `!spinwheel` to draw the **previous** Eastern calendar month; `!spinwheel 2026-10` selects an explicit past month. It refuses to run while entries are pending approval, saves its result **before** the 10-second animation, and will not select again for the same month. `!spinwheel replay 2026-10` replays the saved result without a new draw or chat announcement. `!spinwheel hide` immediately hides the wheel. Only the broadcaster can use `!spinwheel reset 2026-10 RESET-2026-10` to authorize a new draw; the prior draw remains in the audit log. **Reset is destructive to the current winner selection**, so take a backup first.

The wheel is purposely limited to **past months** so entries cannot change during an ongoing monthly draw. Each approved submission is one equally weighted slice. The wheel hides automatically after approximately **18 seconds**, and the check-in overlay never changes.

## 8. Initialize and test

1. During a live stream, the Stream Online trigger should run CI - Start Stream, assigning the current logical stream ID. For offline testing you may manually run the start action **once**.
2. Redeem the specific 🌿 Stream Check-in reward from an eligible Twitch viewer.
3. Confirm the bot replies with lifetime total, streak and stream attendance; OBS leaderboard fades in.
4. Type !checkins as that viewer. Confirm the count is nonzero and check-in status is current.
5. Type !checkinboard and inspect the ranks.
6. Try a duplicate redemption where Twitch allows it: it must not increment counts; refund behavior depends on reward queue settings.
7. Have a moderator run !checkinadmin view @viewer and !checkinadmin add @viewer 9; verify total and rewards. Restore test changes afterward.
8. At ten lifetime check-ins, type !wheel, then !addgame Game Title. Verify credits decrement and !wheel list shows game, user, and date.
9. After the final successful check-in, verify the overlay hides about 60 seconds later.
10. Restart Streamer.bot and verify persisted stats remain.

Do not repeatedly initialize a manual stream during an actual broadcast. The stream-start script normally deduplicates Twitch Stream Online events using startedAt; manual execution uses a new timestamp and can advance the logical stream counter.

## 9. Monthly game wheel

Every ten lifetime check-ins yields one submission credit. Credits carry over until used. A viewer with 30 lifetime check-ins has three credits less any already claimed. Each !addgame entry stores game, Twitch user ID, username, Eastern-local date, Eastern-local YYYY-MM month, UTC timestamp and an entry ID.

New entries reside in month-partitioned persisted global arrays, named `ci_wheel_month_YYYY_MM`. The previous `ci_wheel_entries_json` is kept as a read-only legacy archive after migration. Earned credits are stored as a monotonic per-user `ci_wheel_earned` high-water counter, while `ci_wheel_claimed` counts used credits; admin resets preserve both. Export that string to a UTF-8 file called wheel-entries.json, then run:

    python tools/export-wheel.py wheel-2026-11.json 2026-11

Output: wheel-2026-11.csv with **game, user, date** columns. The exporter does not talk directly to Streamer.bot; you must supply the JSON export manually. Keep raw viewer export files out of Git.

## 10. Upgrading from earlier versions

Back up everything first. **If upgrading from v4, run `src/actions/08-migrate-legacy-wheel.cs` manually in your blocking queue before replacing the wheel actions.** Read [REFRACTOR.md](REFRACTOR.md). Preserve existing persisted ci_* user/global variables. Replace your existing action bodies with the matching numbered source files, keeping the blocking queue. The v4 additions were CI - Game Submit and CI - Wheel Commands, with the original `!game` and `!wheel` chat commands. V5 replaces `!game` with `!addgame`. The v5 refactor adds monthly storage and the one-time migration action. See [OPERATIONS.md](OPERATIONS.md) for scheduled backups, audit history, sandbox, reconciliation and CI release workflow. **New !addgame and !wheel actions refuse submissions/queries until `ci_wheel_legacy_migrated` is true.** If upgrading from a chat-command check-in version, remove the old !checkin trigger and connect CI - Viewer Check-in to the specific Twitch Channel Points redemption instead.

The overlay file is replaced independently in OBS; refresh its Browser Source afterward. Do not erase Streamer.bot's persisted user variables just to install the source code.

## 11. Troubleshooting

### !checkins produces no message

- Verify the Twitch bot account is connected and can send chat messages.
- Check Commands > !checkins: enabled, basic exact match, Everyone allowed.
- Verify CI - My Check-ins has the **Command Triggered** trigger selecting that exact command.
- Verify src/actions/04-mycheckins.cs was compiled and the action is enabled.
- Check action history and Streamer.bot log for missing userId/userName arguments or C# errors.
- Try executing the action from a *real Twitch chat event*; manual test execution may not provide Twitch-user arguments.

### !checkins says zero

This means the command may be working, but that Twitch user has no recorded ci_total. Confirm the check-in redemption actually fired CI - Viewer Check-in; the reward and viewer identifiers must be available. Confirm the stream-start action initialized ci_stream_id.

### Reward does nothing

Check the reward-trigger selector (specific reward, not Any), rewardId and redemptionId, the connection to Twitch, and whether the pending redemption was manually or automatically fulfilled by another action.

### OBS is blank

The leaderboard is **deliberately hidden** until a successful check-in; a blank screen after a minute is expected. For debugging, change the #status CSS opacity from 0 to 1 in the HTML, refresh the OBS browser, and confirm WebSocket access to ws://127.0.0.1:8080/. OBS must be able to access the Streamer.bot host.

### Wrong streak after a missed stream

Streaks count *logical broadcasts*, not calendar dates. Missing stream-start events and manual restarts can affect the stream counter. Use the moderator streak command to correct a verified issue.

### Wheel submissions not working

Check command matching for !addgame is Starts With, there is a Command Triggered trigger selecting !addgame, and CI - Game Submit uses src/actions/06-game-submit.cs. The user needs total/10 greater than their saved ci_wheel_claimed count.

## 12. Privacy, limitations and backup

Data is stored locally in Streamer.bot persisted variables, keyed to Twitch user IDs. Back up Streamer.bot data before changes; do not commit exported data, access tokens, or private user information. The project does not include an executable Windows installer, automatic source import, real spinning-wheel animation or hosted database.

These are community scripts, not a fully tested distributed service. Compile them against your specific Streamer.bot build and verify Channel Points fulfillment/refunds before deploying on a live stream.

## v5 reward-accounting note

`!checkinadmin reset @viewer` clears attendance counts and streaks, but intentionally **does not reset earned or spent wheel credits**. This prevents old reward redemptions from becoming spendable again. The migration action is manual-only, has no chat command or Twitch trigger, and should be disabled after completion. The new `ci_reward_last_stream` marker prevents another Channel Points redemption in the same stream after an admin reset, although the admin can clear the displayed attendance. An interrupted game submission can be reconciled by using `!addgame` with no title; inspect `ci_wheel_pending_json` if recovery fails. See [REFRACTOR.md](REFRACTOR.md) for limitations and recovery instructions.
