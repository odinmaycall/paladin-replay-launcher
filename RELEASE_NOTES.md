# Paladin Replay Launcher 0.3.0

Opens an Age of Empires IV replay and puts your game settings back exactly as they were
afterwards.

## Unreleased

- The game comes to the front on its own. When Age of Empires IV goes fullscreen a
  few seconds after launch, whatever holds the foreground at that instant used to
  win, and the game dropped to the taskbar behind a black screen until you found it
  with Alt+Tab. The launcher now brings its window to the front when it appears and,
  for the first 30 seconds, brings it back if it gets minimised. It never fights a
  deliberate Alt+Tab. `BringGameToFront` in the config turns it off.
- Recovery after an interrupted session restores your settings on its own, the way
  every normal session ends, instead of asking. When the only differences are the
  timestamps and run counters the game rewrites on every launch, it says so and
  restores nothing, rather than asking about files it was never going to touch.
- The console no longer prints the full path to your game folder. It shows
  `~\...\My Games\Age of Empires IV` instead, so a screenshot or a stream of the
  launcher window never carries your Windows username or the folders your
  Documents live in. The full path is still written to the log file.

## New in 0.3.0 — it is code-signed

This is the first signed build. Windows no longer needs to guess whether to trust it:

- **SmartScreen** may still show a reputation notice on a brand-new signed file for a
  little while; that fades as downloads accrue.
- **Smart App Control** accepts a validly signed file, so the `0x800711C7` block that
  stopped some people running 0.2.0 no longer applies.

The publisher shown by Windows is **SignPath Foundation**, because the certificate is
theirs — see the code signing policy below. Check a download yourself:

```powershell
Get-AuthenticodeSignature .\PaladinReplayLauncher.exe | Format-List Status, SignerCertificate
```

`Status` must read `Valid`. `SHA256SUMS.txt` on this page is the hash of the exact file
that was signed.

Also new: `--install` now says what it is about to change (one folder, one per-user
registry key) before it does it.

**Windows 10/11, 64-bit. Steam only** (no Game Pass — the launch path goes through Steam).
No .NET runtime needed; everything is in the one file.

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

Every release is built by this repository's own release workflow from a tagged commit
and signed from that automated build; a tag that cannot be signed is refused.
Committers, reviewers and approvers: [OdinMayCall](https://github.com/odinmaycall).

Privacy: this program will not transfer any information to other networked systems
unless specifically requested by the user or the person installing or operating it. Its
only network request is downloading the replay named in the link you clicked. No
telemetry. Full policy: [README — Code signing policy](README.md#code-signing-policy).

## Install

1. Download `PaladinReplayLauncher.exe`.
2. Open a terminal where you saved it and run it once:

   ```
   PaladinReplayLauncher.exe --install
   ```

   It tells you what it is about to do, then copies itself to
   `%LOCALAPPDATA%\Programs\PaladinReplayLauncher\` and registers the `paladin://` link
   handler for your user account. No administrator rights are needed.
3. You can then delete the file you downloaded.
4. Check it found everything:

   ```
   PaladinReplayLauncher.exe --doctor
   ```

## From 0.2.0 — it tells you when a replay is too old to play

Age of Empires IV refuses replays recorded on an older build, with *"Due to a recent
update, the replay is no longer available"* — which appears **inside the game**, minutes
after you clicked, with nothing to act on.

The launcher reads the build out of the replay header and compares it with your
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

It is advisory and never blocks: Relic state that not every patch breaks replays, so the
launcher lets it try. For a replay that is many patches old it says so plainly, rather
than sending you on a pointless rollback — `previous_live` only ever holds the single
build before the current one.

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
