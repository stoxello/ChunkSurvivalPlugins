using System.Text.Json;
using BlockGame.PluginApi;

namespace BlockGame.Plugins.PlotMe;

public sealed class PlotMePlugin : GamePlugin
{
    private const int ChunkSize = 16;
    private const string AdminPermission = "minigame.plotme.admin";
    private readonly Dictionary<string, List<PluginChunkPosition>> plots = new(StringComparer.OrdinalIgnoreCase);
    private Config config = new();
    private Template? template;
    private bool active;

    public override string Name => "PlotMe";
    public override string Version => "1.0.5";

    public override void OnLoad()
    {
        active = Server.ServerRole.Equals("survival", StringComparison.OrdinalIgnoreCase)
            && Server.Minigame.Equals("plotme", StringComparison.OrdinalIgnoreCase);
        if (active) LoadFiles();
    }

    public override void OnEnable()
    {
        if (!active)
        {
            Server.Log($"Idle because this server is role={Server.ServerRole}, minigame={Server.Minigame}.");
            return;
        }
        Server.RegisterCommand(new PluginCommand("plot", "Manages creative building plots", Command,
            "/plot <auto|claim|home :#|list|info|setspawn|savetemplate|resettemplate|rebuild|reload>",
            aliases: new[] { "plotme", "p" }));
        Server.RegisterListener<PlayerJoinEventArgs>(e => PreparePlayer(e.Player));
        Server.RegisterListener<BlockEditEventArgs>(OnBlockEdit, EventPriority.Highest);
        foreach (var player in Server.OnlinePlayers) PreparePlayer(player);
        Server.Log(template == null ? "Enabled with the default plot template." : "Enabled with a custom plot template.");
    }

    public override void OnDisable()
    {
        if (!active) return;
        SavePlots();
        Server.SaveWorld();
    }

    private void PreparePlayer(PluginPlayer player)
    {
        Server.SetGameMode(player.Id, PluginGameMode.Creative);
        if (!plots.TryGetValue(player.Id, out var owned))
        {
            owned = DiscoverLegacyPlots(player);
            plots[player.Id] = owned;
        }
        if (owned.Count == 0)
        {
            var provisional = Server.GetHomeChunk(player.Id);
            var plot = provisional is { } existing && IsGridPlot(existing) ? existing : AllocatePlot();
            if (!Server.SetHomeChunk(player.Id, plot))
            {
                Server.SendMessage(player.Id, "Your first plot could not be allocated.");
                return;
            }
            if (provisional is { } old && old != plot) Server.ReleaseChunk(player.Id, old);
            BuildPlot(player.Id, plot);
            owned.Add(plot);
            SaveAll();
            Teleport(player, plot);
            Server.SendMessage(player.Id, "Your creative plot is ready. Use /plot home to return.");
        }
        else
        {
            var home = Server.GetHomeChunk(player.Id);
            if (home == null || !owned.Contains(home.Value)) Server.SetHomeChunk(player.Id, owned[0]);
            SavePlots();
            Server.SendMessage(player.Id, "PlotMe: /plot home returns to your plot; /plot info shows its coordinates.");
        }
    }

    private List<PluginChunkPosition> DiscoverLegacyPlots(PluginPlayer player)
    {
        var home = Server.GetHomeChunk(player.Id);
        return Server.GetClaimedChunks(player.Id)
            .Where(IsPlotSurface)
            .OrderBy(p => p == home ? 0 : 1).ThenBy(p => p.Z).ThenBy(p => p.X)
            .ToList();
    }

    private bool IsPlotSurface(PluginChunkPosition chunk)
    {
        int ox = chunk.X * ChunkSize, oz = chunk.Z * ChunkSize;
        return Server.GetBlock(ox, config.SurfaceY, oz) == config.BorderBlock
            && Server.GetBlock(ox + 8, config.SurfaceY, oz + 8) != PluginBlocks.Air;
    }

    private void OnBlockEdit(BlockEditEventArgs e)
    {
        var chunk = ChunkAt(e.Position.X, e.Position.Z);
        bool owned = plots.TryGetValue(e.Player.Id, out var playerPlots) && playerPlots.Contains(chunk);
        if (!owned || e.PreviousBlock == PluginBlocks.Bedrock)
        {
            e.Cancelled = true;
            e.DenialMessage = "You can only edit your own plot.";
            return;
        }
        e.BypassPermissions = true;
    }

    private void Command(CommandContext command)
    {
        string sub = command.Arguments.FirstOrDefault()?.ToLowerInvariant() ?? "home";
        bool adminAction = sub is "setspawn" or "savetemplate" or "resettemplate" or "rebuild" or "reload";
        if (adminAction)
        {
            if (!Server.HasPermission(command.Player.Id, AdminPermission))
            {
                command.Reply("You do not have permission."); return;
            }
            AdminCommand(command, sub);
            return;
        }
        if (!Server.HasPermission(command.Player.Id, "plotme.use")
            && !Server.HasPermission(command.Player.Id, "plotme.use." + sub))
        {
            command.Reply("You do not have permission: plotme.use." + sub); return;
        }
        if (!plots.TryGetValue(command.Player.Id, out var owned)) owned = plots[command.Player.Id] = [];
        switch (sub)
        {
            case "auto": Claim(command, owned, AllocatePlot()); break;
            case "claim": Claim(command, owned, ChunkAt(command.Player.X, command.Player.Z)); break;
            case "list":
                command.Reply($"Your plots ({owned.Count}/{Limit(command.Player)}): "
                    + string.Join(", ", owned.Select((p, i) => $":{i + 1}=({p.X},{p.Z})")));
                break;
            case "info":
                var at = ChunkAt(command.Player.X, command.Player.Z);
                command.Reply(owned.Contains(at) ? $"This is your plot at chunk ({at.X},{at.Z})."
                    : Server.IsChunkClaimed(at) ? $"Plot chunk ({at.X},{at.Z}) is claimed."
                    : $"Chunk ({at.X},{at.Z}) is an unclaimed plot or road.");
                break;
            case "home":
                int number = 1;
                string? value = command.Arguments.Skip(1).FirstOrDefault();
                if (value != null) int.TryParse(value.TrimStart(':'), out number);
                if (number < 1 || number > owned.Count) command.Reply($"Choose /plot home :1 through :{owned.Count}.");
                else { Teleport(command.Player, owned[number - 1]); command.Reply($"Teleported to plot :{number}."); }
                break;
            default: command.Reply("Usage: /plot <auto|claim|home :#|list|info>"); break;
        }
    }

    private void Claim(CommandContext command, List<PluginChunkPosition> owned, PluginChunkPosition chunk)
    {
        int limit = Limit(command.Player);
        if (owned.Count >= limit) { command.Reply($"Plot limit reached ({limit})."); return; }
        if (!IsGridPlot(chunk) || Server.IsChunkClaimed(chunk))
        {
            command.Reply("Stand inside an unclaimed plot chunk (not a road)."); return;
        }
        if (!Server.ClaimChunk(command.Player.Id, chunk)) { command.Reply("That plot could not be claimed."); return; }
        BuildPlot(command.Player.Id, chunk);
        owned.Add(chunk);
        SaveAll();
        Teleport(command.Player, chunk);
        command.Reply($"Claimed plot {owned.Count} at chunk ({chunk.X},{chunk.Z}).");
    }

    private int Limit(PluginPlayer player)
    {
        if (Server.HasPermission(player.Id, "plotme.limit.unlimited")) return int.MaxValue;
        for (int value = config.MaxPermissionLimit; value > config.DefaultPlotLimit; value--)
            if (Server.HasPermission(player.Id, "plotme.limit." + value)) return value;
        return config.DefaultPlotLimit;
    }

    private PluginChunkPosition AllocatePlot()
    {
        for (int index = 0; ; index++)
        {
            int x = index % config.GridColumns * config.GridSpacing;
            int z = index / config.GridColumns * config.GridSpacing;
            var candidate = new PluginChunkPosition(x, z);
            if (!Server.IsChunkClaimed(candidate)) return candidate;
        }
    }

    private bool IsGridPlot(PluginChunkPosition chunk) =>
        FloorMod(chunk.X, config.GridSpacing) == 0 && FloorMod(chunk.Z, config.GridSpacing) == 0;

    private void BuildPlot(string playerId, PluginChunkPosition plot)
    {
        Server.EnsureChunk(plot);
        Server.ClaimChunk(playerId, plot);
        RestoreChunk(plot, true);
        for (int dx = 0; dx < config.GridSpacing; dx++)
        for (int dz = 0; dz < config.GridSpacing; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            var road = new PluginChunkPosition(plot.X + dx, plot.Z + dz);
            Server.EnsureChunk(road);
            if (!Server.IsChunkClaimed(road)) RestoreChunk(road, false);
        }
    }

    private void RestoreChunk(PluginChunkPosition chunk, bool plot)
    {
        var region = new BlockRegion(
            new BlockPosition(chunk.X * ChunkSize, config.ClearMinY, chunk.Z * ChunkSize),
            new BlockPosition(chunk.X * ChunkSize + 15, config.ClearMaxY, chunk.Z * ChunkSize + 15));
        var blocks = Enumerable.Repeat(PluginBlocks.Air, checked((int)region.Volume)).ToArray();
        if (plot && template != null) PasteTemplate(blocks, region, template);
        else DrawDefault(blocks, region, plot);
        Server.RestoreRegion(new RegionSnapshot(region, blocks));
    }

    private void DrawDefault(ushort[] blocks, BlockRegion region, bool plot)
    {
        for (int x = 0; x < ChunkSize; x++)
        for (int z = 0; z < ChunkSize; z++)
        {
            if (plot)
            {
                for (int y = config.SurfaceY - config.PlotDepth; y < config.SurfaceY; y++)
                    Put(blocks, region, x, y, z, config.FillBlock);
                bool border = x == 0 || x == 15 || z == 0 || z == 15;
                Put(blocks, region, x, config.SurfaceY, z, border ? config.BorderBlock : config.SurfaceBlock);
            }
            else
            {
                Put(blocks, region, x, config.SurfaceY - 1, z, config.RoadFoundationBlock);
                Put(blocks, region, x, config.SurfaceY, z, config.RoadBlock);
            }
        }
    }

    private static void PasteTemplate(ushort[] destination, BlockRegion region, Template source)
    {
        int i = 0;
        for (int x = source.MinX; x <= source.MaxX; x++)
        for (int y = source.MinY; y <= source.MaxY; y++)
        for (int z = source.MinZ; z <= source.MaxZ; z++)
            Put(destination, region, x, y, z, source.Blocks[i++]);
    }

    private static void Put(ushort[] blocks, BlockRegion region, int localX, int y, int localZ, ushort block)
    {
        if (localX is < 0 or >= ChunkSize || localZ is < 0 or >= ChunkSize
            || y < region.Min.Y || y > region.Max.Y) return;
        int height = region.Max.Y - region.Min.Y + 1;
        blocks[(localX * height + y - region.Min.Y) * ChunkSize + localZ] = block;
    }

    private void AdminCommand(CommandContext command, string sub)
    {
        switch (sub)
        {
            case "reload": LoadConfigAndTemplate(); command.Reply("PlotMe configuration reloaded."); break;
            case "setspawn":
                var chunk = ChunkAt(command.Player.X, command.Player.Z);
                config.Spawn = new Point((int)Math.Floor(command.Player.X) - chunk.X * ChunkSize,
                    command.Player.Y, (int)Math.Floor(command.Player.Z) - chunk.Z * ChunkSize);
                SaveConfig(); command.Reply("Plot spawn set from your current position."); break;
            case "savetemplate": SaveTemplate(command); break;
            case "resettemplate":
                template = null; if (File.Exists(TemplatePath)) File.Delete(TemplatePath);
                command.Reply("New plots will use the default template."); break;
            case "rebuild":
                var at = ChunkAt(command.Player.X, command.Player.Z);
                if (!plots.TryGetValue(command.Player.Id, out var owned) || !owned.Contains(at))
                    command.Reply("Stand inside one of your plots first.");
                else { RestoreChunk(at, true); Server.SaveWorld(); command.Reply("This plot was rebuilt from the current template."); }
                break;
        }
    }

    private void SaveTemplate(CommandContext command)
    {
        var selection = Server.GetSelection(command.Player.Id)?.Normalize();
        var chunk = ChunkAt(command.Player.X, command.Player.Z);
        if (selection == null) { command.Reply("Select the plot with //pos1 and //pos2."); return; }
        if (!plots.TryGetValue(command.Player.Id, out var owned) || !owned.Contains(chunk))
        { command.Reply("Stand inside one of your plots first."); return; }
        int ox = chunk.X * ChunkSize, oz = chunk.Z * ChunkSize;
        if (selection.Value.Min.X < ox || selection.Value.Max.X >= ox + ChunkSize
            || selection.Value.Min.Z < oz || selection.Value.Max.Z >= oz + ChunkSize)
        { command.Reply("The template selection must stay inside one plot chunk."); return; }
        var snapshot = Server.CaptureRegion(selection.Value);
        template = new Template(selection.Value.Min.X - ox, selection.Value.Min.Y, selection.Value.Min.Z - oz,
            selection.Value.Max.X - ox, selection.Value.Max.Y, selection.Value.Max.Z - oz, snapshot.CopyBlocks());
        File.WriteAllText(TemplatePath, JsonSerializer.Serialize(template, JsonOptions));
        command.Reply("Plot template saved for new plots and /plot rebuild.");
    }

    private void Teleport(PluginPlayer player, PluginChunkPosition plot) =>
        Server.TeleportPlayer(player.Id, new PluginLocation(plot.X * ChunkSize + config.Spawn.X + 0.5f,
            config.Spawn.Y, plot.Z * ChunkSize + config.Spawn.Z + 0.5f));

    private void LoadFiles()
    {
        Directory.CreateDirectory(DataDirectory);
        LoadConfigAndTemplate();
        plots.Clear();
        if (File.Exists(PlotsPath))
            foreach (var entry in JsonSerializer.Deserialize<Dictionary<string, List<PluginChunkPosition>>>(
                File.ReadAllText(PlotsPath), JsonOptions) ?? [])
                plots[entry.Key] = entry.Value;
    }

    private void LoadConfigAndTemplate()
    {
        config = File.Exists(ConfigPath)
            ? JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonOptions) ?? new Config()
            : new Config();
        config.GridSpacing = Math.Max(2, config.GridSpacing);
        config.GridColumns = Math.Max(1, config.GridColumns);
        config.DefaultPlotLimit = Math.Max(1, config.DefaultPlotLimit);
        SaveConfig();
        template = File.Exists(TemplatePath)
            ? JsonSerializer.Deserialize<Template>(File.ReadAllText(TemplatePath), JsonOptions) : null;
    }

    private void SaveAll() { SavePlots(); Server.SaveWorld(); }
    private void SaveConfig() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    private void SavePlots() => File.WriteAllText(PlotsPath, JsonSerializer.Serialize(plots, JsonOptions));
    private string ConfigPath => Path.Combine(DataDirectory, "config.json");
    private string TemplatePath => Path.Combine(DataDirectory, "template.json");
    private string PlotsPath => Path.Combine(DataDirectory, "plots.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static PluginChunkPosition ChunkAt(float x, float z) =>
        new(FloorDiv((int)Math.Floor(x), ChunkSize), FloorDiv((int)Math.Floor(z), ChunkSize));
    private static int FloorDiv(int value, int divisor) { int q = value / divisor, r = value % divisor; return r < 0 ? q - 1 : q; }
    private static int FloorMod(int value, int divisor) { int r = value % divisor; return r < 0 ? r + divisor : r; }

    public sealed class Config
    {
        public int ClearMinY { get; set; } = -64;
        public int ClearMaxY { get; set; } = 319;
        public int SurfaceY { get; set; } = 30;
        public int PlotDepth { get; set; } = 3;
        public ushort FillBlock { get; set; } = PluginBlocks.Dirt;
        public ushort SurfaceBlock { get; set; } = PluginBlocks.Grass;
        public ushort BorderBlock { get; set; } = PluginBlocks.WoolWhite;
        public ushort RoadFoundationBlock { get; set; } = PluginBlocks.Stone;
        public ushort RoadBlock { get; set; } = PluginBlocks.StoneBricks;
        public Point Spawn { get; set; } = new(8, 31, 8);
        public int GridSpacing { get; set; } = 2;
        public int GridColumns { get; set; } = 16;
        public int DefaultPlotLimit { get; set; } = 2;
        public int MaxPermissionLimit { get; set; } = 100;
    }

    public sealed record Point(int X, float Y, int Z);
    public sealed record Template(int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ, ushort[] Blocks);
}
