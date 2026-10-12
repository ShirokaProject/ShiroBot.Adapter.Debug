using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShiroBot.Adapter.Debug;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Adapter;
using ShiroBot.Model.QQ;
using Xunit;

public class MethodTests
{
    [Fact]
    public async Task EveryReflectedAdapterMethodExampleCanBeCalled()
    {
        var adapter = new DebugAdapter();
        await adapter.OnConfigLoadedAsync(new DebugConfig { ListenUrl = "http://127.0.0.1:0" }, default);
        try
        {
            await adapter.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(adapter.BoundUrl!) };
            var methods = await client.GetFromJsonAsync<JsonElement>("/api/methods");
            Assert.True(methods.GetArrayLength() > 50);
            foreach (var entry in methods.EnumerateArray().Where(m => m.GetProperty("callable").GetBoolean()
                         && m.GetProperty("service").GetString() != typeof(IAsyncDisposable).FullName))
            {
                var response = await client.PostAsJsonAsync("/api/call", entry.GetProperty("example"));
                Assert.True(response.IsSuccessStatusCode, entry.GetProperty("methodId").GetString() + ": " + await response.Content.ReadAsStringAsync());
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(result.GetProperty("simulated").GetBoolean());
            }
            var documentation = await client.GetStringAsync("/llm");
            foreach (var entry in methods.EnumerateArray()) Assert.Contains(entry.GetProperty("methodId").GetString()!, documentation);
            var wrong = await client.PostAsJsonAsync("/api/call", new { methodId = "System.Environment.Exit", arguments = new { } });
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }
        finally { await adapter.StopAsync(); }
    }

    [Fact]
    public async Task ExtensionsAndHttpCallsUseTheSameConfiguredMockAndStreamTarget()
    {
        var adapter = new DebugAdapter();
        await adapter.OnConfigLoadedAsync(new DebugConfig { ListenUrl = "http://127.0.0.1:0" }, default);
        try
        {
            await adapter.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(adapter.BoundUrl!) };
            var methods = (await client.GetFromJsonAsync<JsonElement>("/api/methods")).EnumerateArray().ToArray();
            var send = methods.Single(m => m.GetProperty("service").GetString() == typeof(IQOfficialMessageApi).FullName && m.GetProperty("method").GetString() == "SendTextAsync");
            var configured = await client.PostAsJsonAsync("/api/mock-results", new { methodId = send.GetProperty("methodId").GetString(), result = "fixture-message-id" });
            Assert.True(configured.IsSuccessStatusCode);
            var official = ((IBotAdapter)adapter).GetExtension<IQOfficialMessageApi>();
            Assert.NotNull(official);
            var catalog = new EventCatalog();
            var target = send.GetProperty("example").GetProperty("arguments").GetProperty("target").Deserialize<QOfficialMessageTarget>(catalog.Json)!;
            Assert.Equal("fixture-message-id", await official.SendTextAsync(target, "hello"));
            var http = await client.PostAsJsonAsync("/api/call", send.GetProperty("example"));
            Assert.Equal("fixture-message-id", (await http.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());
            Assert.NotNull(((IBotAdapter)adapter).GetExtension<IFileService>());
            Assert.NotNull(((IBotAdapter)adapter).GetExtension<IMessageInteractionService>());
            Assert.NotNull(((IBotAdapter)adapter).GetExtension<IMessageReactionService>());
            var begin = methods.Single(m => m.GetProperty("method").GetString() == "BeginStream");
            var begun = await client.PostAsJsonAsync("/api/call", begin.GetProperty("example"));
            var handle = (await begun.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("targetId").GetString();
            foreach (var name in new[] { "AppendAsync", "CompleteAsync", "DisposeAsync" })
            {
                var method = methods.Single(m => m.GetProperty("method").GetString() == name);
                var response = await client.PostAsJsonAsync("/api/call", new { methodId = method.GetProperty("methodId").GetString(), targetId = handle,
                    arguments = method.GetProperty("example").GetProperty("arguments") });
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            }
        }
        finally { await adapter.StopAsync(); }
        Assert.Null(((IBotAdapter)adapter).GetExtension<IQOfficialMessageApi>());
    }
}
