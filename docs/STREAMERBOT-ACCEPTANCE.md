# Streamer.bot acceptance test plan (requires a real local test instance)

Python sandbox/model tests and stubbed C# compilation are necessary but **not sufficient**.
Use a separate portable Streamer.bot installation and test Twitch account/channel,
and never run destructive tests on the actual viewer records.

## Setup

1. Use a disposable Streamer.bot data directory, a test broadcaster and test bot account.
2. Configure the Channel Points reward **🌿 Stream Check-in** to target the exact reward ID.
3. Install actions 01–19 and compile **each** in Streamer.bot.
4. Put all runtime actions, reviewer actions, migration and archive in one Blocking queue.
5. Register the commands using [the access and matching settings](COMMANDS.md#access-and-matching-settings) and run the updated action 08 before enabling check-ins.
6. Start a synthetic test stream or use the test channel to produce genuine Twitch events.
7. Save a complete backup before test and after test; never import test data into production.

## Test cases / expected results

| Scenario | How to run | Must verify |
| --- | --- | --- |
| Viewer command help | Type `!checkinhelp` as a viewer, then disable a viewer command and try again | One chat reply with the enabled viewer commands and the Channel Points reward; no moderator tools |
| First legitimate redemption | Redeem Stream Check-in on test stream | ci_total +1; streak=1; Twitch fulfilled; exactly one chat message |
| Duplicate within stream | Redeem again | no added count or credits; cancellation/refund attempt; mod warning if refund fails |
| Redemption replay | Redeliver same redemption ID via test infrastructure | no extra count or duplicate chat message |
| Consecutive streams | New stream, redeem again | streak increments; lifetime total increments |
| New gap | Skip a stream before next check-in | streak resets to 1; earned credits preserved |
| Credit milestone | Reach 10 check-ins | exactly one new wheel credit, no duplicate reward if attendance reset |
| First game submission | !addgame A Unique Game | pending entry, claimed credit +1, invisible on !wheel list |
| Review approval | !wheeladmin approve ID | visible on !wheel list and export; no further credit deduction |
| Review rejection | !wheeladmin reject ID | hidden; can replace during the same month |
| Free replacement | !addgame Another Game | new pending entry, same slotId, claimed unchanged |
| Duplicate title | Submit same title with capitalization/spacing differences | rejected, no credit/slot consumed |
| Monthly maximum | Submit 3 distinct games from one viewer | fourth rejected; another viewer can still submit |
| Rollover | Repeat tests at 23:59 Eastern and 00:01 Eastern (or inject time into sandbox) | monthly quota resets and original pending transactions use their saved month |
| Chat redelivery across month | Replay previous-month msgId after 1st | same entry ID recognized; no new credit or monthly slot |
| Restart/crash recovery | Stop after pending journal, restart and send !addgame | exactly one ledger slot and spent credit, no duplicate submission |
| Manual archive | Run CI - Month Rollover for previous month | approved-only snapshot, idempotent on repeat; pending count noted |
| Spin with pending | !spinwheel with unreviewed past-month game | refuses to draw; no winner selected |
| Spin once | !spinwheel with approved past-month games | chosen winner persisted before OBS animation; all approved submission IDs have equal weight |
| Replay | !spinwheel replay YYYY-MM | same winner and same selected slice; no new draw, no repeat winner chat message |
| Duplicate spin | !spinwheel YYYY-MM again | refuses second draw until explicit broadcaster reset |
| Hide and expire | !spinwheel hide; let a new test spin finish | wheel hides on demand or after ~18 seconds; existing check-in overlay remains unchanged |
| Winner chat timing | Enable CI - Wheel Reveal, spin once, then replay and hide before the timer fires | original winner is announced once at the original reveal time |
| Off-stream administration | While the channel is offline and Streamer.bot is connected, run `!checkinadmin set @viewer 20` and `!wheeladmin approve ID` as a moderator with test data | both changes appear in a fresh backup, and a non-moderator cannot make either change |
| Reset authorization | Non-broadcaster and broadcaster try !spinwheel reset YYYY-MM RESET-YYYY-MM | only broadcaster with exact confirmation can reset, with history retained |
| Moderator auth | Non-mod sends !wheeladmin and !checkindiag | action refuses; moderator/broadcaster succeeds |
| OBS unchanged | Check in after a test redemption | existing leaderboard/60-second behavior unchanged |
| Backup & restore | Export, validate checksum; dry-run import on disposable instance; explicitly restore | exact selected viewer and wheel status restored |

## Additional time and date checks

- November 30 23:59 EST → December 1 00:01 EST: quota reset.
- March daylight-saving transition: local date must not advance/reverse unexpectedly.
- November daylight-saving repeat hour: no duplicate message ID or double-spent credit.
- Pending message created before midnight and recovered after midnight belongs to **original** month.
- Rejected submission from an old month cannot be replaced in a newer month; its credit stays spent.
- A moderator approval after automatic archive requires regeneration of that archived month.

## Release gate

Do not merge/deploy until:

- GitHub Python tests pass.
- GitHub Node OBS wheel animation tests pass; the wheel points to the preselected winner at the end.
- The GitHub .NET stub compile job passes.
- Actual installed Streamer.bot compiles every action.
- Real Twitch reward fulfillment/cancellation and chat event arg names are verified.
- A test-channel full workflow above passes and the backup can be restored.
- The original check-in OBS overlay is unchanged; only the optional second, hidden-by-default wheel source is added.

Document any integration differences found in your installed version of Streamer.bot.
There is no way for the GitHub workflow to authenticate against your local Streamer.bot/Twitch without a separate integration test environment.
