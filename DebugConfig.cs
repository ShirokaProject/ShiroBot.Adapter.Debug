using ShiroBot.SDK.Config;
namespace ShiroBot.Adapter.Debug;

[ConfigModel]
public sealed class DebugConfig
{
    [ConfigField("调试服务监听地址，默认仅本机；端口 0 可自动选择。", Label = "监听地址", Group = "server", GroupLabel = "服务")]
    public string ListenUrl { get; set; } = "http://127.0.0.1:5088";
    [ConfigField("模拟平台标识，必须与上报事件一致，例如 qq-official。", Label = "模拟平台", Group = "server")]
    public string Platform { get; set; } = "qq-official";
    [ConfigField("模拟机器人账号。", Label = "机器人 ID", Group = "server")]
    public string SelfId { get; set; } = "debug-bot";
    [ConfigField("API Bearer Token；本机可留空，对外监听必须设置。", Label = "API Token", Type = "password", Group = "server")]
    public string ApiToken { get; set; } = "";
    [ConfigField("事件记录和模拟消息的数量上限。", Label = "记录上限", Min = 1, Default = 1000, Group = "limits", GroupLabel = "限制")]
    public int MaxRecords { get; set; } = 1000;
    [ConfigField("每条记录最大 JSON 字节数；超出时仅保留摘要。", Label = "记录大小上限", Min = 1024, Default = 65536, Group = "limits")]
    public int MaxRecordBytes { get; set; } = 65536;
    [ConfigField("是否允许运行本地脚本。", Label = "启用脚本", Default = false, Group = "scripts", GroupLabel = "脚本")]
    public bool EnableScripts { get; set; }
    [ConfigField("允许执行的脚本所在目录。", Label = "脚本目录", Group = "scripts")]
    public string ScriptDirectory { get; set; } = "debug-scripts";
    [ConfigField("单个脚本或 Webhook 超时秒数。", Label = "执行超时", Min = 1, Default = 10, Group = "scripts")]
    public int ScriptTimeoutSeconds { get; set; } = 10;
    [ConfigField("脚本执行器，例如 python3、node 或 /bin/sh；路径作为独立参数传入。", Label = "脚本执行器", Group = "scripts")]
    public string ScriptRunner { get; set; } = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
    public void Validate()
    {
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.AbsolutePath != "/")
            throw new ArgumentException("ListenUrl must be an HTTP origin.");
        if (!uri.IsLoopback && string.IsNullOrWhiteSpace(ApiToken)) throw new ArgumentException("Non-loopback listener requires ApiToken.");
        if (string.IsNullOrWhiteSpace(Platform) || string.IsNullOrWhiteSpace(SelfId)) throw new ArgumentException("Platform and SelfId are required.");
        if (MaxRecords is < 1 or > 100000 || MaxRecordBytes is < 1024 or > 1048576 || ScriptTimeoutSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(MaxRecords));
        if (string.IsNullOrWhiteSpace(ScriptRunner)) throw new ArgumentException("ScriptRunner is required.");
    }
}
