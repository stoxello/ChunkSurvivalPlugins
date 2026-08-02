using BlockGame.PluginApi;

namespace BlockGame.PluginApi.Minigames;

/// <summary>Lifecycle and helpers shared by dedicated minigame-server plugins.</summary>
public abstract class MinigamePlugin : GamePlugin
{
    protected abstract string MinigameId { get; }
    protected virtual IReadOnlyCollection<string> MinigameAliases => Array.Empty<string>();
    protected bool IsActiveBackend { get; private set; }
    protected IReadOnlyCollection<PluginPlayer> Players => Server.OnlinePlayers;

    public sealed override void OnLoad()
    {
        IsActiveBackend = Server.ServerRole.Equals("minigame", StringComparison.OrdinalIgnoreCase)
            && (Server.Minigame.Equals(MinigameId, StringComparison.OrdinalIgnoreCase)
                || MinigameAliases.Contains(Server.Minigame, StringComparer.OrdinalIgnoreCase));
        OnMinigameLoad();
    }

    public sealed override void OnEnable()
    {
        if (!IsActiveBackend)
        {
            Server.Log($"Idle because this server is role={Server.ServerRole}, minigame={Server.Minigame}.");
            return;
        }
        OnMinigameEnable();
    }

    public sealed override void OnDisable()
    {
        if (IsActiveBackend) OnMinigameDisable();
    }

    protected virtual void OnMinigameLoad() { }
    protected abstract void OnMinigameEnable();
    protected virtual void OnMinigameDisable() { }
    protected void Announce(string message) => Server.Broadcast($"[{Name}] {message}");
    protected bool IsAdmin(PluginPlayer player, string permission) =>
        Server.HasPermission(player.Id, permission);
}
