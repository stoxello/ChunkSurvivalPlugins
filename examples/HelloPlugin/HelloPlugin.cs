using BlockGame.PluginApi;

namespace HelloPlugin;

public sealed class HelloPlugin : GamePlugin
{
    public override string Name => "HelloPlugin";
    public override string Version => "1.1.0";

    public override void OnLoad() => Server.Log("Loaded.");

    public override void OnEnable()
    {
        Server.RegisterListener<PlayerEventArgs>(e =>
            Server.Broadcast($"Welcome {e.Player.Name} - sent by HelloPlugin."));
        Server.RegisterCommand(new PluginCommand("hello", "Greets the player", command =>
            command.Reply($"Hello, {command.Player.Name}! There are {Server.OnlinePlayers.Count} player(s) online."),
            usage: "/hello"));
        Server.RunTaskTimer(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60),
            () => Server.Log($"{Server.OnlinePlayers.Count} player(s) online."));
        Server.Log($"Enabled against plugin API {Server.ApiVersion}. Data: {DataDirectory}");
    }

    public override void OnDisable() => Server.Log("Disabled.");
}
