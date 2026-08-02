using System.Text.Json;
using BlockGame.PluginApi;

namespace BlockGame.Plugins.SkyBlock;

public sealed class SkyBlockPlugin : GamePlugin
{
    private const string AdminPermission = "minigame.skyblock.admin";
    private const int ChunkSize = 16;
    private readonly HashSet<string> initialized = new(StringComparer.OrdinalIgnoreCase);
    private Config config = new();
    private Template? template;
    private IPluginTask? generatorTask;
    private bool active;

    public override string Name => "SkyBlock";
    public override string Version => "1.0.0";

    public override void OnLoad()
    {
        active = Server.ServerRole.Equals("survival", StringComparison.OrdinalIgnoreCase)
            && Server.Minigame.Equals("skyblock", StringComparison.OrdinalIgnoreCase);
        if (active) LoadFiles();
    }

    public override void OnEnable()
    {
        if (!active)
        {
            Server.Log($"Idle because this server is role={Server.ServerRole}, minigame={Server.Minigame}.");
            return;
        }

        Server.RegisterCommand(new PluginCommand(
            "island", "Manages a SkyBlock island", Command,
            "/island <home|info|reload|setspawn|setgenerator|savetemplate|resettemplate|rebuild>",
            aliases: new[] { "is" }));
        Server.RegisterListener<PlayerJoinEventArgs>(e => PrepareIsland(e.Player));
        generatorTask = Server.RunTaskTimer(
            TimeSpan.FromSeconds(config.GeneratorSeconds),
            TimeSpan.FromSeconds(config.GeneratorSeconds),
            RefillGenerators);
        foreach (var player in Server.OnlinePlayers) PrepareIsland(player);
        Server.Log(template == null ? "Enabled with the default island template." : "Enabled with a custom island template.");
    }

    public override void OnDisable()
    {
        generatorTask?.Cancel();
        SaveInitialized();
        if (active) Server.SaveWorld();
    }

    private void PrepareIsland(PluginPlayer player)
    {
        var home = Server.GetHomeChunk(player.Id);
        if (home == null || !Server.EnsureChunk(home.Value) || !Server.ClaimChunk(player.Id, home.Value))
        {
            Server.SendMessage(player.Id, "Your SkyBlock island could not be allocated.");
            return;
        }

        bool legacyIsland = Server.GetBlock(
            WorldX(home.Value, config.Chest.X), (int)Math.Floor(config.Chest.Y),
            WorldZ(home.Value, config.Chest.Z)) == PluginBlocks.Chest;
        if (!initialized.Contains(player.Id) && !legacyIsland)
        {
            BuildIsland(home.Value);
            FillStarterChest(home.Value);
            initialized.Add(player.Id);
            SaveInitialized();
            Server.SaveWorld();
            TeleportHome(player);
            Server.SendMessage(player.Id, "Your SkyBlock island is ready. Use /island home to return.");
        }
        else
        {
            initialized.Add(player.Id);
        }
    }

    private void RefillGenerators()
    {
        foreach (var home in Server.OnlinePlayers
            .Select(p => Server.GetHomeChunk(p.Id))
            .Where(p => p.HasValue).Select(p => p!.Value).Distinct())
        {
            var position = At(home, config.Generator);
            if (Server.GetBlock(position.X, position.Y, position.Z) == PluginBlocks.Air)
                Server.SetBlock(position.X, position.Y, position.Z, config.GeneratorBlock);
        }
    }

    private void BuildIsland(PluginChunkPosition chunk)
    {
        var worldRegion = new BlockRegion(
            new BlockPosition(WorldX(chunk, 0), config.ClearMinY, WorldZ(chunk, 0)),
            new BlockPosition(WorldX(chunk, 15), config.ClearMaxY, WorldZ(chunk, 15)));
        var blocks = Enumerable.Repeat(PluginBlocks.Air, checked((int)worldRegion.Volume)).ToArray();

        if (template == null)
            DrawDefault(blocks, worldRegion);
        else
            PasteTemplate(blocks, worldRegion, template);

        Server.RestoreRegion(new RegionSnapshot(worldRegion, blocks));
    }

    private void DrawDefault(ushort[] blocks, BlockRegion region)
    {
        for (int x = 2; x <= 13; x++)
        for (int z = 2; z <= 13; z++)
        {
            float dx = x - 7.5f, dz = z - 7.5f;
            if (dx * dx + dz * dz > 34f) continue;
            int depth = Math.Max(2, 5 - (int)Math.Sqrt(dx * dx + dz * dz) / 2);
            for (int y = config.IslandTopY - depth; y <= config.IslandTopY; y++)
                Put(blocks, region, x, y, z, y == config.IslandTopY ? PluginBlocks.Grass : PluginBlocks.Dirt);
        }
        for (int y = config.IslandTopY + 1; y <= config.IslandTopY + 4; y++)
            Put(blocks, region, 5, y, 8, PluginBlocks.Wood);
        for (int x = 3; x <= 7; x++)
        for (int y = config.IslandTopY + 3; y <= config.IslandTopY + 6; y++)
        for (int z = 6; z <= 10; z++)
            if (Math.Abs(x - 5) + Math.Abs(z - 8) + Math.Abs(y - (config.IslandTopY + 4)) <= 4)
                Put(blocks, region, x, y, z, PluginBlocks.Leaves, onlyAir: true);
        Put(blocks, region, config.Chest.X, (int)Math.Floor(config.Chest.Y), config.Chest.Z, PluginBlocks.Chest);
        Put(blocks, region, config.Generator.X, (int)Math.Floor(config.Generator.Y), config.Generator.Z, config.GeneratorBlock);
    }

    private static void PasteTemplate(ushort[] destination, BlockRegion region, Template source)
    {
        int i = 0;
        for (int x = source.MinX; x <= source.MaxX; x++)
        for (int y = source.MinY; y <= source.MaxY; y++)
        for (int z = source.MinZ; z <= source.MaxZ; z++)
            Put(destination, region, x, y, z, source.Blocks[i++]);
    }

    private static void Put(ushort[] blocks, BlockRegion region, int localX, int y, int localZ, ushort block, bool onlyAir = false)
    {
        if (localX < 0 || localX >= ChunkSize || localZ < 0 || localZ >= ChunkSize
            || y < region.Min.Y || y > region.Max.Y) return;
        int height = region.Max.Y - region.Min.Y + 1;
        int index = (localX * height + y - region.Min.Y) * ChunkSize + localZ;
        if (!onlyAir || blocks[index] == PluginBlocks.Air) blocks[index] = block;
    }

    private void FillStarterChest(PluginChunkPosition chunk)
    {
        var chest = At(chunk, config.Chest);
        Server.ClearContainer(chest);
        foreach (var item in config.StarterItems)
            Server.SetContainerItemCount(chest, item.ItemId, item.Count);
    }

    private void Command(CommandContext command)
    {
        string action = command.Arguments.FirstOrDefault()?.ToLowerInvariant() ?? "home";
        if (action == "home") { TeleportHome(command.Player); return; }
        if (action == "info")
        {
            var home = Server.GetHomeChunk(command.Player.Id);
            command.Reply(home == null ? "You do not have an island." : $"Your island is chunk ({home.Value.X}, {home.Value.Z}).");
            return;
        }
        if (!Server.HasPermission(command.Player.Id, AdminPermission))
        {
            command.Reply("You do not have permission.");
            return;
        }

        switch (action)
        {
            case "reload":
                generatorTask?.Cancel(); LoadFiles();
                generatorTask = Server.RunTaskTimer(TimeSpan.FromSeconds(config.GeneratorSeconds),
                    TimeSpan.FromSeconds(config.GeneratorSeconds), RefillGenerators);
                command.Reply("SkyBlock configuration reloaded.");
                break;
            case "setspawn":
                SetRelativePoint(command, p => config.Spawn = p, "spawn");
                break;
            case "setgenerator":
                SetRelativePoint(command, p => config.Generator = p, "generator");
                break;
            case "savetemplate":
                SaveTemplate(command);
                break;
            case "resettemplate":
                template = null;
                if (File.Exists(TemplatePath)) File.Delete(TemplatePath);
                command.Reply("New islands will use the default template.");
                break;
            case "rebuild":
                var home = Server.GetHomeChunk(command.Player.Id);
                if (home == null) { command.Reply("You do not have an island."); break; }
                BuildIsland(home.Value); FillStarterChest(home.Value); Server.SaveWorld(); TeleportHome(command.Player);
                command.Reply("Your island was rebuilt from the current template.");
                break;
            default:
                command.Reply("Usage: /island <home|info|reload|setspawn|setgenerator|savetemplate|resettemplate|rebuild>");
                break;
        }
    }

    private void SetRelativePoint(CommandContext command, Action<Point> setter, string label)
    {
        var home = Server.GetHomeChunk(command.Player.Id);
        if (home == null) { command.Reply("You do not have an island."); return; }
        int x = (int)Math.Floor(command.Player.X) - home.Value.X * ChunkSize;
        int z = (int)Math.Floor(command.Player.Z) - home.Value.Z * ChunkSize;
        if (x is < 0 or >= ChunkSize || z is < 0 or >= ChunkSize)
        {
            command.Reply("Stand inside your home chunk first."); return;
        }
        setter(new Point(x, command.Player.Y, z));
        SaveConfig();
        command.Reply($"New-player {label} set to local ({x}, {command.Player.Y:0.0}, {z}).");
    }

    private void SaveTemplate(CommandContext command)
    {
        var home = Server.GetHomeChunk(command.Player.Id);
        var selection = Server.GetSelection(command.Player.Id)?.Normalize();
        if (home == null || selection == null) { command.Reply("Select the island with //pos1 and //pos2."); return; }
        int ox = home.Value.X * ChunkSize, oz = home.Value.Z * ChunkSize;
        if (selection.Value.Min.X < ox || selection.Value.Max.X >= ox + ChunkSize
            || selection.Value.Min.Z < oz || selection.Value.Max.Z >= oz + ChunkSize)
        {
            command.Reply("The template selection must stay inside your home chunk."); return;
        }
        var snapshot = Server.CaptureRegion(selection.Value);
        template = new Template(
            selection.Value.Min.X - ox, selection.Value.Min.Y, selection.Value.Min.Z - oz,
            selection.Value.Max.X - ox, selection.Value.Max.Y, selection.Value.Max.Z - oz,
            snapshot.CopyBlocks());
        File.WriteAllText(TemplatePath, JsonSerializer.Serialize(template, JsonOptions));
        command.Reply("Island template saved. It will be used for new islands and /island rebuild.");
    }

    private void TeleportHome(PluginPlayer player)
    {
        var home = Server.GetHomeChunk(player.Id);
        if (home == null) { Server.SendMessage(player.Id, "You do not have an island."); return; }
        Server.TeleportPlayer(player.Id, new PluginLocation(
            WorldX(home.Value, config.Spawn.X) + 0.5f, config.Spawn.Y,
            WorldZ(home.Value, config.Spawn.Z) + 0.5f));
    }

    private void LoadFiles()
    {
        Directory.CreateDirectory(DataDirectory);
        config = File.Exists(ConfigPath)
            ? JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonOptions) ?? new Config()
            : new Config();
        config.GeneratorSeconds = Math.Max(0.1, config.GeneratorSeconds);
        SaveConfig();
        template = File.Exists(TemplatePath)
            ? JsonSerializer.Deserialize<Template>(File.ReadAllText(TemplatePath), JsonOptions) : null;
        initialized.Clear();
        if (File.Exists(IslandsPath))
            foreach (string id in JsonSerializer.Deserialize<string[]>(File.ReadAllText(IslandsPath), JsonOptions) ?? [])
                initialized.Add(id);
    }

    private void SaveConfig() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    private void SaveInitialized() => File.WriteAllText(IslandsPath, JsonSerializer.Serialize(initialized.OrderBy(x => x), JsonOptions));
    private string ConfigPath => Path.Combine(DataDirectory, "config.json");
    private string TemplatePath => Path.Combine(DataDirectory, "template.json");
    private string IslandsPath => Path.Combine(DataDirectory, "islands.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static int WorldX(PluginChunkPosition chunk, int localX) => chunk.X * ChunkSize + localX;
    private static int WorldZ(PluginChunkPosition chunk, int localZ) => chunk.Z * ChunkSize + localZ;
    private static BlockPosition At(PluginChunkPosition chunk, Point point) =>
        new(WorldX(chunk, point.X), (int)Math.Floor(point.Y), WorldZ(chunk, point.Z));

    public sealed class Config
    {
        public int ClearMinY { get; set; } = -64;
        public int ClearMaxY { get; set; } = 319;
        public int IslandTopY { get; set; } = 41;
        public Point Spawn { get; set; } = new(8, 43, 8);
        public Point Chest { get; set; } = new(8, 42, 8);
        public Point Generator { get; set; } = new(11, 42, 8);
        public ushort GeneratorBlock { get; set; } = PluginBlocks.Cobblestone;
        public double GeneratorSeconds { get; set; } = 1;
        public List<StarterItem> StarterItems { get; set; } =
        [
            new(PluginItems.Water, 1), new(PluginItems.Lava, 1),
            new(PluginItems.Sprout, 2), new(PluginItems.Dirt, 16)
        ];
    }

    public sealed record Point(int X, float Y, int Z);
    public sealed record StarterItem(int ItemId, int Count);
    public sealed record Template(int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ, ushort[] Blocks);
}
