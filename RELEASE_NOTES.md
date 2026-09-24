# Paladin Replay Launcher 0.5.0

Opens an Age of Empires IV replay and puts your game settings back exactly as they were
afterwards.

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

## Still true

- Paladin Shield protects your Age of Empires IV settings on every run and puts back anything the
  game changed while it was open.
- "Dump this game" still exists for map and resource research (`--dump <gameId>`), it is simply
  no longer offered as a way to get a build order.
