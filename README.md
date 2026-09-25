# CS2-BotObserver

A [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin that adds broadcast-style **observer bots** (caster/persona bots) appearing as spectators on the scoreboard. Consumed by [CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) as a git submodule at `addons/counterstrikesharp/plugins/BotObserver`.

## Behavior

- `bot_add_spec [name]` creates an empty-shell fake client that **never joins T/CT**: the engine never sees a shorthanded team, so no compensation is ever granted to the opposing side, and there is no bot AI.
- Names resolve case-insensitively against the unified `bot_info.json` pool and the canonical spelling is always applied (`machinewjq` → `MachineWJQ`). Without a name, one is picked from the pool.
- 0.1 s after creation the bot is renamed through the BotHider API (`bothider:api`), marked connected, and switched to Spectator.
- Observer slots are registered via `IBotHiderApi.SetObserverSlot` **before** any team work, so BotHider's round-start lifecycle never re-teams or respawns them.
- `bot_kick <name>` is intercepted to remove observers.
- All diagnostics go through the managed `ILogger` — `Server.PrintToConsole` inside plugin callbacks crashes `cs2.exe` with an unhandled native exception, so it is never used here.

## Commands

| Command | Description |
| --- | --- |
| `bot_add_spec [name]` | Add an observer bot (random pool name if omitted). |
| `bot_kick <name>` | Remove an observer bot by name (quote names containing spaces). |

## Requirements (provided by the parent repo)

- `BotHiderApi` shared project with observer-slot support (`SetObserverSlot` / `IsObserverSlot`) — referenced via the relative path `..\..\shared\BotHiderApi\BotHiderApi.csproj`.
- `BotHiderImpl` build that honors observer slots in respawn / vote / team logic.
- `bot_info.json` unified name pool (Liquipedia players + casters).

Because of the relative `ProjectReference`, this repo **builds as a submodule at its canonical path** inside a CS2-Bot-Improver checkout — a standalone clone does not contain `shared/BotHiderApi`. Targets net10.0 / CSS API 1.0.371.

## Install (as submodule of CS2-Bot-Improver)

```bash
git submodule add https://github.com/Fimallory/CS2-BotObserver.git addons/counterstrikesharp/plugins/BotObserver
git submodule update --init --recursive
dotnet build addons/counterstrikesharp/plugins/BotObserver/BotObserver.csproj -c Release
```

Copy the built `BotObserver.dll` to `game/csgo/addons/counterstrikesharp/plugins/BotObserver/`, restart the server / `css_plugins reload`.

## License

AGPL-3.0, same as CS2-Bot-Improver.
