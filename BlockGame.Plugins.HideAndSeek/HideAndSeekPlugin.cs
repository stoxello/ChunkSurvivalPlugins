using System.Text.Json;
using BlockGame.PluginApi;
using BlockGame.PluginApi.Minigames;

namespace BlockGame.Plugins.HideAndSeek;

public sealed class HideAndSeekPlugin : MinigamePlugin
{
    private const string AdminPermission = "minigame.hideandseek.admin";
    private static readonly ushort[] Disguises =
        { PluginBlocks.Bookshelf, PluginBlocks.Bricks, PluginBlocks.FineWood, PluginBlocks.WoolRed };
    private readonly HashSet<string> hiders = new(StringComparer.OrdinalIgnoreCase);
    private Config config = new();
    private RegionSnapshot? baseline;
    private State state;
    private string seeker = "";
    private double clock, stateEnds;
    private int lastCountdown = -1;

    public override string Name => "HideAndSeek";
    public override string Version => "1.0.0";
    protected override string MinigameId => "hideandseek";
    protected override IReadOnlyCollection<string> MinigameAliases => new[] { "hide-and-seek" };

    protected override void OnMinigameLoad() => LoadFiles();

    protected override void OnMinigameEnable()
    {
        Server.RegisterCommand(new PluginCommand("hideandseek", "Configures Hide & Seek", Command,
            "/hideandseek <status|generatedefault|setlobby|setseeker|addhider|setarena|savearena|start>",
            AdminPermission, new[] { "has" }));
        Server.RegisterListener<PlayerJoinEventArgs>(e => ToLobby(e.Player));
        Server.RegisterListener<BlockEditEventArgs>(e => { e.Cancelled = true; e.DenialMessage = "The Hide & Seek map is protected."; });
        Server.RegisterListener<PlayerDamageEventArgs>(e => e.Cancelled = true, EventPriority.Highest);
        Server.RegisterListener<PlayerAttackEventArgs>(OnAttack, EventPriority.Highest);
        Server.RegisterListener<TickEventArgs>(OnTick);
        Server.Log(Ready ? "Enabled with a configured map." : "Enabled in setup mode.");
    }

    protected override void OnMinigameDisable()
    {
        if (baseline != null) Server.RestoreRegion(baseline);
        foreach (var player in Players) Server.SetDisguise(player.Id, PluginBlocks.Air);
    }

    private void OnTick(TickEventArgs e)
    {
        clock += e.ElapsedSeconds;
        if (!Ready) return;
        var online = Players.ToList();
        if (state == State.Waiting && online.Count >= config.MinPlayers) Begin(online);
        else if (state == State.Hiding)
        {
            if (!online.Any(p => p.Id == seeker)) { Finish(online, "The hiders win; the seeker left."); return; }
            int remaining = Math.Max(0, (int)Math.Ceiling(stateEnds - clock));
            if (remaining != lastCountdown && (remaining <= 5 || remaining % 5 == 0))
            { lastCountdown = remaining; Announce($"Seeker released in {remaining}..."); }
            if (clock >= stateEnds)
            {
                var s = online.First(p => p.Id == seeker);
                Server.TeleportPlayer(s.Id, config.Seeker!.Location());
                state = State.Playing; stateEnds = clock + config.RoundSeconds;
                Announce("The seeker has been released!");
            }
        }
        else if (state == State.Playing)
        {
            var ids = online.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            hiders.RemoveWhere(id => !ids.Contains(id));
            if (!ids.Contains(seeker)) Finish(online, "The hiders win; the seeker left.");
            else if (hiders.Count == 0) Finish(online, "The seeker found everyone!");
            else if (clock >= stateEnds) Finish(online, "The hiders win!");
        }
        else if (state == State.Finished && clock >= stateEnds) Reset(online);
    }

    private void Begin(IReadOnlyList<PluginPlayer> players)
    {
        Server.RestoreRegion(baseline!);
        hiders.Clear();
        var selected = players[Random.Shared.Next(players.Count)];
        seeker = selected.Id;
        int spawn = 0;
        foreach (var p in players)
        {
            Server.SetGameMode(p.Id, PluginGameMode.Adventure);
            Server.SetDisguise(p.Id, PluginBlocks.Air);
            if (p.Id == seeker)
            {
                Server.TeleportPlayer(p.Id, config.Lobby!.Location());
                Server.SendMessage(p.Id, "You are the SEEKER. Wait for the hiding timer.");
            }
            else
            {
                hiders.Add(p.Id);
                Server.SetDisguise(p.Id, Disguises[Random.Shared.Next(Disguises.Length)]);
                var point = config.HiderSpawns[spawn++ % config.HiderSpawns.Count];
                Server.TeleportPlayer(p.Id, point.Location());
                Server.SendMessage(p.Id, "You are a disguised HIDER. Find a matching hiding place!");
            }
        }
        state = State.Hiding; stateEnds = clock + config.HideSeconds; lastCountdown = -1;
        Announce($"{selected.Name} is the seeker. Hiders, run!");
    }

    private void OnAttack(PlayerAttackEventArgs e)
    {
        if (state != State.Playing || e.Attacker.Id != seeker || !hiders.Remove(e.Target.Id)) return;
        e.Cancelled = true;
        Server.SetDisguise(e.Target.Id, PluginBlocks.Air);
        Server.SetGameMode(e.Target.Id, PluginGameMode.Spectator);
        Server.TeleportPlayer(e.Target.Id, config.Lobby!.Location());
        Announce($"{e.Attacker.Name} found {e.Target.Name}!");
    }

    private void Finish(IEnumerable<PluginPlayer> players, string message)
    {
        Announce(message); state = State.Finished; stateEnds = clock + config.RestartSeconds;
        foreach (var p in players) Server.SetDisguise(p.Id, PluginBlocks.Air);
    }

    private void Reset(IEnumerable<PluginPlayer> players)
    {
        Server.RestoreRegion(baseline!);
        hiders.Clear(); seeker = ""; state = State.Waiting;
        foreach (var p in players) ToLobby(p);
    }

    private void ToLobby(PluginPlayer p)
    {
        Server.SetDisguise(p.Id, PluginBlocks.Air);
        Server.SetGameMode(p.Id, PluginGameMode.Adventure);
        if (config.Lobby != null) Server.TeleportPlayer(p.Id, config.Lobby.Location());
    }

    private void Command(CommandContext c)
    {
        if (!IsAdmin(c.Player, AdminPermission)) { c.Reply("You do not have permission."); return; }
        string action = c.Arguments.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        switch (action)
        {
            case "status": c.Reply($"HideAndSeek state={state}; ready={Ready}; hiderSpawns={config.HiderSpawns.Count}."); break;
            case "generatedefault": GenerateDefault(); c.Reply("Generated the default Hide & Seek map."); break;
            case "setlobby": config.Lobby = Point.From(c.Player); Save(); c.Reply("Lobby set."); break;
            case "setseeker": config.Seeker = Point.From(c.Player); Save(); c.Reply("Seeker release spawn set."); break;
            case "addhider": config.HiderSpawns.Add(Point.From(c.Player)); Save(); c.Reply("Hider spawn added."); break;
            case "setarena":
                var selection = Server.GetSelection(c.Player.Id);
                if (selection == null) { c.Reply("Select the map with //pos1 and //pos2."); return; }
                config.Arena = Region.From(selection.Value); baseline = Server.CaptureRegion(selection.Value); SaveAll(); c.Reply("Map baseline captured."); break;
            case "savearena":
                if (config.Arena == null) { c.Reply("Set the arena first."); return; }
                baseline = Server.CaptureRegion(config.Arena.Value()); SaveArena(); c.Reply("Map baseline saved."); break;
            case "start":
                if (!Ready) { c.Reply("Configure the map first."); return; }
                Begin(Players.ToList()); c.Reply("Hide & Seek started."); break;
            default: c.Reply("Usage: /hideandseek <status|generatedefault|setlobby|setseeker|addhider|setarena|savearena|start>"); break;
        }
    }

    private void GenerateDefault()
    {
        for (int x = -40; x <= 40; x++) for (int z = -40; z <= 40; z++)
            Server.SetBlock(x, 30, z, ((x + z) & 1) == 0 ? PluginBlocks.FineWood : PluginBlocks.Bricks);
        for (int i = -40; i <= 40; i++) for (int y = 31; y <= 35; y++)
        {
            Server.SetBlock(i, y, -40, PluginBlocks.StoneBricks); Server.SetBlock(i, y, 40, PluginBlocks.StoneBricks);
            Server.SetBlock(-40, y, i, PluginBlocks.StoneBricks); Server.SetBlock(40, y, i, PluginBlocks.StoneBricks);
        }
        for (int x = -4; x <= 4; x++) for (int z = -4; z <= 4; z++) Server.SetBlock(x, 54, z, PluginBlocks.Glass);
        config = Config.Default();
        baseline = Server.CaptureRegion(config.Arena!.Value());
        SaveAll();
    }

    private bool Ready => baseline != null && config.Lobby != null && config.Seeker != null && config.Arena != null && config.HiderSpawns.Count > 0;
    private string ConfigPath => Path.Combine(DataDirectory, "config.json");
    private string ArenaPath => Path.Combine(DataDirectory, "arena.blocks");
    private void Save() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    private void SaveArena() => File.WriteAllBytes(ArenaPath, PluginBlockStorage.Encode(baseline!.Blocks));
    private void SaveAll() { Save(); SaveArena(); }
    private void LoadFiles()
    {
        Directory.CreateDirectory(DataDirectory);
        if (File.Exists(ConfigPath)) config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new();
        if (config.Arena != null && File.Exists(ArenaPath)) { var region = config.Arena.Value(); baseline = new(region, PluginBlockStorage.Decode(File.ReadAllBytes(ArenaPath), checked((int)region.Volume))); }
    }

    private enum State { Waiting, Hiding, Playing, Finished }
    public sealed class Config
    {
        public Point? Lobby { get; set; } public Point? Seeker { get; set; } public List<Point> HiderSpawns { get; set; } = new();
        public Region? Arena { get; set; } public int MinPlayers { get; set; } = 2; public int HideSeconds { get; set; } = 20;
        public int RoundSeconds { get; set; } = 180; public int RestartSeconds { get; set; } = 8;
        public static Config Default() => new() { Lobby = new(.5f,55,.5f), Seeker = new(.5f,32,-36),
            HiderSpawns = new() { new(-32,32,32), new(-16,32,-32), new(16,32,32), new(32,32,-32) },
            Arena = new(-40,30,-40,40,55,40) };
    }
    public sealed record Point(float X,float Y,float Z) { public static Point From(PluginPlayer p)=>new(p.X,p.Y,p.Z); public PluginLocation Location()=>new(X,Y,Z); }
    public sealed record Region(int MinX,int MinY,int MinZ,int MaxX,int MaxY,int MaxZ)
    { public static Region From(BlockRegion r){r=r.Normalize();return new(r.Min.X,r.Min.Y,r.Min.Z,r.Max.X,r.Max.Y,r.Max.Z);} public BlockRegion Value()=>new(new(MinX,MinY,MinZ),new(MaxX,MaxY,MaxZ)); }
}
