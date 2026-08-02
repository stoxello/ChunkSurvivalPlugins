using System.Text.Json;
using BlockGame.PluginApi;
using BlockGame.PluginApi.Minigames;

namespace BlockGame.Plugins.Spleef;

public sealed class SpleefPlugin : MinigamePlugin
{
    private const string AdminPermission = "minigame.spleef.admin";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly HashSet<string> alive = new(StringComparer.OrdinalIgnoreCase);
    private SpleefConfig config = new();
    private RegionSnapshot? baseline;
    private RoundState state;
    private double clock;
    private double stateEnds;
    private int lastCountdown = -1;
    public override string Name => "Spleef";
    public override string Version => "1.0.0";
    protected override string MinigameId => "spleef";

    protected override void OnMinigameLoad()
    {
        LoadFiles();
    }

    protected override void OnMinigameEnable()
    {
        Server.RegisterCommand(new PluginCommand("spleef", "Configures and controls Spleef", ExecuteCommand,
            usage: "/spleef <status|generatedefault|setlobby|setspawn|setarena|savearena|start>",
            permission: AdminPermission));
        Server.RegisterListener<PlayerJoinEventArgs>(OnPlayerJoined);
        Server.RegisterListener<BlockEditEventArgs>(OnBlockEdit, EventPriority.Highest);
        Server.RegisterListener<PlayerDamageEventArgs>(e => e.Cancelled = true, EventPriority.Highest);
        Server.RegisterListener<PlayerDeathEventArgs>(OnPlayerDeath, EventPriority.Highest);
        Server.RegisterListener<TickEventArgs>(OnTick);

        foreach (var player in Server.OnlinePlayers) MoveToLobby(player);
        Server.Log(baseline == null
            ? "Enabled in setup mode. Set lobby, spawn, and arena with /spleef."
            : "Enabled with a configured arena.");
    }

    protected override void OnMinigameDisable()
    {
        if (baseline != null) Server.RestoreRegion(baseline);
        alive.Clear();
    }

    private void OnPlayerJoined(PlayerJoinEventArgs e)
    {
        Server.SetGameMode(e.Player.Id, PluginGameMode.Adventure);
        MoveToLobby(e.Player);
        Server.SendMessage(e.Player.Id, baseline == null
            ? "Spleef is being configured."
            : "Spleef: break the arena floor and be the last player standing.");
    }

    private void OnBlockEdit(BlockEditEventArgs e)
    {
        bool permitted = state == RoundState.Playing && alive.Contains(e.Player.Id)
            && config.Arena is not null && Contains(config.Arena.ToRegion(), e.Position)
            && e.PreviousBlock != PluginBlocks.Air && e.NewBlock == PluginBlocks.Air;
        if (permitted) return;
        e.Cancelled = true;
        e.DenialMessage = state == RoundState.Playing
            ? "You can only break blocks inside the Spleef arena."
            : "The Spleef arena is locked outside an active round.";
    }

    private void OnPlayerDeath(PlayerDeathEventArgs e)
    {
        if (state != RoundState.Playing || !alive.Remove(e.Player.Id)) return;
        e.Cancelled = true;
        Eliminate(e.Player, e.Cause);
    }

    private void OnTick(TickEventArgs e)
    {
        clock += e.ElapsedSeconds;
        if (baseline == null || config.Lobby is null || config.Spawn is null || config.Arena is null) return;
        var online = Server.OnlinePlayers.ToList();

        switch (state)
        {
            case RoundState.Waiting:
                if (online.Count < config.MinPlayers) return;
                BeginCountdown(online);
                break;

            case RoundState.Countdown:
                if (online.Count < config.MinPlayers)
                {
                    state = RoundState.Waiting;
                    Announce("Countdown cancelled; waiting for more players.");
                    return;
                }
                int remaining = Math.Max(0, (int)Math.Ceiling(stateEnds - clock));
                if (remaining != lastCountdown)
                {
                    lastCountdown = remaining;
                    Announce($"Starting in {remaining}...");
                }
                if (clock >= stateEnds) StartRound(online);
                break;

            case RoundState.Playing:
                var currentIds = online.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                alive.RemoveWhere(id => !currentIds.Contains(id));
                float eliminationY = config.Arena.ToRegion().Min.Y - config.EliminationDepth;
                foreach (var player in online)
                    if (alive.Contains(player.Id) && player.Y < eliminationY)
                    {
                        alive.Remove(player.Id);
                        Eliminate(player, "the void");
                    }
                if (alive.Count <= 1) FinishRound(online);
                break;

            case RoundState.Finished:
                if (clock >= stateEnds) ResetRound(online);
                break;
        }
    }

    private void BeginCountdown(IReadOnlyCollection<PluginPlayer> players)
    {
        Server.RestoreRegion(baseline!);
        alive.Clear();
        foreach (var player in players)
        {
            Server.SetGameMode(player.Id, PluginGameMode.Adventure);
            Server.TeleportPlayer(player.Id, config.Spawn!.ToLocation());
        }
        state = RoundState.Countdown;
        stateEnds = clock + config.CountdownSeconds;
        lastCountdown = -1;
        Announce("Round starting soon!");
    }

    private void StartRound(IEnumerable<PluginPlayer> players)
    {
        alive.Clear();
        foreach (var player in players)
        {
            alive.Add(player.Id);
            Server.SetHealth(player.Id, 40);
            Server.SetGameMode(player.Id, PluginGameMode.Survival);
            Server.TeleportPlayer(player.Id, config.Spawn!.ToLocation());
        }
        state = RoundState.Playing;
        Announce("GO! Break the floor and stay out of the void.");
    }

    private void Eliminate(PluginPlayer player, string cause)
    {
        Server.SetHealth(player.Id, 40);
        Server.SetGameMode(player.Id, PluginGameMode.Spectator);
        Server.TeleportPlayer(player.Id, config.Lobby!.ToLocation());
        Announce($"{player.Name} was eliminated by {cause}!");
    }

    private void FinishRound(IEnumerable<PluginPlayer> players)
    {
        var winner = players.FirstOrDefault(p => alive.Contains(p.Id));
        Announce(winner == null ? "Nobody survived!" : $"{winner.Name} wins!");
        state = RoundState.Finished;
        stateEnds = clock + config.RestartSeconds;
    }

    private void ResetRound(IEnumerable<PluginPlayer> players)
    {
        Server.RestoreRegion(baseline!);
        alive.Clear();
        foreach (var player in players)
        {
            Server.SetGameMode(player.Id, PluginGameMode.Adventure);
            MoveToLobby(player);
        }
        state = RoundState.Waiting;
        Announce("Arena restored. Waiting for the next round.");
    }

    private void MoveToLobby(PluginPlayer player)
    {
        if (config.Lobby is not null) Server.TeleportPlayer(player.Id, config.Lobby.ToLocation());
    }

    private void ExecuteCommand(CommandContext command)
    {
        if (!Server.HasPermission(command.Player.Id, AdminPermission))
        {
            command.Reply("You do not have permission to configure Spleef.");
            return;
        }

        string action = command.Arguments.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        switch (action)
        {
            case "status":
                command.Reply($"Spleef state={state}; lobby={config.Lobby is not null}; spawn={config.Spawn is not null}; arena={baseline is not null}.");
                break;
            case "setlobby":
                config.Lobby = Point.From(command.Player);
                SaveConfig();
                command.Reply("Spleef lobby and spectator spawn set to your position.");
                break;
            case "setspawn":
                config.Spawn = Point.From(command.Player);
                SaveConfig();
                command.Reply("Spleef player spawn set to your position.");
                break;
            case "generatedefault":
                GenerateDefaultArena();
                command.Reply("Generated and saved the default Spleef arena.");
                break;
            case "setarena":
                var selection = Server.GetSelection(command.Player.Id);
                if (selection is null) { command.Reply("Select the arena with //pos1 and //pos2 first."); return; }
                config.Arena = RegionData.From(selection.Value.Normalize());
                baseline = Server.CaptureRegion(selection.Value);
                SaveConfig();
                SaveArena();
                state = RoundState.Waiting;
                command.Reply($"Captured a {selection.Value.Volume:N0}-block Spleef arena baseline.");
                break;
            case "savearena":
                if (config.Arena is null) { command.Reply("Set the arena first."); return; }
                baseline = Server.CaptureRegion(config.Arena.ToRegion());
                SaveArena();
                command.Reply("Saved the current arena blocks as the new reset baseline.");
                break;
            case "start":
                if (baseline == null || config.Spawn is null || config.Lobby is null)
                { command.Reply("Set lobby, spawn, and arena first."); return; }
                BeginCountdown(Server.OnlinePlayers);
                command.Reply("Spleef countdown started.");
                break;
            default:
                command.Reply("Usage: /spleef <status|generatedefault|setlobby|setspawn|setarena|savearena|start>");
                break;
        }
    }

    private void GenerateDefaultArena()
    {
        const int radius = 14;
        const int floorY = 36;
        for (int x = -radius; x <= radius; x++)
        for (int z = -radius; z <= radius; z++)
            Server.SetBlock(x, floorY, z, PluginBlocks.WoolWhite);

        for (int x = -4; x <= 4; x++)
        for (int z = radius + 5; z <= radius + 9; z++)
            Server.SetBlock(x, floorY + 6, z, PluginBlocks.Glass);

        config.Lobby = new Point(0.5f, floorY + 7f, radius + 7.5f);
        config.Spawn = new Point(0.5f, floorY + 1f, 0.5f);
        config.Arena = new RegionData(-radius, floorY, -radius, radius, floorY, radius);
        baseline = Server.CaptureRegion(config.Arena.ToRegion());
        SaveConfig();
        SaveArena();
        state = RoundState.Waiting;
    }

    private void LoadFiles()
    {
        Directory.CreateDirectory(DataDirectory);
        string configPath = Path.Combine(DataDirectory, "config.json");
        if (File.Exists(configPath))
            config = JsonSerializer.Deserialize<SpleefConfig>(File.ReadAllText(configPath), JsonOptions) ?? new SpleefConfig();
        else SaveConfig();

        if (config.Arena is null) return;
        string arenaPath = Path.Combine(DataDirectory, "arena.blocks");
        if (!File.Exists(arenaPath)) return;
        try { var region = config.Arena.ToRegion(); baseline = new RegionSnapshot(region, PluginBlockStorage.Decode(File.ReadAllBytes(arenaPath), checked((int)region.Volume))); }
        catch (Exception ex) { Server.Log("Could not load arena baseline: " + ex.Message); }
    }

    private void SaveConfig() => File.WriteAllText(Path.Combine(DataDirectory, "config.json"),
        JsonSerializer.Serialize(config, JsonOptions));
    private void SaveArena() => File.WriteAllBytes(Path.Combine(DataDirectory, "arena.blocks"), PluginBlockStorage.Encode(baseline!.Blocks));

    private static bool Contains(BlockRegion region, BlockPosition point)
    {
        var n = region.Normalize();
        return point.X >= n.Min.X && point.X <= n.Max.X
            && point.Y >= n.Min.Y && point.Y <= n.Max.Y
            && point.Z >= n.Min.Z && point.Z <= n.Max.Z;
    }

    private enum RoundState { Waiting, Countdown, Playing, Finished }

    public sealed class SpleefConfig
    {
        public Point? Lobby { get; set; }
        public Point? Spawn { get; set; }
        public RegionData? Arena { get; set; }
        public int MinPlayers { get; set; } = 2;
        public int CountdownSeconds { get; set; } = 10;
        public int RestartSeconds { get; set; } = 6;
        public int EliminationDepth { get; set; } = 5;
    }

    public sealed record Point(float X, float Y, float Z, float Yaw = 0, float Pitch = 0)
    {
        public static Point From(PluginPlayer player) => new(player.X, player.Y, player.Z);
        public PluginLocation ToLocation() => new(X, Y, Z, Yaw, Pitch);
    }

    public sealed record RegionData(int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ)
    {
        public static RegionData From(BlockRegion value) =>
            new(value.Min.X, value.Min.Y, value.Min.Z, value.Max.X, value.Max.Y, value.Max.Z);
        public BlockRegion ToRegion() =>
            new(new BlockPosition(MinX, MinY, MinZ), new BlockPosition(MaxX, MaxY, MaxZ));
    }
}
