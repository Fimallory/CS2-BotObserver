using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using BotHiderApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using Microsoft.Extensions.Logging;

namespace BotObserver;

public class BotObserverPlugin : BasePlugin
{
    public override string ModuleName => "Bot Observer";
    public override string ModuleVersion => "1.2.3";
    public override string ModuleAuthor => "CS2-Bot-Improver";
    public override string ModuleDescription => "Adds broadcast-style observer bots that appear as spectators on the scoreboard.";

    private const int MaxSetupAttempts = 5;

    // steamId64 = SteamId64Base + 32-bit account id (same base BotHider uses).
    private const ulong SteamId64Base = 76561197960265728UL;

    private readonly ConcurrentDictionary<int, CCSPlayerController> _observers = new();
    private readonly ConcurrentDictionary<int, string> _pendingObservers = new();
    private readonly Random _rng = new();

    // Key: lowercase name, Value: canonical spelling from bot_info.json
    private readonly Dictionary<string, string> _namePool = new(StringComparer.OrdinalIgnoreCase);

    // Key: canonical spelling, Value: 32-bit account id from the bot_info.json key.
    private readonly Dictionary<string, uint> _nameSteamIds = new(StringComparer.Ordinal);

    public override void Load(bool hotReload)
    {
        LoadPlayerNamesFromBotInfo();

        AddCommand("bot_add_spec", "Add a broadcast observer bot: bot_add_spec [name]", OnBotSpec);
        AddCommandListener("bot_kick", OnBotKick, HookMode.Pre);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);

        // Watchdog: BotHider builds with round-start respawn/team logic treat a
        // Spectator observer as a dead managed bot and pull it into T/CT, so
        // re-assert Spectator + SteamID every second.
        AddTimer(1.0f, EnforceObservers, TimerFlags.REPEAT);
    }

    public override void Unload(bool hotReload)
    {
        RemoveCommand("bot_add_spec", OnBotSpec);
        RemoveCommandListener("bot_kick", OnBotKick, HookMode.Pre);
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        LogObserverStateAtRoundStart();
        EnforceObservers();
        return HookResult.Continue;
    }

    // Instant revert: a tracked observer must never stay out of Spectator.
    // Round-start respawn logic in older BotHider builds SwitchTeams the shell
    // and spawns a pawn for it; pull it back on the next frame so the window
    // shrinks from ~1s (watchdog poll) to ~1 tick. Our own move to Spectator
    // re-fires this event with team == Spectator and is ignored: no loop.
    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        if (@event.Team == (int)CsTeam.Spectator)
            return HookResult.Continue;

        // @event.Userid is the controller itself in this CSS version.
        var subject = @event.Userid;
        if (subject == null || !subject.IsValid || !subject.UserId.HasValue)
            return HookResult.Continue;
        if (!_observers.ContainsKey(subject.UserId.Value))
            return HookResult.Continue;

        Server.NextFrame(() =>
        {
            if (!subject.IsValid || subject.TeamNum == (int)CsTeam.Spectator)
                return;
            Logger.LogWarning("[BotObserver] Observer \"{Name}\" forced out of Spectator; moving back.",
                subject.PlayerName);
            subject.ChangeTeam(CsTeam.Spectator);
            ApplyObserverState(subject);
        });
        return HookResult.Continue;
    }

    // Re-asserts Spectator + SteamID for every tracked observer. Round-start
    // respawn/team logic in older BotHider builds treats a Spectator observer
    // as a dead managed bot and pulls it into T/CT; this pulls it back.
    private void EnforceObservers()
    {
        if (_observers.IsEmpty)
            return;

        foreach (var observer in _observers.Values)
        {
            if (observer == null || !observer.IsValid)
                continue;

            if (observer.TeamNum != (int)CsTeam.Spectator)
            {
                Logger.LogWarning("[BotObserver] Observer \"{Name}\" left Spectator (team {Team}); moving back.",
                    observer.PlayerName, observer.TeamNum);
                observer.ChangeTeam(CsTeam.Spectator);
                ApplyObserverState(observer);
            }

            if (_nameSteamIds.TryGetValue(observer.PlayerName, out uint accountId))
            {
                ulong expected = SteamId64Base + accountId;
                if (observer.SteamID != expected)
                {
                    var api = new PluginCapability<IBotHiderApi>("bothider:api").Get();
                    if (api != null && observer.Slot >= 0 && api.IsManagedBot(observer.Slot) &&
                        api.SetBotSteamId(observer.Slot, expected))
                    {
                        Logger.LogInformation("[BotObserver] Re-applied SteamID for \"{Name}\".",
                            observer.PlayerName);
                    }
                }
            }
        }
    }

    private void LoadPlayerNamesFromBotInfo()
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "..", "BotHider", "bot_info.json"));
            if (!File.Exists(path))
            {
                Logger.LogWarning("[BotObserver] bot_info.json not found, observer name pool is empty.");
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("players", out var players))
                return;

            foreach (var entry in players.EnumerateObject())
            {
                if (!entry.Value.TryGetProperty("player_name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                    continue;

                var name = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                _namePool[name] = name;

                // The players key is the 32-bit account id; remember it so the
                // observer can claim its real SteamID instead of whatever BotHider
                // assigned while it was still named "loopback".
                if (uint.TryParse(entry.Name, out uint accountId) && accountId != 0)
                    _nameSteamIds.TryAdd(name, accountId);
            }

            Logger.LogInformation("[BotObserver] Unified name pool: {Count} entries ({SidCount} with SteamIDs).",
                _namePool.Count, _nameSteamIds.Count);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "[BotObserver] Failed to load bot_info.json.");
        }
    }

    // Fuzzy case-insensitive lookup; the applied name always follows bot_info.json
    private string ResolveCanonicalName(string input)
    {
        return _namePool.TryGetValue(input, out var canonical) ? canonical : input;
    }

    private void OnBotSpec(CCSPlayerController? caller, CommandInfo info)
    {
        var input = info.ArgCount > 1 ? info.GetArg(1).Trim() : PickDefaultName();

        if (string.IsNullOrWhiteSpace(input))
        {
            info.ReplyToCommand("[BotObserver] Invalid name.");
            return;
        }

        var name = ResolveCanonicalName(input);
        if (!ReferenceEquals(name, input))
            Logger.LogInformation("[BotObserver] Resolved \"{Input}\" to canonical \"{Name}\".", input, name);

        if (_observers.Values.Any(o => o.IsValid && o.PlayerName.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            _pendingObservers.Values.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            info.ReplyToCommand($"[BotObserver] Observer \"{name}\" already exists.");
            return;
        }

        // Empty-shell fake client: never joins T/CT, so the engine never counts
        // a missing player and never grants shorthanded compensation.
        // Born with the final name so BotHider adopts it with the matching
        // bot_info.json identity (name + SteamID) instead of a random one.
        int slot = CreateFakeClientNative(name);
        if (slot < 0)
        {
            info.ReplyToCommand("[BotObserver] Failed to create fake client.");
            return;
        }

        if (!_pendingObservers.TryAdd(slot, name))
        {
            info.ReplyToCommand("[BotObserver] Failed to track the new fake client.");
            return;
        }

        info.ReplyToCommand($"[BotObserver] Creating observer \"{name}\"...");
        AddTimer(
            0.1f,
            () => TryInitializeObserver(slot, name, 0),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private HookResult OnBotKick(CCSPlayerController? caller, CommandInfo info)
    {
        if (info.ArgCount < 2)
            return HookResult.Continue;

        var target = info.GetArg(1).Trim();
        var observer = _observers.Values.FirstOrDefault(o =>
            o.IsValid && o.PlayerName.Equals(target, StringComparison.OrdinalIgnoreCase));

        if (observer == null)
            return HookResult.Continue;

        KickObserver(observer);
        info.ReplyToCommand($"[BotObserver] Removed observer \"{target}\".");
        return HookResult.Handled;
    }

    private void OnClientDisconnect(int slot)
    {
        _pendingObservers.TryRemove(slot, out _);

        var stale = _observers
            .Where(kv => !kv.Value.IsValid || kv.Value.Slot == slot)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in stale)
            _observers.TryRemove(key, out _);
    }

    private void TryInitializeObserver(int slot, string name, int attempt)
    {
        if (!_pendingObservers.TryGetValue(slot, out var pendingName) ||
            !pendingName.Equals(name, StringComparison.Ordinal))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !player.IsValid)
        {
            RetryOrFailSetup(slot, name, attempt, "player controller is not ready");
            return;
        }

        // Empty-shell fake clients live in Spectator from birth and never enter
        // T/CT, so no BotHider registration is needed.
        ApplyObserverName(player, name);
        ApplyObserverState(player);

        if (player.TeamNum == (int)CsTeam.Spectator)
        {
            _pendingObservers.TryRemove(slot, out _);

            if (player.UserId.HasValue)
                _observers[player.UserId.Value] = player;

            Logger.LogInformation(
                "[BotObserver] \"{Name}\" is now an observer (slot {Slot}, team {Team}).",
                name, slot, player.TeamNum);
            return;
        }

        if (attempt >= MaxSetupAttempts)
        {
            FailObserverSetup(slot, name, $"team remained {player.TeamNum}", player);
            return;
        }

        Logger.LogInformation(
            "[BotObserver] Moving \"{Name}\" to Spectator (slot {Slot}, current team {Team}, attempt {Attempt}/{Max}).",
            name, slot, player.TeamNum, attempt + 1, MaxSetupAttempts);
        player.ChangeTeam(CsTeam.Spectator);
        ApplyObserverState(player);

        AddTimer(
            0.1f,
            () => TryInitializeObserver(slot, name, attempt + 1),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void RetryOrFailSetup(int slot, string name, int attempt, string reason)
    {
        if (attempt >= MaxSetupAttempts)
        {
            FailObserverSetup(slot, name, reason);
            return;
        }

        AddTimer(
            0.1f,
            () => TryInitializeObserver(slot, name, attempt + 1),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void FailObserverSetup(
        int slot,
        string name,
        string reason,
        CCSPlayerController? player = null)
    {
        _pendingObservers.TryRemove(slot, out _);
        Logger.LogWarning("[BotObserver] Failed to create observer \"{Name}\": {Reason}.", name, reason);

        player ??= Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true })
            return;

        if (player.UserId.HasValue)
            Server.ExecuteCommand($"kickid {player.UserId.Value} \"Observer setup failed\"");
        else
            player.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
    }

    private static void ApplyObserverState(CCSPlayerController player)
    {
        player.Connected = PlayerConnectedState.Connected;
        Utilities.SetStateChanged(player, "CBasePlayerController", "m_iConnected");
    }

    private void LogObserverStateAtRoundStart()
    {
        foreach (var observer in _observers.Values)
        {
            if (!observer.IsValid)
                continue;

            Logger.LogInformation(
                "[BotObserver] round check: name=\"{Name}\" slot={Slot} team={Team}.",
                observer.PlayerName, observer.Slot, observer.TeamNum);
        }
    }

    private void ApplyObserverName(CCSPlayerController player, string name)
    {
        if (player == null || !player.IsValid)
            return;

        var api = new PluginCapability<IBotHiderApi>("bothider:api").Get();
        if (api == null || player.Slot < 0)
        {
            player.PlayerName = name;
            Utilities.SetStateChanged(player, "CBasePlayerController", "m_iszPlayerName");
            return;
        }

        bool named = false;
        if (api.IsManagedBot(player.Slot))
            named = api.SetPersonaName(player.Slot, name);

        if (!named)
        {
            player.PlayerName = name;
            Utilities.SetStateChanged(player, "CBasePlayerController", "m_iszPlayerName");
        }

        // BotHider adopts the shell before the rename lands and assigns a random
        // identity, so claim the real SteamID explicitly when bot_info has one.
        if (_nameSteamIds.TryGetValue(name, out uint accountId))
        {
            ulong expected = SteamId64Base + accountId;
            if (player.SteamID != expected && api.IsManagedBot(player.Slot))
            {
                bool sidOk = api.SetBotSteamId(player.Slot, expected);
                Logger.LogInformation("[BotObserver] Identity for \"{Name}\": steam {Sid} -> {Ok}.",
                    name, expected, sidOk);
            }
        }
    }

    private void KickObserver(CCSPlayerController observer)
    {
        if (observer.UserId.HasValue)
            _observers.TryRemove(observer.UserId.Value, out _);

        if (observer.UserId.HasValue)
            Server.ExecuteCommand($"kickid {observer.UserId.Value} \"Observer removed\"");
        else
            Server.ExecuteCommand($"kick \"{observer.PlayerName.Replace("\"", "")}\"");
    }

    private string PickDefaultName()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var o in _observers.Values.Where(o => o.IsValid))
            used.Add(o.PlayerName);

        var allPlayers = Utilities.GetPlayers();
        if (allPlayers != null)
            foreach (var p in allPlayers)
                if (p != null && p.IsValid)
                    used.Add(p.PlayerName);

        foreach (var pending in _pendingObservers.Values)
            used.Add(pending);

        var free = _namePool.Values.Where(n => !used.Contains(n)).ToList();
        return free.Count > 0 ? free[_rng.Next(free.Count)] : $"Observer {_observers.Count + 1}";
    }

    private unsafe int CreateFakeClientNative(string? name)
    {
        nint enginePtr = ValveInterface.Engine.Pointer;
        if (enginePtr == nint.Zero)
            return -1;

        nint vtable = Marshal.ReadIntPtr(enginePtr);
        nint cfcFnPtr = Marshal.ReadIntPtr(vtable + 52 * 8);

        // The engine names the fake client after szNetName. Non-ASCII names may
        // garble through the ANSI marshalling; the 0.1s setup renames + re-SIDs.
        string clientName = string.IsNullOrEmpty(name) ? "loopback" : name;
        nint addrPtr = Marshal.StringToHGlobalAnsi(clientName);
        nint retBuf = Marshal.AllocHGlobal(8);
        Marshal.WriteInt64(retBuf, -1);

        try
        {
            var createFakeClient =
                (delegate* unmanaged[Thiscall]<nint, nint, nint, nint>)cfcFnPtr;
            createFakeClient(enginePtr, retBuf, addrPtr);
            return Marshal.ReadInt32(retBuf);
        }
        catch
        {
            return -1;
        }
        finally
        {
            Marshal.FreeHGlobal(addrPtr);
            Marshal.FreeHGlobal(retBuf);
        }
    }
}
