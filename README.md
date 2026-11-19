# VTuber Check-ins

A Twitch Channel Points check-in system for Streamer.bot, with persistent lifetime counts, consecutive-stream streaks, milestones, moderator controls, an OBS leaderboard visible for 60 seconds after a successful check-in, and monthly game wheel credits (one submission per ten lifetime check-ins).

## Components

- `src/actions/`: Streamer.bot C# actions, manually installed in the Actions editor.
- `overlay/checkin-overlay.html`: OBS local browser source.
- `tools/export-wheel.py`: Exports wheel submissions to CSV.
- `docs/INSTALLATION.md`: Configuration instructions.

## Viewer commands

`!checkins` shows your count and credits; `!checkinboard` shows the leaderboard; `!game Game Title` submits a wheel entry; `!wheel` checks credits and `!wheel list` shows monthly entries.

## Admin commands

`!checkinadmin view @user`, `set @user N`, `add @user N`, `remove @user N`, `streak @user N`, and `reset @user`. Restrict this command to Twitch moderators and the broadcaster.

## Status

These scripts require manual installation and compilation in Streamer.bot. They have not been verified against a live Twitch/OBS installation.
