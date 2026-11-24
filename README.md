# VTuber Check-ins

A Twitch Channel Points check-in system for Streamer.bot, with persistent lifetime counts, consecutive-stream streaks, milestones, moderator controls, an OBS leaderboard visible for 60 seconds after a successful check-in, and monthly game wheel credits (one credit per ten lifetime check-ins, up to three submissions per viewer each month, with moderator review).

## Reliability and operations

Check-ins use a pending recovery journal and permanent monthly redemption receipts
to prevent lost credit awards and duplicate processing across streams. Update the
related accounting actions together; see [recovery and tests](docs/OPERATIONS.md#check-in-recovery-and-retry-receipts).
The C# checks target .NET Framework 4.8.1 and exercise actual action bodies with
interrupted writes. Compile against your installed Streamer.bot assemblies and run
the Twitch/OBS acceptance checklist before using the changes live.

- [Operational runbook](docs/OPERATIONS.md): backups, dry-run restore, diagnostics, reconciliation, moderation, safe upgrades and production test gates
- [Central configuration](config/checkins.json): authoritative milestones, reward interval and command registration intent
- [Offline synthetic sandbox](tools/checkin-ops.py): simulated streams, game review and credits, separate from production Streamer.bot
- [Local admin dashboard](tools/admin-dashboard.py): browse a private backup and export approved games. Moderators make updates in Twitch chat, including between broadcasts.
- [Real Streamer.bot acceptance checklist](docs/STREAMERBOT-ACCEPTANCE.md): redemption, duplicate delivery, month rollover, moderator review and backup validation
- [Full chat command reference](docs/COMMANDS.md): includes moderator `!wheeladmin` controls

## Components

- `src/actions/`: Streamer.bot C# actions, manually installed in the Actions editor.
- `overlay/checkin-overlay.html`: Existing OBS attendance browser source (unchanged).
- `overlay/game-wheel-overlay.html`: Separate invisible-by-default, on-demand spinning wheel browser source controlled by Streamer.bot.
- `tools/export-wheel.py`: Exports wheel submissions to CSV.
- `docs/INSTALLATION.md`: Configuration instructions.

## Viewer commands

Type `!checkinhelp` in Twitch chat to see the enabled viewer commands and how to check in.

See [the full command catalog](docs/COMMANDS.md) for usage, permissions, examples and effects.

`!checkins` shows attendance and credits. `!addgame Game Title` spends one earned credit and queues a game for moderator approval (maximum three per viewer per Eastern calendar month). `!wheel` checks credits and submitted slots; `!wheel list` shows only approved games. Rejected titles can be replaced without another credit or monthly slot in the same month.

## Admin commands

`!checkinadmin view @user`, `set @user N`, `add @user N`, `remove @user N`, `streak @user N`, and `reset @user`. `!wheeladmin approve ID` / `reject ID` review game submissions; rename, remove and restore are also available. `!spinwheel` starts the prior month’s draw for moderators/broadcaster; a separate OBS overlay animates the saved result. Restrict these commands to Twitch moderators and the broadcaster.

## v3 migration, backup and offline debug

**Running v3 today?** Go directly to [v3 migration / backup / debug instructions](docs/V3-MIGRATION-BACKUP-DEBUG.md). There is no need to install v4. Manual-only tools include `09-export-data.cs`, `10-import-data.cs` (dry run by default), and `11-debug-admin.cs` (disabled and dry run by default). No commands or triggers are automatically registered.

## Wheel ledger and credits

v5 stores game entries in monthly persisted variables (`ci_wheel_month_YYYY_MM`). Game credits are earned at each new ten-check-in high-water threshold and used credits are not reset by moderator edits. Existing v4 users **must follow the one-time migration guide** in [docs/REFRACTOR.md](docs/REFRACTOR.md) before deploying the new wheel scripts. See [installation instructions](docs/INSTALLATION.md).

## Status

These scripts require manual installation and compilation in Streamer.bot. They have not been verified against a live Twitch/OBS installation.
