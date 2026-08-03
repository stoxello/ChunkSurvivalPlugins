using System.Text.Json;
using BlockGame.PluginApi;
using BlockGame.PluginApi.Minigames;

namespace BlockGame.Plugins.SkyWars;

public sealed class SkyWarsPlugin : MinigamePlugin
{
    private const string AdminPermission = "minigame.skywars.admin";
    private readonly HashSet<string> alive = new(StringComparer.OrdinalIgnoreCase);
    private Config config = new();
    private RegionSnapshot? baseline;
    private State state;
    private double clock, stateEnds;
    private int lastCountdown = -1;

    public override string Name => "SkyWars";
    public override string Version => "1.0.5";
    protected override string MinigameId => "skywars";

    protected override void OnMinigameLoad() => LoadFiles();

    protected override void OnMinigameEnable()
    {
        Server.RegisterCommand(new PluginCommand("skywars", "Configures and controls SkyWars", Command,
            "/skywars <status|generatedefault|setlobby|addspawn|addchest|setarena|savearena|start>",
            AdminPermission, new[] { "sw" }));
        Server.RegisterListener<PlayerJoinEventArgs>(e => ToLobby(e.Player));
        Server.RegisterListener<BlockEditEventArgs>(OnBlockEdit, EventPriority.Highest);
        Server.RegisterListener<PlayerDeathEventArgs>(OnDeath, EventPriority.Highest);
        Server.RegisterListener<TickEventArgs>(OnTick);
        Server.Log(Ready ? "Enabled with a configured arena." : "Enabled in setup mode.");
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
        if (state == State.Waiting && online.Count >= config.MinPlayers) BeginCountdown(online);
        else if (state == State.Countdown)
        {
            if (online.Count < config.MinPlayers) { state = State.Waiting; return; }
            int left = Math.Max(0, (int)Math.Ceiling(stateEnds - clock));
            if (left != lastCountdown && (left <= 5 || left % 5 == 0))
            { lastCountdown = left; Announce($"Starting in {left}..."); }
            if (clock >= stateEnds) StartRound(online);
        }
        else if (state == State.Playing)
        {
            var ids = online.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            alive.RemoveWhere(id => !ids.Contains(id));
            foreach (var p in online.Where(p => alive.Contains(p.Id) && p.Y < config.VoidY).ToList())
                Eliminate(p, "the void");
            if (alive.Count <= 1) Finish(online);
        }
        else if (state == State.Finished && clock >= stateEnds) Reset(online);
    }

    private void BeginCountdown(IEnumerable<PluginPlayer> players)
    {
        RestoreArena();
        foreach (var p in players) ToLobby(p);
        state = State.Countdown; stateEnds = clock + config.CountdownSeconds; lastCountdown = -1;
        Announce("Round starting soon!");
    }

    private void StartRound(IReadOnlyList<PluginPlayer> players)
    {
        alive.Clear();
        int index = 0;
        foreach (var p in players)
        {
            Server.ClearInventory(p.Id); Server.ClearArmor(p.Id); Server.SetHealth(p.Id, 40);
            if (index < config.Spawns.Count)
            {
                alive.Add(p.Id); Server.SetGameMode(p.Id, PluginGameMode.Survival);
                Server.TeleportPlayer(p.Id, config.Spawns[index++].Location());
            }
            else ToLobby(p, PluginGameMode.Spectator);
        }
        state = State.Playing;
        Announce("GO! Loot, bridge to the center, and be the last survivor.");
    }

    private void OnDeath(PlayerDeathEventArgs e)
    {
        if (state != State.Playing || !alive.Contains(e.Player.Id)) return;
        e.Cancelled = true; Eliminate(e.Player, e.Cause);
    }

    private void Eliminate(PluginPlayer player, string cause)
    {
        if (!alive.Remove(player.Id)) return;
        Server.SetHealth(player.Id, 40);
        ToLobby(player, PluginGameMode.Spectator);
        Announce($"{player.Name} was eliminated by {cause}!");
    }

    private void Finish(IEnumerable<PluginPlayer> players)
    {
        var winner = players.FirstOrDefault(p => alive.Contains(p.Id));
        Announce(winner == null ? "Nobody survived!" : $"{winner.Name} wins!");
        state = State.Finished; stateEnds = clock + config.RestartSeconds;
    }

    private void Reset(IEnumerable<PluginPlayer> players)
    {
        RestoreArena(); alive.Clear();
        foreach (var p in players) ToLobby(p);
        state = State.Waiting;
    }

    private void OnBlockEdit(BlockEditEventArgs e)
    {
        bool valid = state == State.Playing && alive.Contains(e.Player.Id)
            && config.Arena != null && Contains(config.Arena.Value(), e.Position)
            && e.PreviousBlock != PluginBlocks.Bedrock;
        if (valid) return;
        e.Cancelled = true; e.DenialMessage = "Building is locked outside an active SkyWars round.";
    }

    private void RestoreArena()
    {
        Server.RestoreRegion(baseline!);
        foreach (var chest in config.Chests)
        {
            var pos = chest.Position();
            Server.ClearContainer(pos);
            Server.SetContainerItemCount(pos, chest.Center ? PluginItems.IronSword : PluginItems.StoneSword, 1);
            Server.SetContainerItemCount(pos, PluginItems.Wood, chest.Center ? 32 : 20);
            Server.SetContainerItemCount(pos, PluginItems.Apple, chest.Center ? 4 : 2);
            Server.SetContainerItemCount(pos, PluginItems.Bow, chest.Center ? 1 : 0);
            Server.SetContainerItemCount(pos, PluginItems.Arrow, chest.Center ? 16 : 4);
        }
    }

    private void Command(CommandContext c)
    {
        if (!IsAdmin(c.Player, AdminPermission)) { c.Reply("You do not have permission."); return; }
        string action = c.Arguments.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        switch (action)
        {
            case "status": c.Reply($"SkyWars state={state}; ready={Ready}; spawns={config.Spawns.Count}; chests={config.Chests.Count}."); break;
            case "generatedefault": GenerateDefault(); c.Reply("Generated the default SkyWars arena."); break;
            case "setlobby": config.Lobby = Point.From(c.Player); Save(); c.Reply("Lobby set."); break;
            case "addspawn": config.Spawns.Add(Point.From(c.Player)); Save(); c.Reply("Player spawn added."); break;
            case "addchest":
                bool center = c.Arguments.Skip(1).FirstOrDefault()?.Equals("center", StringComparison.OrdinalIgnoreCase) == true;
                config.Chests.Add(new Chest((int)Math.Floor(c.Player.X), (int)Math.Floor(c.Player.Y - 1), (int)Math.Floor(c.Player.Z), center));
                Save(); c.Reply(center ? "Center chest added." : "Island chest added."); break;
            case "setarena":
                var selection = Server.GetSelection(c.Player.Id);
                if (selection == null) { c.Reply("Select the arena with //pos1 and //pos2."); return; }
                config.Arena = Region.From(selection.Value); baseline = Server.CaptureRegion(selection.Value); SaveAll(); c.Reply("Arena baseline captured."); break;
            case "savearena":
                if (config.Arena == null) { c.Reply("Set the arena first."); return; }
                baseline = Server.CaptureRegion(config.Arena.Value()); SaveArena(); c.Reply("Arena baseline saved."); break;
            case "start":
                if (!Ready) { c.Reply("Configure the arena first."); return; }
                BeginCountdown(Players); c.Reply("Countdown started."); break;
            default: c.Reply("Usage: /skywars <status|generatedefault|setlobby|addspawn|addchest|setarena|savearena|start>"); break;
        }
    }

    private void GenerateDefault()
    {
        var defaults = Config.Default();
        BuildIsland(0, 0, 9, true);
        foreach (var spawn in defaults.Spawns) BuildIsland((int)Math.Floor(spawn.X), (int)Math.Floor(spawn.Z), 6, false);
        for (int x = -4; x <= 4; x++) for (int z = -4; z <= 4; z++) Server.SetBlock(x, 53, z, PluginBlocks.Glass);
        config = defaults; baseline = Server.CaptureRegion(config.Arena!.Value()); SaveAll(); RestoreArena();
    }

    private void BuildIsland(int cx, int cz, int radius, bool center)
    {
        for (int x = -radius; x <= radius; x++) for (int z = -radius; z <= radius; z++)
        {
            float d = MathF.Sqrt(x * x + z * z); if (d > radius) continue;
            int depth = Math.Max(2, (int)((radius - d) * .65f) + 2);
            for (int y = 43 - depth; y < 43; y++) Server.SetBlock(cx + x, y, cz + z, y == 42 ? PluginBlocks.Grass : PluginBlocks.Dirt);
        }
        Server.SetBlock(cx, 43, cz, PluginBlocks.Chest);
    }

    private void ToLobby(PluginPlayer p, PluginGameMode mode = PluginGameMode.Adventure)
    { Server.SetGameMode(p.Id, mode); if (config.Lobby != null) Server.TeleportPlayer(p.Id, config.Lobby.Location()); }
    private bool Ready => baseline != null && config.Lobby != null && config.Arena != null && config.Spawns.Count > 1;
    private static bool Contains(BlockRegion r, BlockPosition p) { r=r.Normalize(); return p.X>=r.Min.X&&p.X<=r.Max.X&&p.Y>=r.Min.Y&&p.Y<=r.Max.Y&&p.Z>=r.Min.Z&&p.Z<=r.Max.Z; }
    private string ConfigPath => Path.Combine(DataDirectory, "config.json"); private string ArenaPath => Path.Combine(DataDirectory, "arena.blocks");
    private void Save()=>File.WriteAllText(ConfigPath,JsonSerializer.Serialize(config,new JsonSerializerOptions{WriteIndented=true}));
    private void SaveArena()=>File.WriteAllBytes(ArenaPath,PluginBlockStorage.Encode(baseline!.Blocks)); private void SaveAll(){Save();SaveArena();}
    private void LoadFiles(){Directory.CreateDirectory(DataDirectory);if(File.Exists(ConfigPath))config=JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath))??new();if(config.Arena!=null&&File.Exists(ArenaPath)){var region=config.Arena.Value();baseline=new(region,PluginBlockStorage.Decode(File.ReadAllBytes(ArenaPath),checked((int)region.Volume)));}}

    private enum State { Waiting, Countdown, Playing, Finished }
    public sealed class Config
    {
        public Point? Lobby{get;set;} public List<Point> Spawns{get;set;}=new(); public List<Chest> Chests{get;set;}=new(); public Region? Arena{get;set;}
        public int MinPlayers{get;set;}=2; public int CountdownSeconds{get;set;}=10; public int RestartSeconds{get;set;}=8; public float VoidY{get;set;}=10;
        public static Config Default(){var s=new[]{new Point(24.5f,44,.5f),new(17.5f,44,17.5f),new(.5f,44,24.5f),new(-16.5f,44,17.5f),new(-23.5f,44,.5f),new(-16.5f,44,-16.5f),new(.5f,44,-23.5f),new(17.5f,44,-16.5f)};var c=new List<Chest>{new(0,43,0,true)};c.AddRange(s.Select(p=>new Chest((int)Math.Floor(p.X),43,(int)Math.Floor(p.Z),false)));return new(){Lobby=new(.5f,55,.5f),Spawns=s.ToList(),Chests=c,Arena=new(-48,35,-48,47,55,47)};}
    }
    public sealed record Point(float X,float Y,float Z){public static Point From(PluginPlayer p)=>new(p.X,p.Y,p.Z);public PluginLocation Location()=>new(X,Y,Z);}
    public sealed record Chest(int X,int Y,int Z,bool Center){public BlockPosition Position()=>new(X,Y,Z);}
    public sealed record Region(int MinX,int MinY,int MinZ,int MaxX,int MaxY,int MaxZ){public static Region From(BlockRegion r){r=r.Normalize();return new(r.Min.X,r.Min.Y,r.Min.Z,r.Max.X,r.Max.Y,r.Max.Z);}public BlockRegion Value()=>new(new(MinX,MinY,MinZ),new(MaxX,MaxY,MaxZ));}
}
