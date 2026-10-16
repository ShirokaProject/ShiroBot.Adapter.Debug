using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShiroBot.Adapter.Debug;
using ShiroBot.SDK.Models;
using ShiroBot.Model.QQ;
using Xunit;

public sealed class IntegrationTests
{
    private static EventInput Message(string text = "ping") => new(typeof(MessageEvent).FullName!, JsonSerializer.SerializeToElement(new
    {
        messageId = Guid.NewGuid().ToString("N"), channel = new { id = "g", type = "Group" }, sender = new { id = "u" },
        segments = new object[] { new Dictionary<string, object> { ["$type"] = typeof(TextSegment).FullName!, ["text"] = text } }
    }));
    [Fact]
    public async Task HttpInjectionInvokesPluginAndRecordsItsReplyAndScenario()
    {
        var adapter = new DebugAdapter();
        await adapter.OnConfigLoadedAsync(new DebugConfig { ListenUrl = "http://127.0.0.1:0" }, default);
        adapter.Event.EventReceived += async value =>
        {
            if (value is MessageEvent message) await adapter.Message.SendMessageAsync(message.Channel, [new TextSegment("pong")]);
        };
        try
        {
            await adapter.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(adapter.BoundUrl!) };
            Assert.Contains("调试工作台", await client.GetStringAsync("/"));
            var catalog = await client.GetFromJsonAsync<JsonElement>("/api/catalog");
            var documentationResponse = await client.GetAsync("/llm");
            Assert.Equal("text/markdown", documentationResponse.Content.Headers.ContentType!.MediaType);
            var documentation = await documentationResponse.Content.ReadAsStringAsync();
            Assert.Contains(adapter.BoundUrl!, documentation);
            Assert.DoesNotContain("{{BASE_URL}}", documentation);
            foreach (var entry in catalog.GetProperty("events").EnumerateArray())
                Assert.Contains(entry.GetProperty("type").GetString()!, documentation);
            foreach (var route in new[] { "/api/catalog", "/api/status", "/api/diagnostics", "/api/events", "/api/records", "/api/stream", "/api/rules/{id}", "/api/scenarios/run" })
                Assert.Contains(route, documentation);
            Assert.Contains(catalog.GetProperty("events").EnumerateArray(), x => x.GetProperty("name").GetString() == "MessageEvent");
            var response = await client.PostAsJsonAsync("/api/events", Message());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var records = await client.GetFromJsonAsync<JsonElement>("/api/records");
            Assert.Equal(2, records.GetArrayLength());
            Assert.Equal("sendMessage", records[1].GetProperty("type").GetString());
            var diagnostics = await client.GetFromJsonAsync<JsonElement>("/api/diagnostics");
            Assert.True(diagnostics.GetProperty("managedBytes").GetInt64() > 0);
            Assert.Equal(records.GetArrayLength(), diagnostics.GetProperty("retainedRecords").GetInt32());
            var scenario = await client.PostAsJsonAsync("/api/scenarios/run", new Scenario([Message()], [new("sendMessage", "pong")]));
            Assert.True(scenario.IsSuccessStatusCode, await scenario.Content.ReadAsStringAsync());
            var result = await scenario.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(result.GetProperty("passed").GetBoolean());
            var lastRecordId = (await client.GetFromJsonAsync<JsonElement>("/api/records")).EnumerateArray().Max(r => r.GetProperty("id").GetInt64());
            var clearedResponse = await client.DeleteAsync("/api/records");
            Assert.True(clearedResponse.IsSuccessStatusCode);
            var cleared = await clearedResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(4, cleared.GetProperty("cleared").GetInt32());
            Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/records")).GetArrayLength());
            Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/diagnostics")).GetProperty("retainedRecords").GetInt32());
            await client.PostAsJsonAsync("/api/events", Message());
            var newRecords = await client.GetFromJsonAsync<JsonElement>("/api/records");
            Assert.Equal(2, newRecords.GetArrayLength());
            Assert.True(newRecords[0].GetProperty("id").GetInt64() > lastRecordId);
            var bad = await client.PostAsJsonAsync("/api/events", new EventInput("System.IO.File", JsonSerializer.SerializeToElement(new { })));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            var origin = new HttpRequestMessage(HttpMethod.Post, "/api/events") { Content = JsonContent.Create(Message()) };
            origin.Headers.Add("Origin", "https://foreign.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(origin)).StatusCode);
        }
        finally { await adapter.StopAsync(); }
    }
    [Fact]
    public async Task TypedRawIsRealModelAndPlatformIsNormalized()
    {
        var config = new DebugConfig();
        await using var runtime = new DebugRuntime(config, _ => { });
        BotEvent? seen = null;
        runtime.EventReceived += value => { seen = value; return Task.CompletedTask; };
        await runtime.InjectAsync(new(typeof(PlatformEvent).FullName!, JsonSerializer.SerializeToElement(new
        {
            platform = "other", kind = "group_nudge", raw = new { type = typeof(QGroupNudge).FullName, data = new { time = DateTimeOffset.UtcNow, groupId = "g", senderId = "u", receiverId = "bot" } }
        })));
        Assert.Equal(config.Platform, seen!.Platform);
        Assert.IsType<QGroupNudge>(seen.Raw);
    }
    [Fact]
    public async Task RulesBoundRecursionRecordsAndSubscribers()
    {
        await using var runtime = new DebugRuntime(new DebugConfig { MaxRecords = 5 }, _ => { });
        runtime.SetRule(new("loop", "inbound", "*", "event", Event: Message()));
        await runtime.InjectAsync(Message());
        for (var i = 0; i < 100 && runtime.LastId < 5; i++) await Task.Delay(10);
        Assert.Equal(5, runtime.LastId);
        for (var i = 0; i < 50; i++) runtime.Record("outbound", "test", new { i });
        Assert.Equal(5, runtime.Records().Length);
        var connections = Enumerable.Range(0, 32).Select(_ => runtime.Subscribe()).ToArray();
        Assert.Throws<ArgumentException>(() => runtime.Subscribe());
        foreach (var connection in connections) runtime.Unsubscribe(connection.Id);
    }
    [Fact]
    public async Task SseDeliversLiveRecordAndTokenProtectsApi()
    {
        var adapter = new DebugAdapter();
        await adapter.OnConfigLoadedAsync(new DebugConfig { ListenUrl = "http://127.0.0.1:0", ApiToken = "test-token" }, default);
        try
        {
            await adapter.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(adapter.BoundUrl!) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
            var documentation = await client.GetStringAsync("/llm");
            Assert.DoesNotContain("test-token", documentation);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "test-token");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var stream = await client.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(timeout.Token));
            await reader.ReadLineAsync(timeout.Token); await reader.ReadLineAsync(timeout.Token);
            await client.PostAsJsonAsync("/api/events", Message(), timeout.Token);
            Assert.StartsWith("id: ", await reader.ReadLineAsync(timeout.Token));
            Assert.StartsWith("data: ", await reader.ReadLineAsync(timeout.Token));
        }
        finally { await adapter.StopAsync(); }
    }
    [Fact]
    public async Task ScriptsConsumeJsonAndOutputIsBoundedAndTimeoutIsReported()
    {
        var root = Path.Combine(Path.GetTempPath(), "debug-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "reply.sh"), "cat >/dev/null\nprintf 'success'\n");
        await File.WriteAllTextAsync(Path.Combine(root, "timeout.sh"), "sleep 30\n");
        try
        {
            await using var runtime = new DebugRuntime(new DebugConfig { EnableScripts = true, ScriptDirectory = root, ScriptRunner = "/bin/sh", ScriptTimeoutSeconds = 1 }, _ => { });
            Assert.Throws<ArgumentException>(() => runtime.SetRule(new("bad", "inbound", "*", "script", Script: "../reply.sh")));
            runtime.SetRule(new("reply", "inbound", "*", "script", Script: "reply.sh"));
            await runtime.InjectAsync(Message());
            for (var i = 0; i < 100 && runtime.Records().All(r => r.Type != "scriptResult"); i++) await Task.Delay(20);
            Assert.Contains(runtime.Records(), r => r.Type == "scriptResult" && r.Data.GetProperty("stdout").GetString() == "success");
            runtime.DeleteRule("reply");
            runtime.SetRule(new("timeout", "inbound", "*", "script", Script: "timeout.sh"));
            await runtime.InjectAsync(Message());
            for (var i = 0; i < 200 && runtime.Records().All(r => r.Type != "actionError"); i++) await Task.Delay(20);
            Assert.Contains(runtime.Records(), r => r.Type == "actionError");
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task RuleExecutionCapStopsUncorrelatedFeedback()
    {
        await using var runtime = new DebugRuntime(new DebugConfig(), _ => { });
        runtime.SetRule(new("capped", "outbound", "test", "event", Event: Message(), MaxExecutions: 2));
        for (var i = 0; i < 5; i++) runtime.Record("outbound", "test", new { i });
        for (var i = 0; i < 100 && runtime.Records().Count(r => r.Direction == "inbound") < 2; i++) await Task.Delay(10);
        Assert.Equal(2, runtime.Records().Count(r => r.Direction == "inbound"));
        Assert.False(runtime.Rules().Single().Enabled);
    }
    [Fact]
    public void EveryGeneratedEventExampleCanBeParsed()
    {
        var catalog = new EventCatalog();
        var description = JsonSerializer.SerializeToElement(catalog.Describe("qq-official"), catalog.Json);
        foreach (var model in description.GetProperty("events").EnumerateArray())
            Assert.NotNull(catalog.Parse(model.GetProperty("type").GetString()!, model.GetProperty("example"), new DebugConfig()));
    }
    [Fact]
    public async Task RecordSizeAndSlowConsumersAreBounded()
    {
        await using var runtime = new DebugRuntime(new DebugConfig { MaxRecords = 3, MaxRecordBytes = 1024 }, _ => { });
        var subscription = runtime.Subscribe();
        runtime.Record("outbound", "big", new { text = new string('x', 5000) });
        Assert.True(runtime.Records()[0].Data.GetProperty("truncated").GetBoolean());
        for (var i = 0; i < 200; i++) runtime.Record("outbound", "test", new { i });
        Assert.Equal(3, runtime.Records().Length);
        var queued = 0;
        while (subscription.Reader.TryRead(out _)) queued++;
        Assert.Equal(128, queued);
        await Assert.ThrowsAsync<IOException>(async () => await subscription.Reader.Completion);
    }
    [Fact]
    public async Task WebhookGetsEventJson()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            using var network = connection.GetStream();
            using var reader = new StreamReader(network, leaveOpen: true);
            var length = 0;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(timeout.Token)))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Split(':')[1]);
            var chars = new char[length]; var offset = 0;
            while (offset < length) offset += await reader.ReadAsync(chars.AsMemory(offset), timeout.Token);
            await network.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
            return new string(chars);
        });
        await using var runtime = new DebugRuntime(new DebugConfig(), _ => { });
        runtime.SetRule(new("hook", "inbound", "*", "webhook", Url: $"http://127.0.0.1:{port}/events"));
        await runtime.InjectAsync(Message());
        Assert.Contains("inbound", await received);
    }
    [Fact]
    public void RejectsPublicListenerWithoutToken() => Assert.Throws<ArgumentException>(() => new DebugConfig { ListenUrl = "http://0.0.0.0:5088" }.Validate());
}
