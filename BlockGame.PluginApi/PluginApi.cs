namespace BlockGame.PluginApi;

/// <summary>The version of the public plugin contract implemented by this assembly.</summary>
public static class PluginApiVersion
{
    public const string Current = "2.1";
}

/// <summary>Entry point implemented by every server plugin.</summary>
public interface IGamePlugin
{
    string Name { get; }
    string Version { get; }
    void OnEnable(IPluginContext context);
    void OnDisable();
}

/// <summary>Infrastructure hook used by the server before plugin lifecycle callbacks.</summary>
public interface IPluginBootstrap
{
    void Attach(IPluginContext context);
}

/// <summary>
/// Optional Spigot-style base class. The server attaches the context before
/// invoking <see cref="OnLoad"/> and <see cref="OnEnable"/>.
/// </summary>
public abstract class GamePlugin : IGamePlugin, IPluginBootstrap
{
    private IPluginContext? context;

    public abstract string Name { get; }
    public abstract string Version { get; }
    public IPluginContext Server => context
        ?? throw new InvalidOperationException("The plugin is not attached to a server.");
    public string DataDirectory => Server.DataDirectory;

    /// <summary>Called after construction and before any plugin is enabled.</summary>
    public virtual void OnLoad() { }
    public virtual void OnEnable() { }
    public virtual void OnDisable() { }

    void IGamePlugin.OnEnable(IPluginContext value)
    {
        context = value;
        OnEnable();
    }

    void IPluginBootstrap.Attach(IPluginContext value) => context = value;
}

/// <summary>Services and events exposed by the dedicated server.</summary>
public interface IPluginContext
{
    string ApiVersion { get; }
    string DataDirectory { get; }
    string ServerRole { get; }
    string Minigame { get; }
    IReadOnlyCollection<PluginPlayer> OnlinePlayers { get; }

    event EventHandler<PlayerEventArgs>? PlayerJoined;
    event EventHandler<PlayerEventArgs>? PlayerLeft;
    event EventHandler<ChatEventArgs>? PlayerChat;
    event EventHandler<TickEventArgs>? ServerTick;

    void RegisterCommand(PluginCommand command);
    IDisposable RegisterListener<TEvent>(
        Action<TEvent> listener,
        EventPriority priority = EventPriority.Normal,
        bool ignoreCancelled = false) where TEvent : PluginEvent;
    IPluginTask RunTask(Action action);
    IPluginTask RunTaskLater(TimeSpan delay, Action action);
    IPluginTask RunTaskTimer(TimeSpan delay, TimeSpan period, Action action);
    void Broadcast(string message);
    bool SendMessage(string playerId, string message);
    bool HasPermission(string playerId, string permission);
    BlockRegion? GetSelection(string playerId);
    PluginChunkPosition? GetHomeChunk(string playerId);
    IReadOnlyCollection<PluginChunkPosition> GetClaimedChunks(string playerId);
    bool SetHomeChunk(string playerId, PluginChunkPosition chunk);
    bool IsChunkClaimed(PluginChunkPosition chunk);
    bool ClaimChunk(string playerId, PluginChunkPosition chunk);
    bool ReleaseChunk(string playerId, PluginChunkPosition chunk);
    bool EnsureChunk(PluginChunkPosition chunk);
    void SaveWorld();
    PluginPlayer? GetPlayer(string playerId);
    bool TeleportPlayer(string playerId, PluginLocation location);
    bool SetGameMode(string playerId, PluginGameMode gameMode);
    bool SetDisguise(string playerId, ushort blockId);
    int GetHealth(string playerId);
    bool SetHealth(string playerId, int health);
    int GetItemCount(string playerId, int itemId);
    bool SetItemCount(string playerId, int itemId, int count);
    bool ClearInventory(string playerId);
    bool ClearArmor(string playerId);
    int GetContainerItemCount(BlockPosition position, int itemId);
    bool SetContainerItemCount(BlockPosition position, int itemId, int count);
    bool ClearContainer(BlockPosition position);
    ushort GetBlock(int x, int y, int z);
    bool SetBlock(int x, int y, int z, ushort blockId);
    RegionSnapshot CaptureRegion(BlockRegion region);
    void RestoreRegion(RegionSnapshot snapshot);

    // ---- API 2.1: plugin-owned NPCs ----------------------------------------
    // An NPC is a standing entity the server broadcasts like any other creature
    // but never simulates: it has no AI, takes no damage, and never despawns.
    // The plugin decides where it stands, which way it faces, and when it moves.
    // Ids are server entity ids, valid until the NPC is removed or the server
    // restarts; a plugin that needs NPCs to survive a restart stores its own
    // records and respawns them from OnEnable.
    IReadOnlyCollection<PluginNpc> Npcs { get; }
    PluginNpc? GetNpc(int npcId);
    int SpawnNpc(string name, PluginNpcType type, PluginLocation location);
    bool RemoveNpc(int npcId);
    bool MoveNpc(int npcId, PluginLocation location);
    bool SetNpcLook(int npcId, float yaw);
    bool SetNpcName(int npcId, string name);
    bool SetNpcHeldItem(int npcId, int itemId);

    void Log(string message);
}

/// <summary>Appearance of a plugin-owned NPC. Wire identifiers are append-only.</summary>
public enum PluginNpcType
{
    /// <summary>An upright, cloaked humanoid — the default conversational NPC.</summary>
    Wanderer = 0,
    Undead = 1,
    Spider = 2,
    Pig = 3,
    Sheep = 4,
    Cow = 5,
    Cat = 6,
    Bird = 7,
}

/// <summary>An immutable snapshot of one plugin-owned NPC.</summary>
public sealed record PluginNpc(
    int Id, string Name, PluginNpcType Type, PluginLocation Location, int HeldItemId);

/// <summary>
/// Raised when a player clicks a plugin-owned NPC. NPCs are invulnerable, so the
/// click is an interaction rather than an attack; cancelling suppresses the
/// server's own click handling for that swing.
/// </summary>
public sealed class NpcInteractEventArgs : CancellablePluginEvent
{
    public NpcInteractEventArgs(PluginPlayer player, int npcId, string npcName, int heldItemId)
    { Player = player; NpcId = npcId; NpcName = npcName ?? ""; HeldItemId = heldItemId; }
    public PluginPlayer Player { get; }
    public int NpcId { get; }
    public string NpcName { get; }
    public int HeldItemId { get; }
}

public enum EventPriority { Lowest, Low, Normal, High, Highest, Monitor }

/// <summary>Base type for events dispatched through the typed event bus.</summary>
public abstract class PluginEvent : EventArgs { }

public abstract class CancellablePluginEvent : PluginEvent
{
    public bool Cancelled { get; set; }
}

/// <summary>A scheduled callback owned and automatically cancelled by a plugin.</summary>
public interface IPluginTask : IDisposable
{
    bool IsCancelled { get; }
    void Cancel();
}

public enum PluginGameMode { Survival, Creative, Adventure, Spectator }

/// <summary>Stable block IDs commonly used by plugins. IDs are append-only.</summary>
public static class PluginBlocks
{
    public const ushort Air = 0;
    public const ushort Grass = 1;
    public const ushort Dirt = 2;
    public const ushort Stone = 3;
    public const ushort Water = 5;
    public const ushort Wood = 6;
    public const ushort Leaves = 7;
    public const ushort Lava = 13;
    public const ushort FineWood = 16;
    public const ushort Glass = 17;
    public const ushort Chest = 22;
    public const ushort Bedrock = 43;
    public const ushort Cobblestone = 45;
    public const ushort StoneBricks = 48;
    public const ushort GoldBlock = 57;
    public const ushort Bricks = 59;
    public const ushort Bookshelf = 61;
    public const ushort WoolWhite = 66;
    public const ushort WoolYellow = 70;
    public const ushort WoolBlue = 77;
    public const ushort WoolGreen = 79;
    public const ushort WoolRed = 80;
    public const ushort Bed = 89;
}

/// <summary>Stable item IDs commonly used by plugins. IDs are append-only.</summary>
public static class PluginItems
{
    public const int Dirt = 2;
    public const int Stone = 3;
    public const int Wood = 5;
    public const int Water = 9;
    public const int Lava = 11;
    public const int IronIngot = 15;
    public const int StoneSword = 22;
    public const int IronSword = 26;
    public const int IronHelmet = 32;
    public const int IronChestplate = 33;
    public const int Bow = 38;
    public const int Arrow = 39;
    public const int Bread = 43;
    public const int Apple = 44;
    public const int DiamondSword = 66;
    public const int Sprout = 68;
    public const int WoolWhite = 83;
    public const int WoolYellow = 87;
    public const int WoolBlue = 94;
    public const int WoolGreen = 96;
    public const int WoolRed = 97;
}

public readonly record struct PluginLocation(float X, float Y, float Z, float Yaw = 0, float Pitch = 0);

public readonly record struct BlockPosition(int X, int Y, int Z);

public readonly record struct PluginChunkPosition(int X, int Z);

public readonly record struct BlockRegion(BlockPosition Min, BlockPosition Max)
{
    public BlockRegion Normalize() => new(
        new BlockPosition(Math.Min(Min.X, Max.X), Math.Min(Min.Y, Max.Y), Math.Min(Min.Z, Max.Z)),
        new BlockPosition(Math.Max(Min.X, Max.X), Math.Max(Min.Y, Max.Y), Math.Max(Min.Z, Max.Z)));
    public long Volume
    {
        get
        {
            var n = Normalize();
            return checked((long)(n.Max.X - n.Min.X + 1)
                * (n.Max.Y - n.Min.Y + 1) * (n.Max.Z - n.Min.Z + 1));
        }
    }
}

/// <summary>An immutable block snapshot in X, then Y, then Z iteration order.</summary>
public sealed class RegionSnapshot
{
    public RegionSnapshot(BlockRegion region, ushort[] blocks)
    {
        Region = region.Normalize();
        Blocks = Array.AsReadOnly(blocks?.ToArray() ?? throw new ArgumentNullException(nameof(blocks)));
        if (Region.Volume != Blocks.Count)
            throw new ArgumentException("Block count does not match region volume.", nameof(blocks));
    }
    public BlockRegion Region { get; }
    public IReadOnlyList<ushort> Blocks { get; }
    public ushort[] CopyBlocks() => Blocks.ToArray();
}

/// <summary>Portable little-endian storage for 16-bit plugin block snapshots.</summary>
public static class PluginBlockStorage
{
    public static byte[] Encode(IReadOnlyList<ushort> blocks)
    {
        if (blocks == null) throw new ArgumentNullException(nameof(blocks));
        var bytes = new byte[checked(blocks.Count * 2)];
        for (int i = 0; i < blocks.Count; i++)
        {
            bytes[i * 2] = (byte)blocks[i];
            bytes[i * 2 + 1] = (byte)(blocks[i] >> 8);
        }
        return bytes;
    }

    public static ushort[] Decode(byte[] bytes, int expectedBlockCount)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        if (expectedBlockCount < 0) throw new ArgumentOutOfRangeException(nameof(expectedBlockCount));
        var blocks = new ushort[expectedBlockCount];
        if (bytes.Length == expectedBlockCount) // legacy one-byte snapshots
        {
            for (int i = 0; i < blocks.Length; i++) blocks[i] = bytes[i];
            return blocks;
        }
        if (bytes.Length != checked(expectedBlockCount * 2))
            throw new ArgumentException("Snapshot byte count does not match its region.", nameof(bytes));
        for (int i = 0; i < blocks.Length; i++)
            blocks[i] = (ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
        return blocks;
    }
}

public sealed record PluginPlayer(string Id, string Name, int EntityId, float X, float Y, float Z);

public class PlayerEventArgs : PluginEvent
{
    public PlayerEventArgs(PluginPlayer player) => Player = player;
    public PluginPlayer Player { get; }
}

public sealed class PlayerJoinEventArgs : PlayerEventArgs
{
    public PlayerJoinEventArgs(PluginPlayer player) : base(player) { }
}

public sealed class PlayerQuitEventArgs : PlayerEventArgs
{
    public PlayerQuitEventArgs(PluginPlayer player) : base(player) { }
}

public sealed class ChatEventArgs : CancellablePluginEvent
{
    public ChatEventArgs(PluginPlayer player, string message) { Player = player; Message = message; }
    public PluginPlayer Player { get; }
    public string Message { get; set; }
    public bool Cancel { get => Cancelled; set => Cancelled = value; }
}

public sealed class TickEventArgs : PluginEvent
{
    public TickEventArgs(float elapsedSeconds) => ElapsedSeconds = elapsedSeconds;
    public float ElapsedSeconds { get; }
}

public sealed class BlockEditEventArgs : CancellablePluginEvent
{
    public BlockEditEventArgs(PluginPlayer player, BlockPosition position, ushort previousBlock, ushort newBlock)
    { Player = player; Position = position; PreviousBlock = previousBlock; NewBlock = newBlock; }
    public PluginPlayer Player { get; }
    public BlockPosition Position { get; }
    public ushort PreviousBlock { get; }
    public ushort NewBlock { get; set; }
    public bool BypassPermissions { get; set; }
    public string? DenialMessage { get; set; }
}

public sealed class PlayerDamageEventArgs : CancellablePluginEvent
{
    public PlayerDamageEventArgs(PluginPlayer player, int damage, string cause)
    { Player = player; Damage = damage; Cause = cause ?? ""; }
    public PluginPlayer Player { get; }
    public int Damage { get; set; }
    public string Cause { get; }
}

public sealed class PlayerAttackEventArgs : CancellablePluginEvent
{
    public PlayerAttackEventArgs(PluginPlayer attacker, PluginPlayer target, int heldItemId)
    { Attacker = attacker; Target = target; HeldItemId = heldItemId; }
    public PluginPlayer Attacker { get; }
    public PluginPlayer Target { get; }
    public int HeldItemId { get; }
}

public sealed class PlayerDeathEventArgs : CancellablePluginEvent
{
    public PlayerDeathEventArgs(PluginPlayer player, string cause)
    { Player = player; Cause = cause ?? ""; }
    public PluginPlayer Player { get; }
    public string Cause { get; }
}

public sealed class CommandContext
{
    private readonly Action<string> reply;
    public CommandContext(PluginPlayer player, IReadOnlyList<string> arguments, string raw, Action<string> reply)
    { Player = player; Arguments = arguments; Raw = raw; this.reply = reply; }
    public PluginPlayer Player { get; }
    public IReadOnlyList<string> Arguments { get; }
    public string Raw { get; }
    public void Reply(string message) => reply(message);
}

public sealed class PluginCommand
{
    public PluginCommand(
        string name,
        string description,
        Action<CommandContext> execute,
        string? usage = null,
        string? permission = null,
        IReadOnlyCollection<string>? aliases = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Command name is required.", nameof(name));
        Name = name.Trim().TrimStart('/').ToLowerInvariant();
        Description = description ?? "";
        Execute = execute ?? throw new ArgumentNullException(nameof(execute));
        Usage = usage ?? "/" + Name;
        Permission = permission;
        Aliases = aliases ?? Array.Empty<string>();
    }
    public string Name { get; }
    public string Description { get; }
    public string Usage { get; }
    public string? Permission { get; }
    public IReadOnlyCollection<string> Aliases { get; }
    public Action<CommandContext> Execute { get; }
}
