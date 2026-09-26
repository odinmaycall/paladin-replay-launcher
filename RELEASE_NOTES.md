# Paladin Replay Launcher 0.5.7

Opens an Age of Empires IV replay and puts your game settings back exactly as they were
afterwards.

## New in 0.5.7

- **A capture now also records what the game itself says your villagers are doing.** Until now a
  capture worked out each villager's job by asking about that villager — which the game answers with
  "nothing" while they are still walking to a tree or a stone outcropping. That is why a build order
  sometimes showed a group of villagers as unresolved for a minute before they appeared on a
  resource. The game keeps its own count, the one your resource panel shows, and a capture now
  records that alongside the per-villager readings so the two can be lined up. Nothing changes in how
  you capture, and a capture takes no longer.

## New in 0.5.6

- **Installing it now tells Paladin straight away, instead of waiting for your next capture.** Paladin
  only ever found out what your launcher could do at the end of a successful capture, so the moment
  after you installed a newer one the page still described the older one — and could tell you to
  install a launcher you had just installed. Installing now says so immediately, and if you already
  installed it, the page offers an **"Already installed it? Tell Paladin"** link that takes about as
  long as a click. Nothing is captured, no replay is opened, and nothing about your game is touched.

## New in 0.5.5

- **Paladin only offers to finish a capture when this launcher can actually finish it.** Finishing
  an unfinished capture needs two things at once: this launcher has to accept a game Paladin
  already has, and it has to keep the replay afterwards. Until now it claimed both with one word,
  so a build that could do the first but not the second still said it was ready — and the page
  offered to finish a capture that would come back just as unfinished, for another five minutes of
  replay, as often as you were willing to try. This build says the two things separately, and a
  launcher that cannot do both is told to update instead of being sent round again.
- **Keeping your replay now genuinely works, and in 0.5.2 and 0.5.3 it never did.** Keeping the
  replay arrived in 0.5.2 and failed every time: the launcher removed the replay from your game
  folder on its way out and only then tried to send it, so every capture announced that it was
  keeping your replay and then reported that the file could not be found. The capture itself was
  always safe — the game simply stayed unfinished. This was fixed in the build published as 0.5.4,
  whose notes were written before the fix landed and so never mentioned it; it is named here
  instead. If you captured anything with 0.5.2 or 0.5.3, open those games on Paladin and finish
  them in one click. You do not need to watch them again.

## New in 0.5.4

- **It tells you which version you are looking at, and that opening it installs nothing.** Opening
  the downloaded file prints the help screen — it does not install anything, and until now that
  screen never said which build it belonged to. So it was possible to download a new version
  several times, open it each time, and have no way to tell that nothing had changed. The header
  now carries the version and says plainly that `--install` is the step that does the installing.
- **Installing says what it replaced.** `--install` now reports the version it put in place and the
  one it replaced, so an upgrade is visibly an upgrade.

## New in 0.5.3

- **Finishing an unfinished capture is one click again.** If Paladin has part of a game's evidence
  but not all of it — usually a capture made before 0.5.2, whose replay was never kept — its page
  now offers to capture it again, and this launcher accepts that instead of refusing a game Paladin
  already has. Before, the only way through was a command line. A game whose evidence is already
  complete is never re-captured: the page only asks when there is something genuinely missing.

## New in 0.5.2

- **Your replay is kept with Paladin, so the build order keeps working.** A capture reads what every
  villager was doing, but the clock on each step, the Builders column and who placed each landmark
  all come from the replay itself — and Microsoft stops handing replays out about three months
  after the game. Deep Capture now keeps a copy of the replay it just played, at the one moment it
  is certainly still available, and Paladin reads it with its own parser. Nothing you do changes;
  the capture just stops going stale. If keeping it fails, the capture is still safe and the game
  can be completed later without playing it again.

## New in 0.5.1

- **A capture of a short game is no longer called a failure.** Deep Capture ran to 15:00 and
  refused anything shorter — but about a fifth of games end before 15:00, so a capture that
  followed one of those to its final second was reported as incomplete and thrown away. The
  launcher now asks Paladin how long the match actually ran and captures to the end of it. A
  game that runs past 15:00 is unchanged, and a capture that stops early in a long game is
  still refused, because that one really is missing part of the build order.

## New in 0.5.0

- **Deep Capture.** The launcher can now play a replay back with Paladin watching and read what
  every villager is actually doing, rather than working it out afterwards from the order log.
  Start it from the **Deep Capture this game** button on any game's page on Paladin, or with
  `--deep <gameId>` from a terminal. It takes a few minutes, needs the machine to itself, and
  when it finishes it sends the result to Paladin and opens that game's build order for you.
- **It checks the setup landed before committing to a capture.** The game's console drops a
  pasted line now and then — measured at four launches in twenty. Until now that produced a
  replay that ran its full fifteen minutes and printed nothing, and only said so at the end.
  The launcher now asks the game whether everything arrived, re-sends whatever was lost, and
  only then starts capturing. A dropped line costs two seconds instead of a capture.
- **The replay is never left paused.** Setting up stops game time so it costs nothing; every way
  out of that — including a failure — starts it again before the launcher gives up.
- **Captures are compressed on the way up.** A fifteen-minute capture is about a megabyte of
  readings and goes up as about 64 KB.
- **The launcher tells the site what it can do.** Paladin cannot see which programs are
  installed, so the Deep Capture button is offered to everyone and simply does nothing if the
  launcher is missing. After a capture, the launcher says which version it is and what it
  supports, so Paladin can stop offering it where it would not work.

## New in 0.4.1

- **The window stays until you have read it.** Started from a `paladin://` link the launcher
  owns the console window Windows opens for it, so the window closed the instant the run
  returned. Every fast outcome therefore looked the same: a black window that flashed and
  was gone. It bit twice — once on "Dump this game" against an older launcher, which
  answered "Unsupported paladin action 'dump'" and exited, and once on "Dump, then watch",
  which was not broken at all and was correctly refusing a game that already had its world
  layer. Neither could be read. The launcher now holds the window after printing, and waits
  for Enter.
- **Only where somebody is there to close it.** The hold is skipped when either stream is
  redirected, when `--yes` or `--observe` is passed, and when the console belongs to a shell
  you already had open. An unattended queue runs this program for hours as the Shield, and a
  blocking read there would hang the night.

## New in 0.4.0

- **Dump this game.** A Paladin match page whose map panel has no World layer now offers
  a second button beside WATCH REPLAY. It starts that game's replay on your PC, reads the
  map's objects out of the game's own developer console and sends those rows to Paladin,
  which checks them against its own data for that game and draws the World layer from
  then on. About two minutes, hands off: you click it, and the next thing you do is
  reload the page.
- **What that sends, and when.** Only on your click, and only the map's printed rows plus
  eight lines of heading: every object's blueprint name, position, entity and squad id
  and owner; the two players' names and civs as the game prints them; the game build; the
  map's biome, layout, size, seed and player count; the `RUN-OPTIONS` line, which names
  only the replay; the launch's local date and minute; and the launcher's version, the
  keyboard chord and input method it used, and its step timings. Never your Windows user
  or computer name, the working directory, the install path, your locale, your Steam
  account, your settings, the replay itself or either of the game's log files — a line of
  any of those forms is refused by the builder even if one were ever picked up. The exact
  text that left the machine is kept in the session folder so you can read every byte of
  it. `--dump <id> --no-upload` does the whole run and sends nothing at all.
- **It tells you before it starts.** The console lists what will happen and what will be
  sent, then counts five seconds down: Enter starts it now, Ctrl+C stops it. `--yes`
  skips the countdown for a scripted run.
- **Hands off while it runs.** The lines are typed into the game's console, so another
  window taking the focus mid-line would leave half a line in it. The run watches the
  foreground before and after every line and stops with a plain message rather than
  retyping anything. Whatever it printed is kept either way, and your settings are always
  restored.
- **The `-dev` launch's "Account Authentication" message is normal** and needs no answer;
  the dump runs with it on screen, as it always has.
- **The launcher closes the game itself** once the rows are safe. A replay has nothing to
  save, and waiting for the game to unload added over a minute to every run. Ctrl+C mid-run
  closes it the same way, before your settings are checked and put back — a `-dev` game
  left running would write its own settings file back over the restore.
- **Your clipboard is borrowed and given back.** Each console line goes in by paste; the
  text you had on the clipboard is saved first and put back at the end. If it was empty,
  it is emptied again rather than left holding a console line. An image cannot be put
  back, and the console says so rather than pretending otherwise — as it does in the one
  case where Windows refuses to give the clipboard back at all.
- **If Paladin cannot be reached**, the rows are kept and
  `PaladinReplayLauncher.exe --dump-upload <session-id>` sends them later, with no game
  and no second two minutes.
- **Bringing the game to the front no longer starts with a keypress.** It used to mean
  pressing Alt, which is the documented way to get Windows to allow it. If you have the
  game's own **UI element narration** accessibility option switched on, that Alt landed on
  the loading screen and the game started reading its interface out loud. The launcher now
  asks Windows politely first, then checks whether the game has come forward on its own,
  then asks again while sharing the foreground window's input — and presses Alt only if
  all of that is refused. Your game settings are not touched, then or ever; the fix is
  that the launcher reaches for the keyboard less.
- **Ctrl+C in the first seconds now waits for the game rather than leaving it.** Stopping
  a dump in the moment between "Launching Age of Empires IV" and the game actually
  appearing used to restore your settings and exit while the game was still on its way, so
  it opened a few seconds later with nothing behind it. For 25 seconds after the launch
  the launcher now waits for that game and closes it; if it still has not appeared it
  tells you in one line instead of leaving you to find out. Stopping later, once it is
  clear the game is not coming, exits straight away as before, and stopping a plain replay
  is unchanged: that game is yours and it stays.

**New flags:** `--dump <game-id>`, `--dump-upload <session-id>`, `--no-upload`,
`--squads`, `--force`, `--watch`, `--yes`. `--dry-run` works with a dump too and launches
nothing.

## New in 0.3.0

- **The game comes to the front on its own.** When Age of Empires IV goes fullscreen a
  few seconds after launch, whatever holds the foreground at that instant used to
  win, and the game dropped to the taskbar behind a black screen until you found it
  with Alt+Tab. The launcher now brings its window to the front when it appears and,
  for the first 30 seconds, brings it back if it gets minimised. It never fights a
  deliberate Alt+Tab. `BringGameToFront` in the config turns it off.
- **Recovery after an interrupted session restores your settings on its own**, the way
  every normal session ends, instead of asking. When the only differences are the
  timestamps and run counters the game rewrites on every launch, it says so and
  restores nothing, rather than asking about files it was never going to touch.
- **The console no longer prints the full path to your game folder.** It shows
  `~\...\My Games\Age of Empires IV` instead, so a screenshot or a stream of the
  launcher window never carries your Windows username or the folders your
  Documents live in. The full path is still written to the log file.
- `--install` says what it is about to change (one folder, one per-user registry
  key) before it does it.

**Windows 10/11, 64-bit. Steam only** (no Game Pass — the launch path goes through Steam).
No .NET runtime needed; everything is in the one file.

## This build is not code-signed

Like 0.2.0 and 0.3.0, this release carries no code signature. The free open-source signing
programme declined the project on reputation grounds (2026-09-03), and a paid
certificate is being weighed; until one exists, Windows will treat the file as an
unknown publisher:

- **SmartScreen** shows *"Windows protected your PC"* the first time you run the file.
  Choose **More info → Run anyway**.
- **Smart App Control** (some Windows 11 machines, in evaluation or on mode) blocks
  unsigned programs outright, with no override. Turning it off is a one-way switch in
  Windows Security; whether that is worth it for a replay launcher is your call.

Check that the file you downloaded is the one this workflow built:

```powershell
Get-FileHash .\PaladinReplayLauncher.exe -Algorithm SHA256
```

The hash must match the line in `SHA256SUMS.txt` on this page. Every release is built
by this repository's own workflow from the tagged commit, never uploaded by hand; the
source is public, and the privacy policy below applies unchanged.

## Code signing policy

A pushed release tag is refused unless it can be signed, so an unsigned binary cannot
reach this page by accident; an unsigned release is only ever published by a person
choosing so in the release workflow, and its notes say so — as these do.
Committers, reviewers and approvers: [OdinMayCall](https://github.com/odinmaycall).

Privacy: this program will not transfer any information to other networked systems
unless specifically requested by the user or the person installing or operating it. Its
only network request is downloading the replay named in the link you clicked. Clicking
"Dump this game" also asks paladin.odinmaycall.com whether that game already has a map
and, if not, uploads that game's printed world rows (about 150-400 KB of text, listed in
the console before sending) to it; nothing else, and never without that click. No
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
| `--dump <game-id>` | dump that game's map and send it to Paladin |
| `--dump <game-id> --no-upload` | the same run, but the rows stay on this PC |
| `--dump-upload <session-id>` | send rows a previous run kept, with no game |
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
- **A dump needs the game signed in.** The console's script functions are unavailable
  otherwise, and the run stops and says so rather than half-finishing.
- **Tournament games on custom maps cannot be dumped** on a PC that does not have that
  map's mod: the replay never reaches the map, the run gives up after three minutes and
  nothing is sent.
- A dump types into the game's console with an English keyboard's layout in mind. If the
  console does not open, the run says which key combinations it tried and stops.

## Uninstall

```
PaladinReplayLauncher.exe --uninstall
```

Then delete `%LOCALAPPDATA%\Programs\PaladinReplayLauncher\`. Your backups and logs in
`%LOCALAPPDATA%\PaladinReplayLauncher\` are deliberately left for you to remove yourself.
