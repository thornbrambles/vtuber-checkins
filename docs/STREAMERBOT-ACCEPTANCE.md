# Streamer.bot acceptance test plan (requires a real local test instance)

Python sandbox/model tests and stubbed C# compilation are necessary but **not sufficient**.
Use a separate portable Streamer.bot installation and test Twitch account/channel,
and never run destructive tests on the actual viewer records.

## Setup

1. Use a disposable Streamer.bot data directory, a test broadcaster and test bot account.
2. Configure the Channel Points reward **🌿 Stream Check-in** to target the exact reward ID.
3. Install actions 01–16 from this PR and compile **each** in Streamer.bot.
4. Put all runtime actions, reviewer actions, migration and archive in one Blocking queue.
5. Register !checkins, !checkinboard, !wheel, !addgame, !wheeladmin, !checkindiag with matching permissions.
6. Start a synthetic test stream or use the test channel to produce genuine Twitch events.
7. Save a complete backup before test and after test; never import test data into production.

## Test cases / expected results

| Scenario | How to run | Must verify |
| --- | --- | --- |
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
- The GitHub .NET stub compile job passes.
- Actual installed Streamer.bot compiles every action.
- Real Twitch reward fulfillment/cancellation and chat event arg names are verified.
- A test-channel full workflow above passes and the backup can be restored.
- No OBS overlay changes are required for this upgrade.

Document any integration differences found in your installed version of Streamer.bot.
There is no way for the GitHub workflow to authenticate against your local Streamer.bot/Twitch without a separate integration test environment.
