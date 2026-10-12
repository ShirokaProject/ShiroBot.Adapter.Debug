using ShiroBot.Adapter.Debug;
using ShiroBot.SDK.Models;
var adapter = new DebugAdapter();
await adapter.OnConfigLoadedAsync(new DebugConfig
{
    ListenUrl = Environment.GetEnvironmentVariable("DEBUG_LISTEN_URL") ?? "http://127.0.0.1:5088",
    ApiToken = Environment.GetEnvironmentVariable("DEBUG_API_TOKEN") ?? "",
    Platform = Environment.GetEnvironmentVariable("DEBUG_PLATFORM") ?? "qq-official",
    EnableScripts = Environment.GetEnvironmentVariable("DEBUG_ENABLE_SCRIPTS") == "1",
    ScriptDirectory = Environment.GetEnvironmentVariable("DEBUG_SCRIPT_DIRECTORY") ?? "debug-scripts"
}, default);
if (args.Contains("--echo")) adapter.Event.EventReceived += async value =>
{
    if (value is MessageEvent message) await adapter.Message.SendMessageAsync(message.Channel, [new TextSegment("pong: " + message.GetPlainText())]);
};
var quit = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
try { await adapter.StartAsync(); Console.WriteLine($"Debug console: {adapter.BoundUrl} (Ctrl+C to stop)"); await quit.Task; }
finally { await adapter.StopAsync(); }
