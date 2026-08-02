using System.Text.Json;
using System.Text.Json.Serialization;
using BlockGame.PluginApi;

namespace BlockGame.Plugins.Citizens;

/// <summary>
/// Citizens-style NPCs: named, persistent, clickable characters that a builder
/// places and edits entirely from chat commands. An NPC is a server entity the
/// simulation ignores, so it stands where it was put, cannot be killed, and only
/// moves when this plugin moves it — along an author-placed waypoint route, or to
/// face whoever walks up to it.
/// </summary>
public sealed class CitizensPlugin : GamePlugin
{
    private const string AdminPermission = "citizens.admin";
    private readonly Dictionary<int, Npc> npcs = new();
    private readonly Dictionary<string, int> selections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> lastInteract = new(StringComparer.OrdinalIgnoreCase);
    private Config config = new();
    private int nextId = 1;

    public override string Name => "Citizens";
    public override string Version => "1.0.0";

    public override void OnLoad() => LoadFiles();

    public override void OnEnable()
    {
        Server.RegisterCommand(new PluginCommand("npc", "Creates and edits NPCs", Command,
            "/npc <create|list|select|remove|rename|type|equip|face|tp|tphere|lookclose|text|path|reload>",
            aliases: new[] { "citizens" }));
        Server.RegisterListener<NpcInteractEventArgs>(OnInteract);
        Server.RegisterListener<PlayerQuitEventArgs>(e =>
        {
            selections.Remove(e.Player.Id);
            lastInteract.Remove(e.Player.Id);
        });

        foreach (var npc in npcs.Values.OrderBy(n => n.Id)) Spawn(npc);
        Server.RunTaskTimer(TimeSpan.Zero, TimeSpan.FromSeconds(config.UpdateIntervalSeconds), Update);
        Server.Log($"Enabled with {npcs.Count} NPC(s).");
    }

    public override void OnDisable()
    {
        SaveNpcs();
        // Scheduled tasks and listeners are released by the server; the entities are
        // ours, so they are removed here rather than left standing in a saved world.
        foreach (var npc in npcs.Values.Where(n => n.EntityId != 0)) Server.RemoveNpc(npc.EntityId);
    }

    // ---- Simulation --------------------------------------------------------

    private void Update()
    {
        float dt = (float)config.UpdateIntervalSeconds;
        foreach (var npc in npcs.Values)
        {
            if (npc.EntityId == 0) continue;
            var live = Server.GetNpc(npc.EntityId);
            if (live == null) { npc.EntityId = 0; continue; }   // removed underneath us
            if (npc.Walking && npc.Waypoints.Count > 0) Walk(npc, live, dt);
            else if (npc.LookClose) LookAtNearestPlayer(npc, live);
        }
    }

    // Straight-line movement between waypoints, looping. There is no pathfinding:
    // an NPC walks the line it was given, so waypoints belong on a walkable route.
    private void Walk(Npc npc, PluginNpc live, float dt)
    {
        var target = npc.Waypoints[npc.WaypointIndex % npc.Waypoints.Count];
        float dx = target.X - live.Location.X;
        float dy = target.Y - live.Location.Y;
        float dz = target.Z - live.Location.Z;
        float flat = MathF.Sqrt(dx * dx + dz * dz);
        float step = MathF.Max(0.01f, config.WalkSpeed * dt);
        if (flat <= step)
        {
            npc.WaypointIndex = (npc.WaypointIndex + 1) % npc.Waypoints.Count;
            Server.MoveNpc(npc.EntityId, new PluginLocation(target.X, target.Y, target.Z, live.Location.Yaw));
            return;
        }
        Server.MoveNpc(npc.EntityId, new PluginLocation(
            live.Location.X + dx / flat * step,
            live.Location.Y + Math.Clamp(dy, -step, step),
            live.Location.Z + dz / flat * step,
            MathF.Atan2(dx, dz)));
    }

    private void LookAtNearestPlayer(Npc npc, PluginNpc live)
    {
        PluginPlayer? nearest = null;
        float best = config.LookCloseRange * config.LookCloseRange;
        foreach (var player in Server.OnlinePlayers)
        {
            float distance = DistanceSquared(player, live.Location);
            if (distance > best) continue;
            best = distance;
            nearest = player;
        }
        if (nearest == null) return;
        Server.SetNpcLook(npc.EntityId,
            MathF.Atan2(nearest.X - live.Location.X, nearest.Z - live.Location.Z));
    }

    private void OnInteract(NpcInteractEventArgs e)
    {
        var npc = npcs.Values.FirstOrDefault(n => n.EntityId == e.NpcId);
        if (npc == null) return;
        e.Cancelled = true; // the click belongs to this NPC, not to combat
        if (npc.Text.Count == 0) return;
        if (lastInteract.TryGetValue(e.Player.Id, out var previous)
            && (DateTime.UtcNow - previous).TotalSeconds < config.InteractCooldownSeconds) return;
        lastInteract[e.Player.Id] = DateTime.UtcNow;

        string line = npc.Text[npc.TextIndex % npc.Text.Count];
        npc.TextIndex = (npc.TextIndex + 1) % npc.Text.Count;
        Server.SendMessage(e.Player.Id, $"[{npc.Name}] " + line
            .Replace("<player>", e.Player.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("<npc>", npc.Name, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Commands ----------------------------------------------------------

    private void Command(CommandContext command)
    {
        string sub = (command.Arguments.FirstOrDefault() ?? "help").ToLowerInvariant();
        if (sub is "help" or "?") { Help(command); return; }
        if (!Allowed(command.Player, sub))
        {
            command.Reply($"You do not have permission: citizens.npc.{sub}");
            return;
        }
        string[] rest = command.Arguments.Skip(1).ToArray();
        switch (sub)
        {
            case "create": Create(command, rest); break;
            case "list": List(command); break;
            case "select" or "sel": Select(command, rest); break;
            case "remove" or "delete": Remove(command, rest); break;
            case "rename": Rename(command, rest); break;
            case "type": Type(command, rest); break;
            case "equip": Equip(command, rest); break;
            case "face": Face(command); break;
            case "tp" or "teleport": TeleportToNpc(command); break;
            case "tphere": BringNpcHere(command); break;
            case "lookclose" or "look": LookClose(command, rest); break;
            case "text": Text(command, rest); break;
            case "path" or "waypoints": Path(command, rest); break;
            case "reload": LoadFiles(); Respawn(); command.Reply("Citizens configuration reloaded."); break;
            default: Help(command); break;
        }
    }

    private static void Help(CommandContext command)
    {
        command.Reply("/npc create <name> — place an NPC where you stand");
        command.Reply("/npc list | select <id|name> | remove [all] | rename <name>");
        command.Reply("/npc type <" + string.Join('|', TypeNames.Keys) + "> | equip <item id|none>");
        command.Reply("/npc face | tp | tphere | lookclose [on|off]");
        command.Reply("/npc text <add <line>|list|clear> — what it says when clicked");
        command.Reply("/npc path <add|list|clear|start|stop> — walk a waypoint route");
    }

    private void Create(CommandContext command, string[] arguments)
    {
        string name = string.Join(' ', arguments).Trim();
        if (name.Length == 0) { command.Reply("Usage: /npc create <name>"); return; }
        if (config.MaxNpcs > 0 && npcs.Count >= config.MaxNpcs)
        { command.Reply($"This server allows at most {config.MaxNpcs} NPCs."); return; }

        var npc = new Npc
        {
            Id = nextId++,
            Name = name,
            Type = PluginNpcType.Wanderer,
            X = command.Player.X,
            Y = command.Player.Y,
            Z = command.Player.Z,
            Owner = command.Player.Id,
        };
        if (!Spawn(npc)) { nextId--; command.Reply("The server refused to spawn that NPC."); return; }
        npcs[npc.Id] = npc;
        selections[command.Player.Id] = npc.Id;
        SaveNpcs();
        command.Reply($"Created NPC {npc.Id} '{npc.Name}' and selected it.");
    }

    private void List(CommandContext command)
    {
        if (npcs.Count == 0) { command.Reply("No NPCs exist yet. Use /npc create <name>."); return; }
        int selected = selections.TryGetValue(command.Player.Id, out int id) ? id : 0;
        command.Reply($"NPCs ({npcs.Count}):");
        foreach (var npc in npcs.Values.OrderBy(n => n.Id))
            command.Reply($"  {(npc.Id == selected ? "*" : " ")}{npc.Id} '{npc.Name}' {npc.Type} "
                + $"({npc.X:0.#}, {npc.Y:0.#}, {npc.Z:0.#})"
                + (npc.LookClose ? " lookclose" : "")
                + (npc.Waypoints.Count > 0 ? $" path:{npc.Waypoints.Count}{(npc.Walking ? " walking" : "")}" : "")
                + (npc.EntityId == 0 ? " [not spawned]" : ""));
    }

    private void Select(CommandContext command, string[] arguments)
    {
        string value = string.Join(' ', arguments).Trim();
        var npc = Resolve(value);
        if (npc == null) { command.Reply($"No NPC matches '{value}'."); return; }
        selections[command.Player.Id] = npc.Id;
        command.Reply($"Selected NPC {npc.Id} '{npc.Name}'.");
    }

    private void Remove(CommandContext command, string[] arguments)
    {
        if (arguments.FirstOrDefault()?.Equals("all", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!Server.HasPermission(command.Player.Id, AdminPermission))
            { command.Reply("Removing every NPC requires " + AdminPermission + "."); return; }
            int removed = npcs.Count;
            foreach (var npc in npcs.Values.Where(n => n.EntityId != 0)) Server.RemoveNpc(npc.EntityId);
            npcs.Clear();
            selections.Clear();
            SaveNpcs();
            command.Reply($"Removed {removed} NPC(s).");
            return;
        }
        if (!Selected(command, out var selected)) return;
        if (selected.EntityId != 0) Server.RemoveNpc(selected.EntityId);
        npcs.Remove(selected.Id);
        foreach (string player in selections.Where(s => s.Value == selected.Id).Select(s => s.Key).ToArray())
            selections.Remove(player);
        SaveNpcs();
        command.Reply($"Removed NPC {selected.Id} '{selected.Name}'.");
    }

    private void Rename(CommandContext command, string[] arguments)
    {
        if (!Selected(command, out var npc)) return;
        string name = string.Join(' ', arguments).Trim();
        if (name.Length == 0) { command.Reply("Usage: /npc rename <name>"); return; }
        string previous = npc.Name;
        npc.Name = name;
        if (npc.EntityId != 0) Server.SetNpcName(npc.EntityId, name);
        SaveNpcs();
        command.Reply($"Renamed '{previous}' to '{name}'.");
    }

    private void Type(CommandContext command, string[] arguments)
    {
        if (!Selected(command, out var npc)) return;
        string value = (arguments.FirstOrDefault() ?? "").ToLowerInvariant();
        if (!TypeNames.TryGetValue(value, out var type))
        { command.Reply("Usage: /npc type <" + string.Join('|', TypeNames.Keys) + ">"); return; }
        npc.Type = type;
        // The appearance is fixed when the entity spawns, so the NPC is replaced.
        Respawn(npc);
        SaveNpcs();
        command.Reply($"NPC {npc.Id} is now a {type}.");
    }

    private void Equip(CommandContext command, string[] arguments)
    {
        if (!Selected(command, out var npc)) return;
        string value = (arguments.FirstOrDefault() ?? "").ToLowerInvariant();
        int itemId;
        if (value is "none" or "clear" or "0") itemId = 0;
        else if (!int.TryParse(value, out itemId) || itemId < 0)
        { command.Reply("Usage: /npc equip <item id|none>"); return; }
        if (npc.EntityId != 0 && !Server.SetNpcHeldItem(npc.EntityId, itemId))
        { command.Reply($"Item {itemId} is not a valid item id."); return; }
        npc.HeldItemId = itemId;
        SaveNpcs();
        command.Reply(itemId == 0
            ? $"NPC {npc.Id} is now empty-handed."
            : $"NPC {npc.Id} now holds item {itemId}.");
    }

    private void Face(CommandContext command)
    {
        if (!Selected(command, out var npc)) return;
        var live = npc.EntityId == 0 ? null : Server.GetNpc(npc.EntityId);
        if (live == null) { command.Reply("That NPC is not spawned."); return; }
        float yaw = MathF.Atan2(command.Player.X - live.Location.X, command.Player.Z - live.Location.Z);
        Server.SetNpcLook(npc.EntityId, yaw);
        npc.Yaw = yaw;
        SaveNpcs();
        command.Reply($"NPC {npc.Id} now faces you.");
    }

    private void TeleportToNpc(CommandContext command)
    {
        if (!Selected(command, out var npc)) return;
        var live = npc.EntityId == 0 ? null : Server.GetNpc(npc.EntityId);
        var location = live?.Location ?? new PluginLocation(npc.X, npc.Y, npc.Z, npc.Yaw);
        if (!Server.TeleportPlayer(command.Player.Id, location))
        { command.Reply("You could not be teleported."); return; }
        command.Reply($"Teleported to NPC {npc.Id} '{npc.Name}'.");
    }

    private void BringNpcHere(CommandContext command)
    {
        if (!Selected(command, out var npc)) return;
        npc.X = command.Player.X;
        npc.Y = command.Player.Y;
        npc.Z = command.Player.Z;
        if (npc.EntityId != 0)
            Server.MoveNpc(npc.EntityId, new PluginLocation(npc.X, npc.Y, npc.Z, npc.Yaw));
        else Spawn(npc);
        SaveNpcs();
        command.Reply($"Moved NPC {npc.Id} to you.");
    }

    private void LookClose(CommandContext command, string[] arguments)
    {
        if (!Selected(command, out var npc)) return;
        string value = (arguments.FirstOrDefault() ?? "").ToLowerInvariant();
        npc.LookClose = value switch
        {
            "on" or "true" or "yes" => true,
            "off" or "false" or "no" => false,
            _ => !npc.LookClose,
        };
        SaveNpcs();
        command.Reply($"NPC {npc.Id} {(npc.LookClose ? "now turns toward nearby players" : "no longer turns toward players")}.");
    }

    private void Text(CommandContext command, string[] arguments)
    {
        if (!Selected(command, out var npc)) return;
        string action = (arguments.FirstOrDefault() ?? "list").ToLowerInvariant();
        switch (action)
        {
            case "add":
                string line = string.Join(' ', arguments.Skip(1)).Trim();
                if (line.Length == 0) { command.Reply("Usage: /npc text add <line>"); return; }
                npc.Text.Add(line);
                SaveNpcs();
                command.Reply($"Added line {npc.Text.Count} to NPC {npc.Id}.");
                break;
            case "clear":
                npc.Text.Clear();
                npc.TextIndex = 0;
                SaveNpcs();
                command.Reply($"Cleared the text of NPC {npc.Id}.");
                break;
            default:
                if (npc.Text.Count == 0) { command.Reply($"NPC {npc.Id} says nothing when clicked."); return; }
                command.Reply($"NPC {npc.Id} text ({npc.Text.Count} line(s), one per click):");
                for (int i = 0; i < npc.Text.Count; i++) command.Reply($"  {i + 1}. {npc.Text[i]}");
                break;
        }
    }

    private void Path(CommandContext command, string[] arguments)
    {
        if (!Selected(command, out var npc)) return;
        switch ((arguments.FirstOrDefault() ?? "list").ToLowerInvariant())
        {
            case "add":
                npc.Waypoints.Add(new Waypoint(command.Player.X, command.Player.Y, command.Player.Z));
                SaveNpcs();
                command.Reply($"Waypoint {npc.Waypoints.Count} added where you stand.");
                break;
            case "clear":
                npc.Waypoints.Clear();
                npc.WaypointIndex = 0;
                npc.Walking = false;
                SaveNpcs();
                command.Reply($"Cleared the route of NPC {npc.Id}.");
                break;
            case "start":
                if (npc.Waypoints.Count < 2)
                { command.Reply("Add at least two waypoints with /npc path add first."); return; }
                npc.Walking = true;
                SaveNpcs();
                command.Reply($"NPC {npc.Id} is walking its {npc.Waypoints.Count}-point route.");
                break;
            case "stop":
                npc.Walking = false;
                SaveNpcs();
                command.Reply($"NPC {npc.Id} stopped walking.");
                break;
            default:
                if (npc.Waypoints.Count == 0) { command.Reply($"NPC {npc.Id} has no route."); return; }
                command.Reply($"NPC {npc.Id} route ({(npc.Walking ? "walking" : "stopped")}):");
                for (int i = 0; i < npc.Waypoints.Count; i++)
                {
                    var point = npc.Waypoints[i];
                    command.Reply($"  {i + 1}. ({point.X:0.#}, {point.Y:0.#}, {point.Z:0.#})");
                }
                break;
        }
    }

    // ---- Helpers -----------------------------------------------------------

    private static readonly Dictionary<string, PluginNpcType> TypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wanderer"] = PluginNpcType.Wanderer,
        ["undead"] = PluginNpcType.Undead,
        ["spider"] = PluginNpcType.Spider,
        ["pig"] = PluginNpcType.Pig,
        ["sheep"] = PluginNpcType.Sheep,
        ["cow"] = PluginNpcType.Cow,
        ["cat"] = PluginNpcType.Cat,
        ["bird"] = PluginNpcType.Bird,
    };

    private bool Allowed(PluginPlayer player, string sub) =>
        Server.HasPermission(player.Id, AdminPermission)
        || Server.HasPermission(player.Id, "citizens.npc")
        || Server.HasPermission(player.Id, "citizens.npc." + sub);

    private bool Selected(CommandContext command, out Npc npc)
    {
        npc = null!;
        if (selections.TryGetValue(command.Player.Id, out int id) && npcs.TryGetValue(id, out var found))
        { npc = found; return true; }
        command.Reply("Select an NPC first: /npc select <id|name>.");
        return false;
    }

    private Npc? Resolve(string value) =>
        int.TryParse(value, out int id) && npcs.TryGetValue(id, out var byId) ? byId
            : npcs.Values.FirstOrDefault(n => n.Name.Equals(value, StringComparison.OrdinalIgnoreCase));

    private bool Spawn(Npc npc)
    {
        int entityId = Server.SpawnNpc(npc.Name, npc.Type, new PluginLocation(npc.X, npc.Y, npc.Z, npc.Yaw));
        if (entityId == 0) return false;
        npc.EntityId = entityId;
        npc.WaypointIndex = 0;
        if (npc.HeldItemId != 0) Server.SetNpcHeldItem(entityId, npc.HeldItemId);
        return true;
    }

    private void Respawn(Npc npc)
    {
        if (npc.EntityId != 0) Server.RemoveNpc(npc.EntityId);
        npc.EntityId = 0;
        Spawn(npc);
    }

    private void Respawn()
    {
        foreach (var npc in npcs.Values.OrderBy(n => n.Id)) Respawn(npc);
    }

    private static float DistanceSquared(PluginPlayer player, PluginLocation location)
    {
        float dx = player.X - location.X, dy = player.Y - location.Y, dz = player.Z - location.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    // ---- Persistence -------------------------------------------------------

    private void LoadFiles()
    {
        Directory.CreateDirectory(DataDirectory);
        config = File.Exists(ConfigPath)
            ? JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonOptions) ?? new Config()
            : new Config();
        config.LookCloseRange = Math.Clamp(config.LookCloseRange, 1f, 64f);
        config.WalkSpeed = Math.Clamp(config.WalkSpeed, 0.1f, 20f);
        config.UpdateIntervalSeconds = Math.Clamp(config.UpdateIntervalSeconds, 0.05, 2.0);
        config.InteractCooldownSeconds = Math.Clamp(config.InteractCooldownSeconds, 0, 60);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));

        npcs.Clear();
        if (!File.Exists(NpcsPath)) { nextId = 1; return; }
        var saved = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(NpcsPath), JsonOptions);
        foreach (var npc in saved?.Npcs ?? []) npcs[npc.Id] = npc;
        nextId = Math.Max(1, npcs.Count == 0 ? 1 : npcs.Keys.Max() + 1);
    }

    private void SaveNpcs() => File.WriteAllText(NpcsPath,
        JsonSerializer.Serialize(new SaveFile { Npcs = npcs.Values.OrderBy(n => n.Id).ToList() }, JsonOptions));

    private string ConfigPath => System.IO.Path.Combine(DataDirectory, "config.json");
    private string NpcsPath => System.IO.Path.Combine(DataDirectory, "npcs.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public sealed class Config
    {
        /// <summary>How close a player must be for a lookclose NPC to turn to them.</summary>
        public float LookCloseRange { get; set; } = 8f;
        /// <summary>Waypoint walking speed, in blocks per second.</summary>
        public float WalkSpeed { get; set; } = 2.2f;
        public double UpdateIntervalSeconds { get; set; } = 0.1;
        public double InteractCooldownSeconds { get; set; } = 0.75;
        /// <summary>Zero means "as many as the server allows".</summary>
        public int MaxNpcs { get; set; }
    }

    public sealed class Npc
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public PluginNpcType Type { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public int HeldItemId { get; set; }
        public bool LookClose { get; set; }
        public bool Walking { get; set; }
        /// <summary>The player who created it, for auditing; ownership is not enforced.</summary>
        public string Owner { get; set; } = "";
        public List<Waypoint> Waypoints { get; set; } = new();
        public List<string> Text { get; set; } = new();

        // Live state. The saved position is the authored one, so a restart puts a
        // walking NPC back at the start of its route rather than wherever it stopped.
        [JsonIgnore] public int EntityId { get; set; }
        [JsonIgnore] public int WaypointIndex { get; set; }
        [JsonIgnore] public int TextIndex { get; set; }
    }

    public sealed record Waypoint(float X, float Y, float Z);

    public sealed class SaveFile
    {
        public List<Npc> Npcs { get; set; } = new();
    }
}
