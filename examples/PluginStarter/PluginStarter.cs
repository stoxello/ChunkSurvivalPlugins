using System.Text.Json;
using BlockGame.PluginApi;

namespace PluginStarter;

public sealed class PluginStarter : GamePlugin
{
    private Settings settings = new();
    private int welcomes;

    public override string Name => "PluginStarter";
    public override string Version => "1.0.0";

    public override void OnLoad()
    {
        Directory.CreateDirectory(DataDirectory);
        string path = Path.Combine(DataDirectory, "config.json");
        if (File.Exists(path))
            settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        else
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public override void OnEnable()
    {
        Server.RegisterCommand(new PluginCommand(
            "starter", "Shows the starter plugin status", command =>
                command.Reply($"{settings.Greeting}, {command.Player.Name}! Welcomes this session: {welcomes}."),
            usage: "/starter", permission: "pluginstarter.use", aliases: new[] { "startplugin" }));

        Server.RegisterListener<PlayerJoinEventArgs>(OnPlayerJoin);
        Server.RegisterListener<ChatEventArgs>(OnChat, EventPriority.High);
        Server.RunTaskTimer(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5),
            () => Server.Log($"{Server.OnlinePlayers.Count} player(s) online; {welcomes} welcome(s) sent."));
        Server.Log($"Enabled on API {Server.ApiVersion}. Configuration: {DataDirectory}");
    }

    public override void OnDisable() => Server.Log("Disabled.");

    private void OnPlayerJoin(PlayerJoinEventArgs e)
    {
        welcomes++;
        Server.SendMessage(e.Player.Id, $"{settings.Greeting}, {e.Player.Name}! Use /starter for plugin status.");
    }

    private void OnChat(ChatEventArgs e)
    {
        if (!settings.BlockedWords.Any(word =>
            e.Message.Contains(word, StringComparison.OrdinalIgnoreCase))) return;
        e.Cancelled = true;
        Server.SendMessage(e.Player.Id, "That message was blocked by PluginStarter.");
    }

    public sealed class Settings
    {
        public string Greeting { get; set; } = "Welcome";
        public List<string> BlockedWords { get; set; } = [];
    }
}
