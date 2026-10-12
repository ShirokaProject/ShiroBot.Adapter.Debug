using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ShiroBot.Adapter.Debug;

internal static class LlmDocumentation
{
    public static string Render(string url, DebugConfig config, EventCatalog catalog, MethodCatalog methods, IEnumerable<Endpoint> endpoints)
    {
        using var stream = typeof(DebugAdapter).Assembly.GetManifestResourceStream("ShiroBot.Adapter.Debug.Web.llm.md")!;
        using var reader = new StreamReader(stream);
        var text = new StringBuilder(reader.ReadToEnd().Replace("{{BASE_URL}}", url.TrimEnd('/')));
        text.AppendLine("\n## 当前实例（不含 Token）\n");
        AppendJson(text, new { baseUrl = url, config.Platform, config.SelfId,
            authenticationRequired = config.ApiToken.Length > 0, config.EnableScripts,
            config.ScriptRunner, config.ScriptTimeoutSeconds, config.MaxRecords, config.MaxRecordBytes });
        text.AppendLine("\n## 当前已注册 HTTP 路由\n");
        foreach (var route in endpoints.OfType<RouteEndpoint>().OrderBy(e => e.RoutePattern.RawText))
            foreach (var method in route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
                text.AppendLine($"- `{method} {route.RoutePattern.RawText}`");
        text.AppendLine("\n## 完整反射目录\n");
        text.AppendLine("下列目录与 GET /api/catalog 一致，包含所有当前发现的事件、平台 Model、消息段、字段、枚举值和示例。events 可注入；payloads 用于 raw；segments 用于消息段。不要把目录中的 operations 当成可直接调用的 HTTP 路由。\n");
        AppendJson(text, catalog.Describe(config.Platform));
        text.AppendLine("\n## SDK 与平台扩展方法完整反射目录\n");
        AppendJson(text, methods.Describe());
        return text.ToString();
    }

    private static void AppendJson(StringBuilder text, object value)
    {
        text.AppendLine("```json");
        text.AppendLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        text.AppendLine("```");
    }
}
