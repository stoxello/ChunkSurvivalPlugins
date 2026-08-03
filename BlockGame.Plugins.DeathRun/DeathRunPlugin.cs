using System.Text.Json;
using BlockGame.PluginApi;
using BlockGame.PluginApi.Minigames;

namespace BlockGame.Plugins.DeathRun;

public sealed class DeathRunPlugin : MinigamePlugin
{
    private const string AdminPermission = "minigame.deathrun.admin";
    private readonly HashSet<string> runners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> finished = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> checkpoints = new(StringComparer.OrdinalIgnoreCase);
    private Config config = new();
    private RegionSnapshot? baseline;
    private State state;
    private string activator = "";
    private double clock, stateEnds, nextTrap;

    public override string Name => "DeathRun";
    public override string Version => "1.0.5";
    protected override string MinigameId => "deathrun";

    protected override void OnMinigameLoad() => LoadFiles();

    protected override void OnMinigameEnable()
    {
        Server.RegisterCommand(new PluginCommand("deathrun", "Configures and controls Death Run", Command,
            "/deathrun <status|generatedefault|setlobby|setrunner|setactivator|setarena|addcheckpoint|savearena|start|trap>",
            AdminPermission, new[] { "dr", "trap" }));
        Server.RegisterListener<PlayerJoinEventArgs>(e => ToLobby(e.Player));
        Server.RegisterListener<BlockEditEventArgs>(e => { e.Cancelled = true; e.DenialMessage = "The Death Run course is protected."; });
        Server.RegisterListener<PlayerDeathEventArgs>(OnDeath, EventPriority.Highest);
        Server.RegisterListener<TickEventArgs>(OnTick);
        Server.Log(baseline == null ? "Enabled in setup mode." : "Enabled with a configured course.");
    }

    protected override void OnMinigameDisable()
    {
        if (baseline != null) Server.RestoreRegion(baseline);
    }

    private void OnTick(TickEventArgs e)
    {
        clock += e.ElapsedSeconds;
        if (!Ready) return;
        var online = Players.ToList();
        if (state == State.Waiting && online.Count >= config.MinPlayers) Begin(online);
        else if (state == State.Countdown && clock >= stateEnds)
        {
            state = State.Playing; stateEnds = clock + config.RoundSeconds;
            Announce("GO! Reach the finish line!");
        }
        else if (state == State.Playing)
        {
            var ids = online.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            runners.RemoveWhere(id => !ids.Contains(id));
            foreach (var p in online.Where(p => runners.Contains(p.Id) && !finished.Contains(p.Id)))
            {
                int cp = LatestCheckpoint(p);
                if (!checkpoints.TryGetValue(p.Id, out int old) || cp > old)
                { checkpoints[p.Id] = cp; Server.SendMessage(p.Id, $"Checkpoint {cp} reached!"); }
                if (p.Y < config.VoidY) Respawn(p);
                if (p.Z >= config.FinishZ && finished.Add(p.Id)) Announce($"{p.Name} finished the course!");
            }
            if (finished.Count > 0 || clock >= stateEnds || runners.Count == 0) Finish(online);
        }
        else if (state == State.Finished && clock >= stateEnds) Reset(online);
    }

    private void Begin(IReadOnlyList<PluginPlayer> players)
    {
        Server.RestoreRegion(baseline!);
        runners.Clear(); finished.Clear(); checkpoints.Clear();
        var controller = players[Random.Shared.Next(players.Count)];
        activator = controller.Id;
        foreach (var p in players)
        {
            Server.SetGameMode(p.Id, PluginGameMode.Adventure);
            if (p.Id == activator)
            {
                Server.TeleportPlayer(p.Id, config.Activator!.Location());
                Server.SendMessage(p.Id, "You are the ACTIVATOR. Use /deathrun trap.");
            }
            else
            {
                runners.Add(p.Id); checkpoints[p.Id] = 0;
                Server.TeleportPlayer(p.Id, config.Runner!.Location());
            }
        }
        state = State.Countdown; stateEnds = clock + config.CountdownSeconds;
        Announce($"{controller.Name} controls the traps. Runners, get ready!");
    }

    private void Finish(IEnumerable<PluginPlayer> players)
    {
        Announce(finished.Count > 0 ? "The runners win!" : "The activator wins!");
        state = State.Finished; stateEnds = clock + config.RestartSeconds;
    }

    private void Reset(IEnumerable<PluginPlayer> players)
    {
        Server.RestoreRegion(baseline!);
        foreach (var p in players) ToLobby(p);
        runners.Clear(); finished.Clear(); checkpoints.Clear(); activator = "";
        state = State.Waiting;
    }

    private void OnDeath(PlayerDeathEventArgs e)
    {
        if (state != State.Playing || !runners.Contains(e.Player.Id)) return;
        e.Cancelled = true; Respawn(e.Player);
    }

    private void Respawn(PluginPlayer player)
    {
        int index = checkpoints.TryGetValue(player.Id, out int cp) ? cp : 0;
        var point = index > 0 && index <= config.Checkpoints.Count ? config.Checkpoints[index - 1] : config.Runner!;
        Server.SetHealth(player.Id, 40);
        Server.TeleportPlayer(player.Id, point.Location());
    }

    private int LatestCheckpoint(PluginPlayer player)
    {
        int result = 0;
        for (int i = 0; i < config.Checkpoints.Count; i++)
            if (player.Z >= config.Checkpoints[i].Z) result = i + 1;
        return result;
    }

    private void ActivateTrap(CommandContext command)
    {
        if (state != State.Playing || command.Player.Id != activator)
        { command.Reply("Only the active controller can trigger traps."); return; }
        if (clock < nextTrap) { command.Reply("Traps are recharging."); return; }
        var lead = Players.Where(p => runners.Contains(p.Id) && !finished.Contains(p.Id))
            .OrderByDescending(p => p.Z).FirstOrDefault();
        if (lead == null) return;
        int zone = Math.Clamp(((int)lead.Z / 20) * 20 + 10, 10, 150);
        for (int x = -3; x <= 3; x++) for (int z = zone - 3; z <= zone + 3; z++)
            Server.SetBlock(x, 30, z, PluginBlocks.Air);
        nextTrap = clock + config.TrapCooldownSeconds;
        Announce($"Trap activated at section {zone / 20 + 1}!");
    }

    private void Command(CommandContext c)
    {
        string invoked = c.Raw.Trim().TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        string action = c.Arguments.FirstOrDefault()?.ToLowerInvariant()
            ?? (invoked.Equals("trap", StringComparison.OrdinalIgnoreCase) ? "trap" : "status");
        if (action == "trap") { ActivateTrap(c); return; }
        if (!IsAdmin(c.Player, AdminPermission)) { c.Reply("You do not have permission."); return; }
        switch (action)
        {
            case "status": c.Reply($"DeathRun state={state}; ready={Ready}; checkpoints={config.Checkpoints.Count}."); break;
            case "generatedefault": GenerateDefault(); c.Reply("Generated the default Death Run course."); break;
            case "setlobby": config.Lobby = Point.From(c.Player); Save(); c.Reply("Lobby set."); break;
            case "setrunner": config.Runner = Point.From(c.Player); Save(); c.Reply("Runner spawn set."); break;
            case "setactivator": config.Activator = Point.From(c.Player); Save(); c.Reply("Activator spawn set."); break;
            case "addcheckpoint": config.Checkpoints.Add(Point.From(c.Player)); config.Checkpoints.Sort((a,b) => a.Z.CompareTo(b.Z)); Save(); c.Reply("Checkpoint added."); break;
            case "setarena":
                var selection = Server.GetSelection(c.Player.Id);
                if (selection == null) { c.Reply("Select the course with //pos1 and //pos2."); return; }
                config.Arena = Region.From(selection.Value); baseline = Server.CaptureRegion(selection.Value); SaveAll(); c.Reply("Course baseline captured."); break;
            case "savearena":
                if (config.Arena == null) { c.Reply("Set the arena first."); return; }
                baseline = Server.CaptureRegion(config.Arena.Value()); SaveArena(); c.Reply("Course baseline saved."); break;
            case "start":
                if (!Ready) { c.Reply("Configure the course first."); return; }
                Begin(Players.ToList()); c.Reply("Countdown started."); break;
            default: c.Reply("Usage: /deathrun <status|generatedefault|setlobby|setrunner|setactivator|setarena|addcheckpoint|savearena|start|trap>"); break;
        }
    }

    private void GenerateDefault()
    {
        for (int z = 0; z <= 160; z++) for (int x = -8; x <= 8; x++)
        {
            Server.SetBlock(x, 30, z, z >= 156 ? PluginBlocks.GoldBlock : PluginBlocks.StoneBricks);
            if (Math.Abs(x) == 8) for (int y = 31; y <= 34; y++) Server.SetBlock(x, y, z, PluginBlocks.Bricks);
        }
        for (int z = 0; z <= 160; z++) for (int x = 11; x <= 13; x++) Server.SetBlock(x, 32, z, PluginBlocks.Glass);
        for (int zone = 20; zone < 156; zone += 20) for (int x = -2; x <= 2; x++) Server.SetBlock(x, 30, zone, PluginBlocks.Air);
        for (int x = -4; x <= 4; x++) for (int z = 0; z <= 6; z++) Server.SetBlock(x, 47, z, PluginBlocks.Glass);
        config = Config.Default();
        baseline = Server.CaptureRegion(config.Arena!.Value());
        SaveAll();
    }

    private bool Ready => baseline != null && config.Lobby != null && config.Runner != null && config.Activator != null && config.Arena != null;
    private void ToLobby(PluginPlayer p) { Server.SetGameMode(p.Id, PluginGameMode.Adventure); if (config.Lobby != null) Server.TeleportPlayer(p.Id, config.Lobby.Location()); }
    private string ConfigPath => Path.Combine(DataDirectory, "config.json");
    private string ArenaPath => Path.Combine(DataDirectory, "arena.blocks");
    private void Save() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    private void SaveArena() => File.WriteAllBytes(ArenaPath, PluginBlockStorage.Encode(baseline!.Blocks));
    private void SaveAll() { Save(); SaveArena(); }
    private void LoadFiles()
    {
        Directory.CreateDirectory(DataDirectory);
        if (File.Exists(ConfigPath)) config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new();
        if (config.Arena != null && File.Exists(ArenaPath)) { var region = config.Arena.Value(); baseline = new RegionSnapshot(region, PluginBlockStorage.Decode(File.ReadAllBytes(ArenaPath), checked((int)region.Volume))); }
    }

    private enum State { Waiting, Countdown, Playing, Finished }
    public sealed class Config
    {
        public Point? Lobby { get; set; } public Point? Runner { get; set; } public Point? Activator { get; set; }
        public Region? Arena { get; set; } public List<Point> Checkpoints { get; set; } = new();
        public int MinPlayers { get; set; } = 2; public int CountdownSeconds { get; set; } = 10;
        public int RoundSeconds { get; set; } = 180; public int RestartSeconds { get; set; } = 8;
        public int TrapCooldownSeconds { get; set; } = 6; public float VoidY { get; set; } = 10; public float FinishZ { get; set; } = 156;
        public static Config Default() => new() { Lobby = new(.5f,48,.5f), Runner = new(.5f,32,2.5f), Activator = new(12.5f,34,2.5f),
            Arena = new(-8,30,0,13,48,160), Checkpoints = new() { new(.5f,32,42.5f), new(.5f,32,82.5f), new(.5f,32,122.5f) } };
    }
    public sealed record Point(float X, float Y, float Z) { public static Point From(PluginPlayer p) => new(p.X,p.Y,p.Z); public PluginLocation Location() => new(X,Y,Z); }
    public sealed record Region(int MinX,int MinY,int MinZ,int MaxX,int MaxY,int MaxZ)
    { public static Region From(BlockRegion r) { r=r.Normalize(); return new(r.Min.X,r.Min.Y,r.Min.Z,r.Max.X,r.Max.Y,r.Max.Z); } public BlockRegion Value()=>new(new(MinX,MinY,MinZ),new(MaxX,MaxY,MaxZ)); }
}
