# Paladin Replay Launcher

A small native Windows launcher that opens an Age of Empires IV replay and puts the
user's game settings back exactly as they were afterwards.

The protection feature is **Paladin Shield**. It exists because launching replays the
way replay sites currently do — `steam.exe -applaunch 1466860 -dev -replay playback:<file>` —
is reported to reset or modify AoE4 user settings, particularly with `-dev` and with
replays recorded on older game builds.

```
User clicks WATCH REPLAY on the Paladin site
   -> paladin://replay?url=...
   -> launcher gets the replay file (local path, direct URL, or a future archive provider)
   -> Paladin Shield snapshots + backs up the protected settings, and verifies the backup
   -> replay is copied into the AoE4 playback folder
   -> Steam launches AoE4 into the replay
   -> launcher stays resident, watching the real game process
   -> game exits -> settings are re-hashed, compared, and any changes restored
   -> temporary replay and session data cleaned up
```

Status: **Phase 1, 2 and 3 are implemented and tested. Phase 4 needs one manual run on
a machine with AoE4 installed** — see [Phase 4](#phase-4--is--dev-required).

---

## Why C# / .NET 10 and not Rust

Rust was the stated preference and it would suit this job well. It was not used because
there is no Rust toolchain on the development machine (`cargo` and `rustc` are both
absent), so the first hour would have gone on a `rustup` install rather than on the
launcher. The .NET 10 SDK is already present and builds this solution offline in under a
second.

The practical trade is even: .NET gives Win32 known-folder resolution, registry access
and process monitoring in the base library with no third-party packages, publishes to a
single `.exe`, and has a straight path to a native WinForms window when the console
prototype is replaced. It is not Electron and it carries no browser runtime. If the
project later standardises on Rust, `Paladin.Core` is deliberately pure logic with no
Windows dependencies and would port more or less directly.

---

## Layout

```
./
  src/Paladin.Core/           net10.0      — all platform-independent logic, no deps
    Config/LauncherConfig     the protected-path list and every launch knob
    Logging/PaladinLog        line logger, console + file
    Model/                    SessionRecord, FileSnapshot, FileChange, RestoreResult
    Protocol/PaladinUri       paladin:// parsing and link building
    Replay/                   IReplayProvider + Local, DirectUrl, registry, validator
    Shield/                   PathGlob, ProtectionPolicy, Snapshot, ChangeDetector,
                              RestoreService, SessionStore, RecoveryTriage
    Steam/                    VdfParser, LaunchCommandBuilder
  src/Paladin.Launcher/       net10.0-windows — the executable
    Platform/Aoe4Locator      Steam, game and Documents discovery
    Platform/GameProcessMonitor
    Platform/ProtocolRegistrar
    SessionRunner             the ordered pipeline
    RecoveryRunner            crash recovery
    Program / CommandLineOptions / ShieldUi
  tests/Paladin.Tests/        net10.0 — 73 tests, zero packages
  web/paladin-test.html       local link-test page
```

`Paladin.Core` never references a Windows API. That is what makes the dangerous parts —
change detection, restore rules, recovery triage — testable without a game installed.

---

## Build, test, run

```bash
cd paladin-replay-launcher
dotnet build
```

```bash
dotnet run --project tests/Paladin.Tests
```

```bash
dotnet publish src/Paladin.Launcher/Paladin.Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o ./publish
```

That is the **required** publish form, not just the convenient one — see below. It
produces one ~37 MB `PaladinReplayLauncher.exe` that needs no .NET runtime installed,
which is also what you would hand to a user.

### Smart App Control blocks the framework-dependent build

Windows 11 Smart App Control (`HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy` →
`VerifiedAndReputablePolicyState = 1`) blocks unsigned binaries that have no cloud
reputation. A plain `dotnet publish` splits the app into `PaladinReplayLauncher.exe` plus
`PaladinReplayLauncher.dll`, and SAC lets the exe start but then blocks the DLL:

```
System.IO.FileLoadException: Could not load file or assembly '...\PaladinReplayLauncher.dll'.
An Application Control policy has blocked this file. (0x800711C7)
```

`dotnet run` fails the same way, for the same DLL.

A single-file self-contained publish avoids *that particular* failure, because the managed
assemblies load from inside the bundle rather than as separate files on disk.

**But it is not a reliable workaround, and an earlier version of this document overstated
it.** SAC's verdict is per-binary and reputation-driven, so it is not a stable rule you
can build against. Observed on one machine within a single afternoon:

- a split build's DLL — blocked;
- `dotnet run` — blocked, then later worked;
- a single-file build — ran fine;
- a *newly built* single-file exe, same code, minutes later — **blocked**, confirmed in
  the CodeIntegrity event log (id 3077, "did not meet the Enterprise signing level
  requirements", policy `{0283ac0f-…}`, which is the SAC policy);
- a downloaded, Mark-of-the-Web-tagged copy — ran fine.

Every fresh unsigned build is an unknown file, and whether it runs is effectively a coin
toss. So for **distribution this is a hard blocker, not friction**: shipping unsigned means
some users simply cannot run it, with an error message that tells them nothing useful. A
real Authenticode signature from a certificate SAC trusts is required. A self-signed
certificate will **not** satisfy SAC.

For **development**, use `dotnet run`, and re-publish/`--install` when you need the real
exe — if a new build is blocked, that is SAC, not your code.

Turning Smart App Control off is a one-way change — Windows cannot re-enable it without a
reinstall — so it must never be asked of a user.

For comparison: aoe4replays.gg's launcher is also unsigned and also carries an
un-cleared Mark-of-the-Web, and runs fine on the same machine. Reputation accrued over
many users appears to be doing that work, which a brand-new binary does not have.

The test project is unaffected and runs normally with `dotnet run`.

---

## Using it

Check what it found on this machine, changing nothing:

```bash
publish\PaladinReplayLauncher.exe --doctor
```

Launch a replay already on disk:

```bash
publish\PaladinReplayLauncher.exe "C:\replays\AgeIV_Replay_244989270"
```

Launch a replay from a direct URL:

```bash
publish\PaladinReplayLauncher.exe "https://example.com/replays/game.rec"
```

Rehearse the whole pipeline without starting the game:

```bash
publish\PaladinReplayLauncher.exe "C:\replays\game.rec" --dry-run
```

Install it (per-user, no admin) — do this once:

```bash
publish\PaladinReplayLauncher.exe --install
```

## Installing, and why it stays working

`--install` copies the exe to `%LOCALAPPDATA%\Programs\PaladinReplayLauncher\` and
registers `paladin://` from there. You can then delete whatever you downloaded.

This exists because of a real failure mode reported from using a comparable launcher:
*"sometimes I would restart the machine and would have to reinstall it again to make it
work."* A URI handler stores an **absolute path** to the exe. If that exe lives in
Downloads it can be moved, tidied, re-downloaded under a different name, or swept up by
Storage Sense — and then the registry points at nothing, browser links silently do
nothing, and the only visible symptom is that clicking WATCH REPLAY does not respond.

Two defences:

1. **A stable home.** `%LOCALAPPDATA%\Programs` is not a folder Windows tidies.
2. **Self-heal on every run.** Any ordinary run checks the registration and repairs it if
   it is missing or points at a file that no longer exists.

The self-heal is deliberately conservative. If the registration points at a *different
exe that does exist*, it is left alone — that is someone's deliberate install, and
silently stealing the association would be worse than the problem being solved. All three
cases are verified:

| Registration state | Behaviour |
|---|---|
| Missing entirely | Re-registered to the running exe |
| Points at a deleted file | Re-registered to the running exe |
| Points at a different, existing exe | **Left alone** |

`--repair` forces re-registration to the current exe. `--doctor` reports which of the
three states you are in. `--uninstall` removes the handler and tells you where the exe
and your backups are, without deleting either.

One caveat this does **not** fix: if the registration is wiped *and* you only ever click
browser links, nothing runs to repair it. Run the launcher once from the Start Menu or a
shortcut and it fixes itself. A logon-time repair task would close that gap and is a
reasonable future addition, but it is standing configuration and should be opt-in.

Finish an interrupted session's restore:

```bash
publish\PaladinReplayLauncher.exe --recover
```

`--help` lists every option. Others worth knowing: `--no-dev`, `--keep-replay`,
`--protect-extended`, `--config <path>`, `-y`, `-v`.

### Where it keeps things

```
%LOCALAPPDATA%\PaladinReplayLauncher\
  config.json                      every setting below, editable
  Logs\paladin-YYYYMMDD.log        rolling daily log
  Sessions\<session-id>\
    session.json                   metadata + restore_pending flag + hashes
    session.log                    just this session
    backup\                        the pre-launch copies, same structure as Documents
    quarantine\                    files the replay created, moved aside not deleted
```

---

## What is protected

Paths are relative to the AoE4 Documents folder
(`Documents\My Games\Age of Empires IV`, wherever Documents actually is). `*` matches
within one segment, `**` matches any number of segments below.

**Protected by default**

| Pattern | What it is |
|---|---|
| `configuration_system.lua` | System/graphics/audio configuration |
| `local.ini` | Local client settings |
| `keyBindingProfiles/**` | All `.rkp` keybinding profiles |
| `Users/*/configuration_user.lua` | Per-profile user configuration |
| `Users/*/cloud/configuration_user.lua` | The cloud-synced copy of the same |

On the development machine that resolves to 23 real files, about 1.7 MB — a snapshot and
verified backup takes well under a second.

**Opt-in only, via `--protect-extended` or `"ProtectExtended": true`**

| Pattern | Why it is not on by default |
|---|---|
| `Users/*/datastore/**` | Campaign progress, coat of arms, rogue mode. A user can legitimately play a campaign mission in the same AoE4 run as the replay; rolling that back would destroy real progress. |
| `Users/*/savedShoppingCart.sav` | Store state, not settings. |

**Never touched**, even if a pattern would otherwise catch them:
`**/*.log`, `LogFiles/**`, `Cache/**`, `playback/**`, `matchhistory/**`, `network/**`,
`Scratch/**`, `screenshots/**`, `warnings*.log`, `appisrunning.bin`.

Savegames are not in any list and are never read or written.

> **This list is a starting point, not a finding.** It was chosen from what actually
> exists on a live install plus the observation that the machine's owner had manually
> made ` - Copy` duplicates of exactly `configuration_system.lua`, `local.ini`,
> `configuration_user.lua` and the datastore files — which is a strong hint about what
> gets clobbered, but not proof. Widen it in `config.json` once a real `-dev` replay run
> shows what changes. `--doctor` prints the current effective list and every file it
> matches, so testing a pattern change needs no rebuild.

---

## Safety rules, and where they are enforced

| Rule | Enforced in |
|---|---|
| Never delete originals before a verified backup exists | `SnapshotService.WriteBackup` re-hashes every copy; `SessionRunner` aborts before launch if any file fails |
| Never write outside the protected paths | `RestoreService` re-checks `ProtectionPolicy.IsProtected` on every change before acting |
| Never restore from an unverified or rotted backup | `RestoreService.RestoreFromBackup` re-hashes the backup immediately before use |
| Atomic replacement | write to `.paladin-tmp`, then `File.Replace` |
| Never cross user or machine | `SessionStore.FindPendingRestores` filters on SID and machine name |
| Never use an old global backup | one fresh session snapshot per launch; there is no global backup to misuse |
| Never hard-delete a file the replay created | moved to `quarantine\`, path reported |
| A failed restore keeps everything and says where | `SessionRunner.CleanUp` returns early; the backup path is printed |

---

## Crash recovery

`restore_pending` is written to `session.json` **before** Steam is asked to launch, and
cleared only after every restored file has been re-hashed and matched. On every start —
including a plain replay launch — the launcher scans for its own unfinished sessions and
offers to finish them, before taking a new snapshot. That ordering matters: snapshotting
first would bake the damaged state in as the new "good" state.

Recovery is more cautious than a normal restore. It only considers sessions with a
matching SID and machine name, only touches files that still differ from their pre-launch
hash, and defers any file modified more than six hours after the session's last heartbeat
on the grounds that a later deliberate edit is then the likelier explanation. Those rules
live in `RecoveryTriage` in `Paladin.Core` and are unit tested.

Verified by simulation: a session was marked pending, its sandbox settings were then
overwritten, deleted and added to; `--recover` restored the modified file, recreated the
deleted keybinding profile, quarantined the created file, left an unprotected file alone,
and cleared the flag.

---

## Trying it

`web/paladin-test.html` is a small local page for exercising the whole chain by clicking,
the way a real site link would. Open it in a browser, put in a match id and the profile
ids of both players, and press the button.

It needs a match whose replay Microsoft still has. Replays are not kept forever, so an old
match may simply 404 — the launcher says so clearly rather than failing quietly.

Run `--install` once first, so the `paladin://` handler exists.

## How the Paladin website drives it

The site renders, on a match page:

```
▶ Watch Replay
🛡 Settings protected
```

which links to `paladin://replay?url=…&url=…` built by `getPaladinLauncherUrl` in
`src/lib/replayAvailability.ts`. AoE4Replays' own `aoe4rep://` option is kept alongside it
under "Other ways to watch" — not replaced, and their scheme is never registered or
hijacked.

The replay comes from **Microsoft's own public endpoint**, which the site already used for
its download fallback:

```
https://api.ageofempires.com/api/GameStats/AgeIV/GetMatchReplay/?matchId={gameId}&profileId={profileId}
```

No permission needed from anyone, and no scraping of aoe4replays.gg.

Two measured facts shape this:

1. **It serves gzip.** 447 KB compressed, 1.79 MB of real replay inside, magic `1f 8b 08`.
   The server labels it `Content-Type: application/zip` and names it `.gz` — the header
   and the extension disagree, so `ReplayArchive` detects by magic bytes and ignores both.
   Without decompression AoE4 would be handed a `.gz` and fail.
2. **Availability is per-participant.** The same match returns the replay for one player's
   profileId and 404 for the other's, and the endpoint rejects `HEAD` with 405 — so the
   page cannot cheaply discover which works without downloading the whole replay in the
   browser just to pick a link. Instead the link carries **both** URLs and the launcher
   tries each in turn. Verified end to end with the failing id deliberately placed first.

## Replay providers

`IReplayProvider` is the only thing the launcher core knows about replay acquisition.

| Provider | Status |
|---|---|
| `LocalReplayProvider` | Done. Uses a file already on disk, never moves or deletes it. |
| `DirectUrlReplayProvider` | Done. http/https only, size-capped, validates what it downloaded. |
| `Aoe4ReplaysProvider` | Not built. **Do not build it without aoe4replays.gg's permission** — the interface is here so that adding it is a registration, not a rewrite. |
| Relic/official API provider | Not built. Same shape. |

`ReplayValidator` refuses an HTML sign-in page, a JSON error body, an empty response or
anything under 1 KB, so the failure is a clear message rather than a confusing one inside
the game. It recognises the real AoE4 replay header — ASCII `AOE4_RE` at offset 4,
preceded by a `uint16` game build — measured from real files rather than assumed.

---

## What a real `-dev` replay launch actually changed

Measured on a live install, not assumed. Of 23 protected files, **4 changed**:

| File | Before | After |
|---|---|---|
| `configuration_system.lua` | 8072 b | 8072 b, different hash |
| `local.ini` | 5723 b | **5725 b** |
| `Users/<active-profile>/configuration_user.lua` | 12853 b | 12853 b, different hash |
| `Users/<active-profile>/cloud/configuration_user.lua` | 25706 b | 25706 b, different hash |

Restore put all four back and verified them. Two things worth noting: **keybinding
profiles were not touched**, and only the **active** Steam profile changed — 1 of the 10
under `Users/`. The default protected set is therefore wider than the observed blast
radius, which is the safe direction, but `keyBindingProfiles/**` is unproven rather than
validated.

### The control run settled it: those four are bookkeeping

A second run with `--observe` — an ordinary AoE4 launch, **no replay and no `-dev`** —
changed **the same four files**. Because changed copies are now kept before restoring,
the actual differences could be read:

| File | The entire difference |
|---|---|
| `configuration_system.lua` | `last_modified` timestamp |
| `configuration_user.lua` | `last_modified` timestamp |
| `.../cloud/configuration_user.lua` | `last_modified` timestamp |
| `local.ini` | `app_runs_count` 1000→1001, `most_recently_viewed_profile_id` |

Not one actual setting. Age of Empires IV rewrites these on **every** launch.

That makes a plain content hash the wrong detector: it reports "your settings changed"
100% of the time, and restoring on that signal reverts the game's own housekeeping — and
would revert a setting the user deliberately changed during the session.

So each protected **text** file now carries a second, *semantic* hash, computed with
known bookkeeping keys removed (`VolatileKeys` in config.json). Changes are classified:

- **Modified** — content differs even ignoring bookkeeping. Restored.
- **BookkeepingOnly** — only timestamps and counters differ. Reported, never touched.
- **Created / Removed** — always treated as real.

Binary files such as `.rkp` keybindings get no semantic hash, and any difference there
counts as real. The fallback everywhere is "assume it matters".

Verified against the real files from that run: all four differ by raw hash and are
identical by semantic hash, so the new detector reports no settings change where the old
one reported four.

**What is still unknown:** the owner reports that settings *have* genuinely changed at
times in the past. That damage is intermittent and has not yet been caught in the act.
The Shield is now quiet enough to make the real signal visible when it happens — which is
the point. Old-build replays remain the most likely trigger and are untested.

### The earlier caveat, kept for the record

Three of the four files changed content while keeping byte-identical size. That reads
like small in-place field edits, not a settings reset — and AoE4 plausibly rewrites its
config on *every* launch (last-played, window position, session counters). Without a
control run there is no way to tell "the replay damaged settings" apart from "Paladin is
reverting routine bookkeeping".

That distinction matters, because if it is bookkeeping then the Shield is also reverting
any settings change the user deliberately made *during* the replay session.

Two features exist to close the gap:

**`SaveChangedCopies`** (on by default) copies each changed file to `<session>/changed/`
*before* restoring, so the modified version survives for diffing. Restoring otherwise
destroys the only copy of the evidence.

**`--observe`** is the control experiment:

```bash
publish\PaladinReplayLauncher.exe --observe
```

It snapshots, then waits while you launch and close AoE4 **normally** — it launches
nothing itself — reports what changed, and asks before restoring. The difference between
that file list and a replay run's list is the real effect of `-dev`/replay launching.

Then diff the two copies to see what the bytes actually are:

```bash
fc /b "%LOCALAPPDATA%\PaladinReplayLauncher\Sessions\<id>\changed\local.ini" "%LOCALAPPDATA%\PaladinReplayLauncher\Sessions\<id>\backup\local.ini"
```

## Phase 4 — is `-dev` required?

Not yet answered, and it cannot be answered without launching the game.

The launch string is fully templated, so both forms can be tested without a rebuild:

```bash
publish\PaladinReplayLauncher.exe "C:\replays\AgeIV_Replay_244989270"
```
→ `-applaunch 1466860 -dev -replay playback:AgeIV_Replay_244989270`

```bash
publish\PaladinReplayLauncher.exe "C:\replays\AgeIV_Replay_244989270" --no-dev
```
→ `-applaunch 1466860 -replay playback:AgeIV_Replay_244989270`

Both strings are asserted in the test suite. Run each once and record whether the replay
actually opens; the Shield works identically either way. `LaunchArgumentTemplate` and
`ReplayArgumentTemplate` in `config.json` cover any other form worth trying.

---

## paladin:// links

```
paladin://replay?url=https%3A%2F%2Fexample.com%2Fgame.rec     -> DirectUrlReplayProvider
paladin://replay?path=C%3A%5Creplays%5Cgame.rec               -> LocalReplayProvider
paladin://replay/244989270                                    -> archive provider (not built)
paladin://replay?id=244989270&source=relic                    -> named provider
```

Registration writes to `HKCU\Software\Classes\paladin`, so it needs no administrator
rights. `--unregister-protocol` removes it. If the exe moves, re-run
`--register-protocol` from the new location.

Everything arriving through a link is untrusted: only `replay` is a valid action, only
http/https URLs are accepted, and a downloaded file name is stripped of any directory
component before use.

`web/paladin-test.html` is a local page with WATCH REPLAY buttons for each form. Open it
from disk; it is a test fixture, not part of the Paladin site.

---

## Coexisting with the aoe4replays.gg launcher

That launcher is a separate program and Paladin does not touch, replace or interfere
with it. Confirmed on a machine with both installed:

| Scheme | Handler |
|---|---|
| `aoe4rep://` | `aoe4_replay_launcher.exe` (aoe4replays.gg) |
| `paladin://` | `PaladinReplayLauncher.exe` |

Different schemes, different `HKCU\Software\Classes` keys. Installing or uninstalling
either leaves the other alone. Both can be installed at once; a user clicking a link on
aoe4replays.gg gets theirs, and a link on Paladin gets ours.

Two useful things learned from having it side by side:

- **Unsigned downloads do work here.** Their exe is unsigned and still carries an
  un-cleared Mark-of-the-Web, and it runs fine on this Smart App Control machine. That
  matches the MOTW test done on our own build, and downgrades code signing from a
  blocker to a friction/trust issue.
- **Their install model is the one to copy**: drop the exe anywhere, run it once, it
  self-registers, and moving it means running it once more. That is exactly the
  first-run registration this project still needs.

Their launcher is open source at `github.com/aoe4replays-gg/launcher`. Reading it would
settle the Phase 4 question — whether `-dev` is actually required — without guessing, and
is a fair thing to do with deliberately published code. **Taking over their `aoe4rep://`
scheme so Paladin could protect their links too is technically one registry write, but
would be hijacking another project's protocol and would break whenever they change their
format. If that is ever wanted, ask them first.**

## Notes, assumptions and open questions

1. **`playback:<name>` with no extension.** On a live install, replays downloaded by
   replay sites sit in `playback\` with *no* file extension (`AgeIV_Replay_207502139`)
   while game-recorded ones are `playback\replays\*.rec`. The launcher therefore strips
   the extension by default (`StripReplayExtension`). Whether AoE4 also accepts
   `playback:name.rec` is untested.
2. **Whether `-dev` actually damages settings is still unproven.** The whole Shield is
   built on a report, not a measurement. The first real `-dev` run will produce the
   evidence — the session log lists every changed file by name and hash.
3. **The protected list is provisional.** See the warning above.
4. **Game process name.** `RelicCardinal.exe` was verified on a live install.
   `AoE4`/`AoEIV` are configured as fallbacks in case a future patch renames it, and
   the monitor falls back from exe-path matching to name matching when a process's
   module path is unreadable.
5. **Multiple AoE4 processes.** The monitor waits for its own process *and* then for all
   matching processes to disappear, since `-dev` is capable of relaunching the game.
6. **OneDrive-redirected Documents is handled and was the actual case on the development
   machine** — Documents resolved to a folder with an unrelated name. Detection goes
   through the shell's known-folder API with the User Shell Folders registry value and
   OneDrive environment variables as fallbacks. A hard-coded `%USERPROFILE%\Documents`
   would have failed outright.
7. **The UI is a console app.** `IShieldUi` is the seam; a WinForms window implementing
   it needs no change to the Shield or session logic.
8. **No installer yet.** The publish output is one xcopy-deployable exe plus one
   `--register-protocol` call. An installer will eventually need a code-signing
   certificate anyway — see the Smart App Control note above.
9. **Cloud config copies.** `Users/*/cloud/configuration_user.lua` is protected as well
   as the non-cloud copy. If Steam Cloud re-syncs a changed file back down after the
   launcher restores it, the restore could be undone. Untested, and worth checking on
   the first real run.

---

## Verified on the development machine

| Check | Result |
|---|---|
| Test suite | 73 passed, 0 failed |
| Steam / library / game exe detection | Steam on `C:`, AoE4 on a second `D:` library, `RelicCardinal.exe` found |
| Documents detection | Resolved a OneDrive-redirected, non-standard Documents path |
| Snapshot + verified backup | 23 files, 1.7 MB |
| Full dry run, local replay | Replay placed, no false-positive changes, cleaned up |
| Full dry run, URL replay | Downloaded, `Content-Disposition` name honoured, placed |
| Bad downloads | HTML sign-in page and 404 both refused with a clear message |
| Crash recovery | Modified restored, deleted recreated, created quarantined, unprotected untouched |
| `paladin://` handler | Registered per-user; link invocation and a `file://` smuggling attempt both behaved |
| Smart App Control | Split build blocked; single-file self-contained build runs clean |
| Live AoE4 replay launch with `-dev` | Ran. 4 of 23 protected files changed; all 4 restored and verified |
| Control run (normal launch, no replay) | **Not run** — `--observe` exists for it; needed to interpret the above |
| `-replay` without `-dev` | **Not run** (Phase 4) |


## Old replays, and what this does about them

Age of Empires IV refuses a replay recorded on a build it no longer matches. The launcher
reads both numbers before downloading anything — the replay header stores a build at
offset 2, and `RelicCardinal.exe` reports `16.3.<build>.0`, confirmed identical on a live
install — and says so plainly instead of letting the failure surface inside the game.

It is advisory. Relic state that not every patch breaks replays, so a mismatch is a
reason to warn, never to refuse.

**What it will not do: switch your game build.** Steam publishes a `previous_live` branch
("Archive of previous live build", no password) holding the single build before the
current one, and rolling back to it is four clicks in Steam's own UI. The launcher tells
you how, but does not do it for you, because:

- Steam offers no supported command or URL for switching branches, so automating it means
  writing Steam's own `appmanifest` and forcing an update. Getting that wrong damages a
  48 GB install.
- It replaces the live install rather than making a copy — you would re-download in both
  directions and could not play current-patch multiplayer until you switched back.
- A tool whose entire purpose is not touching your files without asking should not
  silently re-version your game to watch a replay.

For a replay that is **many** patches old, `previous_live` cannot go back far enough, and
the launcher says so rather than suggesting a pointless rollback. Reconstructing an
arbitrary historical build is a real project — [EKYavsil's AoE4 Replay
Launcher](https://github.com/EKYavsil/AoE4-Replay-Launcher) does it with DepotDownloader,
restic deduplication and hardlinked composition, at roughly 55 GB on disk. Separate
project, not affiliated with this one.

### Note for developers on Smart App Control machines

`dotnet run` on the test project can fail with `0x800711C7` when Smart App Control blocks
the freshly built `Paladin.Core.dll`. The suite runs fine as a single file, which bundles
it:

```bash
dotnet publish tests/Paladin.Tests/Paladin.Tests.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./.testpub
./.testpub/Paladin.Tests.exe
```

CI is unaffected — GitHub's runners do not enforce Smart App Control.
