# Paladin Replay Launcher 0.2.0

Opens an Age of Empires IV replay and puts your game settings back exactly as they were
afterwards.

## New in 0.2.0 — it tells you when a replay is too old to play

Age of Empires IV refuses replays recorded on an older build, with *"Due to a recent
update, the replay is no longer available"* — which appears **inside the game**, minutes
after you clicked, with nothing to act on.

The launcher now reads the build out of the replay header and compares it with your
installed game before anything is downloaded:

```
[warn] This replay was recorded on game build 10884, and Age of Empires IV is now
       on 11308. It may refuse to play with "Due to a recent update, the replay
       is no longer available".
       It may still play - not every patch breaks replays. Let it try first.
       If it fails, you can roll the game back one build in Steam:
         right-click Age of Empires IV -> Properties -> Betas -> previous_live
       Switch back to 'None'/public afterwards to play multiplayer again.
```

This matters most **the day a patch lands**, when every existing replay is suddenly one
build behind — exactly when you want to review tournament games from the old patch.

It is advisory and never blocks: Relic state that not every patch breaks replays, so the
launcher lets it try. For a replay that is many patches old it says so plainly, rather
than sending you on a pointless rollback — `previous_live` only ever holds the single
build before the current one.

Both numbers are read, not guessed: the replay header stores a build at offset 2, and
`RelicCardinal.exe` reports `16.3.<build>.0`. Confirmed identical on a live install.

**Windows 10/11, 64-bit. Steam only** (no Game Pass — the launch path goes through Steam).
No .NET runtime needed; everything is in the one file.

## Install

1. Download `PaladinReplayLauncher.exe`.
2. Open a terminal where you saved it and run it once:

   ```
   PaladinReplayLauncher.exe --install
   ```

   It copies itself to `%LOCALAPPDATA%\Programs\PaladinReplayLauncher\` and registers the
   `paladin://` link handler for your user account. No administrator rights are needed.
3. You can then delete the file you downloaded.
4. Check it found everything:

   ```
   PaladinReplayLauncher.exe --doctor
   ```

## Read this before you install: it is not code-signed

This build has no Authenticode signature, so Windows may refuse to run it.

- **SmartScreen** will likely show *"Windows protected your PC"*. Choose **More info →
  Run anyway**.
- **Smart App Control**, if enabled, may block it outright with
  `0x800711C7 — An Application Control policy has blocked this file`. There is no
  workaround from our side, and turning Smart App Control off is a one-way change that
  Windows cannot undo without a reinstall, so **please do not disable it**. If you hit
  this, the launcher is not usable for you yet; a signed build is what fixes it.

Whether an unsigned build is allowed is reputation-driven and varies between machines and
between builds. Signing is the next thing on the list.

## What it does

```
Click WATCH REPLAY on a Paladin match
   -> the launcher downloads the replay
   -> backs up your AoE4 settings and verifies the backup
   -> puts the replay where AoE4 expects it
   -> launches the game into the replay through Steam
   -> waits while you watch
   -> checks your settings when the game closes, and restores anything that really changed
   -> cleans up
```

An **authentication error in AoE4 when the replay starts is normal** in replay/dev mode.
Click OK; the replay plays. That is the game, not the launcher.

## What "Settings protected" actually means

Protected by default, under `Documents\My Games\Age of Empires IV`:

| Path | |
|---|---|
| `configuration_system.lua` | system/graphics/audio settings |
| `local.ini` | local client settings |
| `keyBindingProfiles/**` | all your keybinding profiles |
| `Users/*/configuration_user.lua` | per-profile settings |
| `Users/*/cloud/configuration_user.lua` | the cloud-synced copy |

Campaign progress, coat of arms and savegames are **not** touched by default. You can
legitimately change those in the same session as a replay, and rolling them back would
destroy real progress.

Measured on a live install: Age of Empires IV rewrites four of those files on **every**
launch, replay or not, changing only `last_modified` timestamps and `local.ini`'s
`app_runs_count` / `most_recently_viewed_profile_id`. The launcher recognises that as
bookkeeping and leaves it alone, so it only acts on a real settings change. Without that
distinction it would "restore" something after every single launch — including any setting
you deliberately changed while watching.

## Safety

- Nothing is launched until a backup exists and every file in it has been re-hashed to
  match.
- Only the protected paths above are ever written; nothing else is read or changed.
- Files the session creates are moved to a quarantine folder, never deleted.
- Each launch gets its own fresh snapshot. There is no old global backup that could be
  written over newer settings.
- If a restore fails, the backup is kept and its location is printed.
- If the game or the launcher crashes, or the PC loses power, the next run offers to
  finish the restore. It only touches files that still differ, and leaves alone anything
  changed long afterwards.

Backups, logs and session records live in `%LOCALAPPDATA%\PaladinReplayLauncher\`.

## Useful commands

| | |
|---|---|
| `--doctor` | what was detected, what is protected, changes nothing |
| `--install` | install and register the link handler |
| `--repair` | re-register `paladin://` to this exe |
| `--recover` | finish an interrupted session's restore |
| `--observe` | snapshot, wait while *you* launch AoE4 normally, report what changed |
| `--dry-run` | do everything except launch the game |
| `--no-dev` | launch without the `-dev` flag |
| `--uninstall` | remove the link handler |
| `--version` | print the version |

## Known limitations

- **Unsigned** — see above. The main thing standing between this and general use.
- **Steam only.** Game Pass installs are not supported.
- Whether `-dev` is genuinely required for replay playback is **not yet established**.
  `--no-dev` exists to test it.
- Settings damage has been reported but **not yet reproduced** in testing. Every launch
  observed so far changed only bookkeeping. The launcher keeps a copy of any changed file
  before restoring, so if it does happen the evidence survives.
- The interface is a console window. A proper window comes later.
- Does not interfere with the AoE4Replays.gg launcher; they use different link types and
  can both be installed.

## Uninstall

```
PaladinReplayLauncher.exe --uninstall
```

Then delete `%LOCALAPPDATA%\Programs\PaladinReplayLauncher\`. Your backups and logs in
`%LOCALAPPDATA%\PaladinReplayLauncher\` are deliberately left for you to remove yourself.
