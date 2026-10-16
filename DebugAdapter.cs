using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
[assembly: ShiroBotApiCompatibility("0.9.2", "0.9.2")]
namespace ShiroBot.Adapter.Debug;
[BotAdapter("debug", Name = "DebugAdapter", Version = "0.1.1", Description = "Model event simulator, WebAPI, live console and script triggers", Author = "ShirokaProject", Protocol = "debug", IsSingleFile = true)]
public sealed class DebugAdapter : IBotAdapter, IConfigurableAdapter, IConfigurableComponent<DebugConfig>
{
    private WebApplication? _app;
    private DebugRuntime? _runtime;
    private MethodCatalog? _methods;
    private readonly DebugServices _services;
    private readonly Events _events;
    public DebugAdapter() { _services = new(() => _runtime ?? throw new InvalidOperationException("Adapter is stopped."), () => CurrentConfigValue); _events = new(); }
    public IConfigContext Config { get; set; } = null!;
    public IConsoleLogger Logger { get; set; } = null!;
    public DebugConfig CurrentConfigValue { get; private set; } = new();
    ConfigApplyMode IConfigurableAdapter.ApplyMode => ConfigApplyMode.RestartComponent;
    public string Platform => CurrentConfigValue.Platform;
    public IMessageService Message => _services;
    public IChannelService Channel => _services;
    public IUserService User => _services;
    public IEventService Event => _events;
    public string? BoundUrl { get; private set; }
    public TService? GetExtension<TService>() where TService : class => _methods?.Resolve(typeof(TService)) as TService;
    public Task OnConfigLoadedAsync(DebugConfig config, CancellationToken cancellationToken) { config.Validate(); CurrentConfigValue = config; return Task.CompletedTask; }
    public Task OnConfigChangedAsync(DebugConfig previous, DebugConfig config, CancellationToken cancellationToken) => OnConfigLoadedAsync(config, cancellationToken);
    public async Task StartAsync()
    {
        await StopAsync(); CurrentConfigValue.Validate();
        var runtime = new DebugRuntime(CurrentConfigValue, message => Logger?.Warning(message));
        _runtime = runtime;
        var methods = new MethodCatalog(runtime, _services, CurrentConfigValue);
        _methods = methods;
        runtime.EventReceived += async value => { _services.Remember(value); await _events.Publish(value); };
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(DebugAdapter).Assembly.FullName });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(CurrentConfigValue.ListenUrl);
            builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = 1048576; o.Limits.MaxConcurrentConnections = 64; });
            var app = builder.Build(); _app = app;
            app.Use(async (ctx, next) =>
            {
                // Prevent a remote website from using the local debug server as a script execution endpoint.
                if (ctx.Request.Headers.TryGetValue("Origin", out var origin) && origin.ToString() != $"{ctx.Request.Scheme}://{ctx.Request.Host}") { ctx.Response.StatusCode = 403; return; }
                if (ctx.Request.Path.StartsWithSegments("/api") && CurrentConfigValue.ApiToken.Length > 0 && ctx.Request.Headers.Authorization != "Bearer " + CurrentConfigValue.ApiToken) { ctx.Response.StatusCode = 401; return; }
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                try { await next(ctx); }
                catch (Exception ex) when (ex is ArgumentException or JsonException or NotSupportedException or InvalidOperationException or FormatException)
                {
                    if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }); }
                }
            });
            app.MapGet("/", async ctx =>
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                using var stream = typeof(DebugAdapter).Assembly.GetManifestResourceStream("ShiroBot.Adapter.Debug.Web.index.html")!;
                await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            });
            app.MapGet("/api/catalog", () => Results.Json(runtime.Catalog.Describe(Platform), runtime.Catalog.Json));
            app.MapGet("/llm", () => Results.Text(
                LlmDocumentation.Render(BoundUrl!, CurrentConfigValue, runtime.Catalog, methods,
                    ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)),
                "text/markdown; charset=utf-8"));
            app.MapGet("/api/methods", () => Results.Json(methods.Describe(), runtime.Catalog.Json));
            app.MapPost("/api/call", async Task<IResult> (HttpContext ctx) =>
                Results.Json(await methods.CallAsync(await Read<MethodCall>(ctx, runtime), ctx.RequestAborted), runtime.Catalog.Json));
            app.MapGet("/api/mock-results", () => Results.Json(methods.Results(), runtime.Catalog.Json));
            app.MapPost("/api/mock-results", async Task<IResult> (HttpContext ctx) =>
            { methods.SetResult(await Read<MockResult>(ctx, runtime)); return Results.Ok(); });
            app.MapDelete("/api/mock-results", (string methodId) => methods.DeleteResult(methodId) ? Results.Ok() : Results.NotFound());
            app.MapGet("/api/diagnostics", () =>
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                var gc = GC.GetGCMemoryInfo();
                var skia = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "SkiaSharp")?.GetType("SkiaSharp.SKGraphics");
                long? ReadSkiaCache(string method)
                {
                    try { return skia?.GetMethod(method, Type.EmptyTypes)?.Invoke(null, null) is { } value ? Convert.ToInt64(value) : null; }
                    catch { return null; }
                }
                return Results.Json(new { processId = process.Id, workingSetBytes = process.WorkingSet64,
                    sampledAt = DateTimeOffset.UtcNow, serverGC = System.Runtime.GCSettings.IsServerGC,
                    assemblyCount = AppDomain.CurrentDomain.GetAssemblies().Length,
                    threadCount = process.Threads.Count,
                    gcIndex = gc.Index, gcPauseTimePercentage = gc.PauseTimePercentage,
                    generations = gc.GenerationInfo.ToArray().Select((generation, index) => new
                    { index, generation.SizeBeforeBytes, generation.SizeAfterBytes,
                        generation.FragmentationBeforeBytes, generation.FragmentationAfterBytes }),
                    managedBytes = GC.GetTotalMemory(false), heapSizeBytes = gc.HeapSizeBytes, fragmentedBytes = gc.FragmentedBytes,
                    skiaFontCacheBytes = ReadSkiaCache("GetFontCacheUsed"),
                    skiaResourceCacheBytes = ReadSkiaCache("GetResourceCacheTotalBytesUsed"),
                    gcCommittedBytes = gc.TotalCommittedBytes,
                    totalAllocatedBytes = GC.GetTotalAllocatedBytes(), gen0Collections = GC.CollectionCount(0),
                    gen1Collections = GC.CollectionCount(1), gen2Collections = GC.CollectionCount(2),
                    retainedRecords = runtime.RecordCount, maxRecords = CurrentConfigValue.MaxRecords });
            });
            app.MapGet("/api/status", () => Results.Json(new { platform = Platform, selfId = CurrentConfigValue.SelfId, boundUrl = BoundUrl, enableScripts = CurrentConfigValue.EnableScripts, scriptRunner = CurrentConfigValue.ScriptRunner, scriptExample = OperatingSystem.IsWindows() ? "check_reply.ps1" : "check_reply.sh" }));
            app.MapPost("/api/events", async Task<IResult> (HttpContext ctx) =>
            {
                var input = await Read<EventInput>(ctx, runtime);
                var record = await runtime.InjectAsync(input);
                return Results.Json(record, runtime.Catalog.Json);
            });
            app.MapGet("/api/records", (long? after) => Results.Json(runtime.Records(after ?? 0), runtime.Catalog.Json));
            app.MapDelete("/api/records", () => Results.Ok(new { cleared = runtime.ClearRecords() }));
            app.MapGet("/api/rules", () => Results.Json(runtime.Rules(), runtime.Catalog.Json));
            app.MapPost("/api/rules", async Task<IResult> (HttpContext ctx) => { runtime.SetRule(await Read<TriggerRule>(ctx, runtime)); return Results.Ok(); });
            app.MapDelete("/api/rules/{id}", (string id) => runtime.DeleteRule(id) ? Results.Ok() : Results.NotFound());
            app.MapPost("/api/scenarios/run", async Task<IResult> (HttpContext ctx) => Results.Json(await runtime.RunScenarioAsync(await Read<Scenario>(ctx, runtime), ctx.RequestAborted), runtime.Catalog.Json));
            app.MapGet("/api/stream", async ctx =>
            {
                var subscription = runtime.Subscribe();
                ctx.Response.ContentType = "text/event-stream";
                try
                {
                    await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted); await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    await foreach (var record in subscription.Reader.ReadAllAsync(ctx.RequestAborted))
                    {
                        await ctx.Response.WriteAsync($"id: {record.Id}\ndata: {JsonSerializer.Serialize(record, runtime.Catalog.Json)}\n\n", ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                finally { runtime.Unsubscribe(subscription.Id); }
            });
            await app.StartAsync(); BoundUrl = app.Urls.FirstOrDefault(); Logger?.Info($"Debug adapter: {BoundUrl}");
        }
        catch { await StopAsync(); throw; }
    }
    private static async Task<T> Read<T>(HttpContext ctx, DebugRuntime runtime) =>
        await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, runtime.Catalog.Json, ctx.RequestAborted) ?? throw new ArgumentException("JSON body is required.");
    public async Task StopAsync()
    {
        var app = _app; var runtime = _runtime; _app = null; BoundUrl = null;
        try { if (runtime != null) await runtime.DisposeAsync(); }
        finally
        {
            _runtime = null; _methods?.Clear(); _methods = null; _services.Clear();
            if (app != null) { try { await app.StopAsync(TimeSpan.FromSeconds(5)); } finally { await app.DisposeAsync(); } }
        }
    }
    private sealed class Events : IEventService
    {
        public event Func<BotEvent, Task>? EventReceived;
        public async Task Publish(BotEvent value)
        {
            if (EventReceived is { } handlers) foreach (Func<BotEvent, Task> handler in handlers.GetInvocationList()) await handler(value);
        }
    }
}
