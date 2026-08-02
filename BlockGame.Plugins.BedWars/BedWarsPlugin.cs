using System.Text.Json;
using BlockGame.PluginApi;
using BlockGame.PluginApi.Minigames;

namespace BlockGame.Plugins.BedWars;

public sealed class BedWarsPlugin : MinigamePlugin
{
    private const string AdminPermission = "minigame.bedwars.admin";
    private readonly Dictionary<string, int> teams = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> eliminated = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> beds = new();
    private Config config = new();
    private RegionSnapshot? baseline;
    private State state;
    private double clock, stateEnds, nextGenerator;

    public override string Name => "BedWars";
    public override string Version => "1.0.0";
    protected override string MinigameId => "bedwars";
    protected override void OnMinigameLoad() => LoadFiles();

    protected override void OnMinigameEnable()
    {
        Server.RegisterCommand(new PluginCommand("bedwars", "Configures and controls BedWars", Command,
            "/bedwars <status|generatedefault|setlobby|setteam|setbed|setgenerator|setarena|savearena|start|shop>",
            AdminPermission, new[] { "bw", "shop" }));
        Server.RegisterListener<PlayerJoinEventArgs>(e => ToLobby(e.Player));
        Server.RegisterListener<BlockEditEventArgs>(OnBlockEdit, EventPriority.Highest);
        Server.RegisterListener<PlayerDeathEventArgs>(OnDeath, EventPriority.Highest);
        Server.RegisterListener<TickEventArgs>(OnTick);
        Server.Log(Ready ? "Enabled with a configured arena." : "Enabled in setup mode.");
    }

    protected override void OnMinigameDisable() { if (baseline != null) Server.RestoreRegion(baseline); }

    private void OnTick(TickEventArgs e)
    {
        clock += e.ElapsedSeconds; if (!Ready) return;
        var online = Players.ToList();
        if (state == State.Waiting && online.Count >= config.MinPlayers) Begin(online);
        else if (state == State.Countdown && clock >= stateEnds) Start(online);
        else if (state == State.Playing)
        {
            if (clock >= nextGenerator)
            {
                nextGenerator = clock + config.GeneratorSeconds;
                foreach (var p in online)
                    if (teams.TryGetValue(p.Id, out int t) && !eliminated.Contains(p.Id) && Distance(p, config.Teams[t].Generator) < 7)
                        Server.SetItemCount(p.Id, PluginItems.IronIngot, Server.GetItemCount(p.Id, PluginItems.IronIngot) + 1);
            }
            foreach (var p in online.Where(p => teams.ContainsKey(p.Id) && !eliminated.Contains(p.Id) && p.Y < config.VoidY).ToList())
                HandleDeath(p, "the void");
            var activeTeams = online.Where(p => teams.ContainsKey(p.Id) && !eliminated.Contains(p.Id)).Select(p => teams[p.Id]).Distinct().ToList();
            if (activeTeams.Count <= 1) Finish(activeTeams.FirstOrDefault(-1));
        }
        else if (state == State.Finished && clock >= stateEnds) Reset(online);
    }

    private void Begin(IReadOnlyList<PluginPlayer> players)
    {
        Restore(); teams.Clear(); eliminated.Clear(); beds.Clear();
        for (int i = 0; i < config.Teams.Count; i++) beds.Add(i);
        int n = 0;
        foreach (var p in players)
        {
            int team = n++ % config.Teams.Count; teams[p.Id] = team;
            Server.ClearInventory(p.Id); Server.ClearArmor(p.Id); Server.SetGameMode(p.Id, PluginGameMode.Adventure);
            Server.TeleportPlayer(p.Id, config.Teams[team].Spawn.Location());
            Server.SendMessage(p.Id, $"You are on Team {team + 1}. Use /bedwars shop <blocks|sword|bow> near your generator.");
        }
        state = State.Countdown; stateEnds = clock + config.CountdownSeconds;
        Announce("Protect your bed and destroy the enemy beds!");
    }

    private void Start(IEnumerable<PluginPlayer> players)
    {
        foreach (var p in players.Where(p => teams.ContainsKey(p.Id))) Server.SetGameMode(p.Id, PluginGameMode.Survival);
        state = State.Playing; nextGenerator = clock; Announce("GO!");
    }

    private void OnDeath(PlayerDeathEventArgs e)
    {
        if (state != State.Playing || !teams.ContainsKey(e.Player.Id) || eliminated.Contains(e.Player.Id)) return;
        e.Cancelled = true; HandleDeath(e.Player, e.Cause);
    }

    private void HandleDeath(PluginPlayer p, string cause)
    {
        int team = teams[p.Id]; Server.ClearInventory(p.Id); Server.SetHealth(p.Id, 40);
        if (beds.Contains(team))
        {
            Server.TeleportPlayer(p.Id, config.Teams[team].Spawn.Location());
            Server.SendMessage(p.Id, "You respawned because your bed is intact.");
        }
        else
        {
            eliminated.Add(p.Id); ToLobby(p, PluginGameMode.Spectator);
            Announce($"{p.Name} was eliminated by {cause}!");
        }
    }

    private void OnBlockEdit(BlockEditEventArgs e)
    {
        bool active = state == State.Playing && teams.ContainsKey(e.Player.Id) && !eliminated.Contains(e.Player.Id)
            && config.Arena != null && Contains(config.Arena.Value(), e.Position) && e.PreviousBlock != PluginBlocks.Bedrock;
        if (!active) { e.Cancelled = true; e.DenialMessage = "Building is locked outside an active BedWars round."; return; }
        if (e.PreviousBlock != PluginBlocks.Bed || e.NewBlock != PluginBlocks.Air) return;
        int victim = config.Teams.FindIndex(t => t.Bed.Position() == e.Position);
        if (victim < 0) return;
        if (teams[e.Player.Id] == victim) { e.Cancelled = true; e.DenialMessage = "You cannot break your own bed."; return; }
        beds.Remove(victim); Announce($"Team {victim + 1}'s bed was destroyed by {e.Player.Name}!");
    }

    private void Shop(CommandContext c, string item)
    {
        if (state != State.Playing || !teams.TryGetValue(c.Player.Id, out int team) || eliminated.Contains(c.Player.Id)
            || Distance(c.Player, config.Teams[team].Generator) > 8) { c.Reply("Use the shop near your generator during a round."); return; }
        int cost, id, count;
        switch (item) { case "blocks": cost=4; id=config.Teams[team].WoolItem; count=16; break; case "sword": cost=8; id=PluginItems.StoneSword; count=1; break; case "bow": cost=12; id=PluginItems.Bow; count=1; break; default: c.Reply("Shop items: blocks, sword, bow."); return; }
        int iron = Server.GetItemCount(c.Player.Id, PluginItems.IronIngot);
        if (iron < cost) { c.Reply($"You need {cost} iron."); return; }
        Server.SetItemCount(c.Player.Id, PluginItems.IronIngot, iron-cost);
        Server.SetItemCount(c.Player.Id, id, Server.GetItemCount(c.Player.Id,id)+count);
        if (id==PluginItems.Bow) Server.SetItemCount(c.Player.Id,PluginItems.Arrow,Server.GetItemCount(c.Player.Id,PluginItems.Arrow)+8);
    }

    private void Finish(int winner) { Announce(winner < 0 ? "No team survived!" : $"Team {winner + 1} wins!"); state=State.Finished; stateEnds=clock+config.RestartSeconds; }
    private void Reset(IEnumerable<PluginPlayer> players) { Restore(); teams.Clear(); eliminated.Clear(); beds.Clear(); foreach(var p in players)ToLobby(p); state=State.Waiting; }
    private void Restore() { Server.RestoreRegion(baseline!); }

    private void Command(CommandContext c)
    {
        string invoked=c.Raw.Trim().TrimStart('/').Split(' ',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()??"";
        if(invoked.Equals("shop",StringComparison.OrdinalIgnoreCase)){Shop(c,c.Arguments.FirstOrDefault()?.ToLowerInvariant()??"");return;}
        string action=c.Arguments.FirstOrDefault()?.ToLowerInvariant()??"status";
        if(action=="shop"){Shop(c,c.Arguments.Skip(1).FirstOrDefault()?.ToLowerInvariant()??"");return;}
        if(!IsAdmin(c.Player,AdminPermission)){c.Reply("You do not have permission.");return;}
        switch(action)
        {
            case "status":c.Reply($"BedWars state={state}; ready={Ready}; teams={config.Teams.Count}.");break;
            case "generatedefault":GenerateDefault();c.Reply("Generated the default BedWars arena.");break;
            case "setlobby":config.Lobby=Point.From(c.Player);Save();c.Reply("Lobby set.");break;
            case "setteam":SetMarker(c,"spawn");break; case "setbed":SetMarker(c,"bed");break; case "setgenerator":SetMarker(c,"generator");break;
            case "setarena":var s=Server.GetSelection(c.Player.Id);if(s==null){c.Reply("Select the arena with //pos1 and //pos2.");return;}config.Arena=Region.From(s.Value);baseline=Server.CaptureRegion(s.Value);SaveAll();c.Reply("Arena captured.");break;
            case "savearena":if(config.Arena==null){c.Reply("Set the arena first.");return;}baseline=Server.CaptureRegion(config.Arena.Value());SaveArena();c.Reply("Arena saved.");break;
            case "start":if(!Ready){c.Reply("Configure the arena first.");return;}Begin(Players.ToList());c.Reply("Countdown started.");break;
            default:c.Reply("Usage: /bedwars <status|generatedefault|setlobby|setteam|setbed|setgenerator|setarena|savearena|start|shop>");break;
        }
    }

    private void SetMarker(CommandContext c,string kind)
    {
        if(!int.TryParse(c.Arguments.Skip(1).FirstOrDefault(),out int number)||number<1||number>config.Teams.Count){c.Reply($"Specify team 1-{config.Teams.Count}.");return;}
        var t=config.Teams[number-1]; var p=Point.From(c.Player);
        config.Teams[number-1]=kind switch{"spawn"=>t with{Spawn=p},"generator"=>t with{Generator=p},"bed"=>t with{Bed=new((int)Math.Floor(p.X),(int)Math.Floor(p.Y-1),(int)Math.Floor(p.Z))},_=>t};
        Save();c.Reply($"Team {number} {kind} set.");
    }

    private void GenerateDefault()
    {
        config=Config.Default(); BuildIsland(0,0,11);
        foreach(var t in config.Teams){BuildIsland((int)Math.Floor(t.Spawn.X),(int)Math.Floor(t.Spawn.Z),8);Server.SetBlock(t.Bed.X,t.Bed.Y,t.Bed.Z,PluginBlocks.Bed);}
        for(int x=-4;x<=4;x++)for(int z=-4;z<=4;z++)Server.SetBlock(x,55,z,PluginBlocks.Glass);
        baseline=Server.CaptureRegion(config.Arena!.Value());SaveAll();
    }
    private void BuildIsland(int cx,int cz,int r){for(int x=-r;x<=r;x++)for(int z=-r;z<=r;z++)if(x*x+z*z<=r*r)for(int y=39;y<=42;y++)Server.SetBlock(cx+x,y,cz+z,y==42?PluginBlocks.Grass:PluginBlocks.Dirt);}
    private void ToLobby(PluginPlayer p,PluginGameMode m=PluginGameMode.Adventure){Server.SetGameMode(p.Id,m);if(config.Lobby!=null)Server.TeleportPlayer(p.Id,config.Lobby.Location());}
    private bool Ready=>baseline!=null&&config.Lobby!=null&&config.Arena!=null&&config.Teams.Count>1;
    private static float Distance(PluginPlayer p,Point q)=>MathF.Sqrt((p.X-q.X)*(p.X-q.X)+(p.Y-q.Y)*(p.Y-q.Y)+(p.Z-q.Z)*(p.Z-q.Z));
    private static bool Contains(BlockRegion r,BlockPosition p){r=r.Normalize();return p.X>=r.Min.X&&p.X<=r.Max.X&&p.Y>=r.Min.Y&&p.Y<=r.Max.Y&&p.Z>=r.Min.Z&&p.Z<=r.Max.Z;}
    private string ConfigPath=>Path.Combine(DataDirectory,"config.json");private string ArenaPath=>Path.Combine(DataDirectory,"arena.blocks");
    private void Save()=>File.WriteAllText(ConfigPath,JsonSerializer.Serialize(config,new JsonSerializerOptions{WriteIndented=true}));private void SaveArena()=>File.WriteAllBytes(ArenaPath,PluginBlockStorage.Encode(baseline!.Blocks));private void SaveAll(){Save();SaveArena();}
    private void LoadFiles(){Directory.CreateDirectory(DataDirectory);if(File.Exists(ConfigPath))config=JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath))??new();if(config.Arena!=null&&File.Exists(ArenaPath)){var region=config.Arena.Value();baseline=new(region,PluginBlockStorage.Decode(File.ReadAllBytes(ArenaPath),checked((int)region.Volume)));}}
    private enum State{Waiting,Countdown,Playing,Finished}
    public sealed class Config{public Point? Lobby{get;set;}public List<Team> Teams{get;set;}=new();public Region? Arena{get;set;}public int MinPlayers{get;set;}=2;public int CountdownSeconds{get;set;}=10;public int RestartSeconds{get;set;}=8;public int GeneratorSeconds{get;set;}=2;public float VoidY{get;set;}=10;
        public static Config Default(){var s=new[]{new Point(32.5f,43,.5f),new(.5f,43,32.5f),new(-31.5f,43,.5f),new(.5f,43,-31.5f)};int[] blocks={PluginBlocks.WoolRed,PluginBlocks.WoolBlue,PluginBlocks.WoolGreen,PluginBlocks.WoolYellow};int[] items={PluginItems.WoolRed,PluginItems.WoolBlue,PluginItems.WoolGreen,PluginItems.WoolYellow};var ts=new List<Team>();for(int i=0;i<4;i++){var p=s[i];ts.Add(new(p,new((int)Math.Floor(p.X)+2,43,(int)Math.Floor(p.Z)),p,blocks[i],items[i]));}return new(){Lobby=new(.5f,56,.5f),Teams=ts,Arena=new(-48,35,-48,48,56,48)};}}
    public sealed record Point(float X,float Y,float Z){public static Point From(PluginPlayer p)=>new(p.X,p.Y,p.Z);public PluginLocation Location()=>new(X,Y,Z);}
    public sealed record Bed(int X,int Y,int Z){public BlockPosition Position()=>new(X,Y,Z);}
    public sealed record Team(Point Spawn,Bed Bed,Point Generator,int WoolBlock,int WoolItem);
    public sealed record Region(int MinX,int MinY,int MinZ,int MaxX,int MaxY,int MaxZ){public static Region From(BlockRegion r){r=r.Normalize();return new(r.Min.X,r.Min.Y,r.Min.Z,r.Max.X,r.Max.Y,r.Max.Z);}public BlockRegion Value()=>new(new(MinX,MinY,MinZ),new(MaxX,MaxY,MaxZ));}
}
