using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ShiroBot.SDK.Models;
namespace ShiroBot.Adapter.Debug;

internal sealed record DebugRecord(long Id, DateTimeOffset Time, string Direction, string Type, JsonElement Data, string? CorrelationId, int Depth);
internal sealed record EventInput(string Type, JsonElement Data, string? CorrelationId = null);
internal sealed record TriggerRule(string Id, string Direction, string Type, string Action, string? Script = null, string? Url = null, EventInput? Event = null, bool Enabled = true, int MaxExecutions = 100);
internal sealed record Scenario(EventInput[] Events, Expectation[]? Expect = null, int TimeoutMilliseconds = 3000);
internal sealed record Expectation(string Type, string? Contains = null, string Direction = "outbound", int Count = 1);

internal sealed class DebugRuntime : IAsyncDisposable
{
    public readonly EventCatalog Catalog = new();
    private readonly DebugConfig _config;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly Queue<DebugRecord> _records = new();
    private readonly Dictionary<long, System.Threading.Channels.Channel<DebugRecord>> _subscribers = new();
    private readonly Dictionary<string, TriggerRule> _rules = new();
    private readonly Dictionary<string, int> _ruleExecutions = new();
    private readonly System.Threading.Channels.Channel<(DebugRecord Record, TriggerRule Rule)> _jobs = System.Threading.Channels.Channel.CreateBounded<(DebugRecord, TriggerRule)>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Task _worker;
    private long _id, _subscriberId;
    private bool _disposed;
    private readonly BoundedJson _snapshots;
    public event Func<BotEvent, Task>? EventReceived;
    private readonly AsyncLocal<(string? Id, int Depth)> _context = new();
    public DebugRuntime(DebugConfig config, Action<string> log)
    {
        _config = config; _log = log;
        _snapshots = new(Catalog.Json, config.MaxRecordBytes);
        _worker = RunTriggersAsync();
    }
    public long LastId { get { lock (_gate) return _id; } }
    public DebugRecord[] Records(long after = 0) { lock (_gate) return _records.Where(r => r.Id > after).ToArray(); }
    public int RecordCount { get { lock (_gate) return _records.Count; } }
    public int ClearRecords() { lock (_gate) { var count = _records.Count; _records.Clear(); return count; } }
    public TriggerRule[] Rules() { lock (_gate) return _rules.Values.ToArray(); }
    public void SetRule(TriggerRule rule)
    {
        if (rule.MaxExecutions is < 1 or > 10000) throw new ArgumentException("MaxExecutions must be between 1 and 10000.");
        if (string.IsNullOrWhiteSpace(rule.Id) || rule.Direction is not ("inbound" or "outbound") || rule.Action is not ("script" or "webhook" or "event")) throw new ArgumentException("Invalid trigger rule.");
        if (rule.Action == "script") ResolveScript(rule.Script);
        if (rule.Action == "webhook" && (!Uri.TryCreate(rule.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))) throw new ArgumentException("Invalid webhook URL.");
        if (rule.Action == "event") { if (rule.Event is null) throw new ArgumentException("Event action requires event."); Catalog.Parse(rule.Event.Type, rule.Event.Data, _config); }
        lock (_gate)
        {
            if (!_rules.ContainsKey(rule.Id) && _rules.Count >= 100) throw new ArgumentException("Maximum 100 rules.");
            _rules[rule.Id] = rule; _ruleExecutions[rule.Id] = 0;
        }
    }
    public bool DeleteRule(string id) { lock (_gate) { _ruleExecutions.Remove(id); return _rules.Remove(id); } }
    public DebugRecord Record(string direction, string type, object data, string? correlation = null, int? depth = null)
    {
        var json = _snapshots.Snapshot(data)
            ?? JsonSerializer.SerializeToElement(new { truncated = true, message = "Record exceeded MaxRecordBytes", originalType = type });
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var record = new DebugRecord(++_id, DateTimeOffset.UtcNow, direction, type, json, correlation ?? _context.Value.Id, depth ?? _context.Value.Depth);
            _records.Enqueue(record);
            while (_records.Count > _config.MaxRecords) _records.Dequeue();
            foreach (var pair in _subscribers.ToArray())
                if (!pair.Value.Writer.TryWrite(record)) { pair.Value.Writer.TryComplete(new IOException("Slow SSE consumer; reconnect and query records.")); _subscribers.Remove(pair.Key); }
            if (record.Depth < 4)
                foreach (var rule in _rules.Values.Where(r => r.Enabled && r.Direction == direction && (r.Type == "*" || r.Type == type)).ToArray())
                {
                    var executions = _ruleExecutions.GetValueOrDefault(rule.Id);
                    if (executions >= rule.MaxExecutions)
                    {
                        _rules[rule.Id] = rule with { Enabled = false };
                        _log($"Debug rule {rule.Id} disabled after {executions} actions. Update the rule to rearm.");
                        continue;
                    }
                    if (_jobs.Writer.TryWrite((record, rule))) _ruleExecutions[rule.Id] = executions + 1;
                    else _log("Debug trigger queue is full; action skipped.");
                }
            return record;
        }
    }
    internal bool FitsSnapshot(object value) => _snapshots.Fits(value);
    public (string? Id, int? Depth) SourceContext(ShiroBot.SDK.Models.Channel channel, string? messageId)
    {
        if (messageId is null) return (null, null);
        lock (_gate)
        {
            foreach (var record in _records.Reverse())
                if (record.Direction == "inbound" && record.Data.TryGetProperty("messageId", out var id) && id.GetString() == messageId
                    && record.Data.TryGetProperty("channel", out var source) && source.GetProperty("id").GetString() == channel.Id)
                    return (record.CorrelationId, record.Depth);
        }
        return (null, null);
    }
    public async Task<DebugRecord> InjectAsync(EventInput input, int depth = 0)
    {
        var value = Catalog.Parse(input.Type, input.Data, _config);
        var previous = _context.Value;
        _context.Value = (input.CorrelationId ?? Guid.NewGuid().ToString("N"), depth);
        try
        {
            var record = Record("inbound", input.Type, value);
            if (EventReceived is { } listeners)
                foreach (Func<BotEvent, Task> listener in listeners.GetInvocationList()) await listener(value).ConfigureAwait(false);
            return record;
        }
        finally { _context.Value = previous; }
    }
    public (long Id, ChannelReader<DebugRecord> Reader) Subscribe()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_subscribers.Count >= 32) throw new ArgumentException("Maximum 32 live clients.");
            var queue = System.Threading.Channels.Channel.CreateBounded<DebugRecord>(128);
            var id = ++_subscriberId; _subscribers[id] = queue;
            return (id, queue.Reader);
        }
    }
    public void Unsubscribe(long id) { lock (_gate) if (_subscribers.Remove(id, out var queue)) queue.Writer.TryComplete(); }
    public async Task<object> RunScenarioAsync(Scenario scenario, CancellationToken cancellation)
    {
        if (scenario.Events == null || scenario.Events.Length is < 1 or > 100 || scenario.TimeoutMilliseconds is < 1 or > 30000 || scenario.Expect?.Any(e => e.Count < 1) == true) throw new ArgumentException("Invalid scenario limits.");
        var correlation = Guid.NewGuid().ToString("N");
        var start = LastId;
        foreach (var input in scenario.Events) { cancellation.ThrowIfCancellationRequested(); await InjectAsync(input with { CorrelationId = correlation }); }
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(scenario.TimeoutMilliseconds);
        var expected = scenario.Expect ?? [];
        object[] results;
        bool passed;
        do
        {
            var records = Records(start).Where(r => r.CorrelationId == correlation || r.Direction == "outbound" && r.CorrelationId == null).ToArray();
            var checks = expected.Select(e => (Expected: e, Actual: records.Count(r => r.Type == e.Type && r.Direction == e.Direction && (e.Contains == null || r.Data.GetRawText().Contains(e.Contains, StringComparison.Ordinal))))).ToArray();
            passed = checks.All(c => c.Actual >= c.Expected.Count);
            results = checks.Select(c => (object)new { expected = c.Expected, actual = c.Actual, passed = c.Actual >= c.Expected.Count }).ToArray();
            if (passed || DateTimeOffset.UtcNow >= deadline) break;
            await Task.Delay(25, cancellation);
        } while (true);
        return new { passed, correlationId = correlation, checks = results, records = Records(start) };
    }
    private string ResolveScript(string? script)
    {
        if (!_config.EnableScripts) throw new ArgumentException("EnableScripts is disabled.");
        if (string.IsNullOrWhiteSpace(script) || Path.IsPathRooted(script) || script.Split('/', '\\').Any(s => s is ".." or ".")) throw new ArgumentException("Script must be a relative file inside ScriptDirectory.");
        var root = Path.GetFullPath(_config.ScriptDirectory);
        var path = Path.GetFullPath(Path.Combine(root, script));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) throw new ArgumentException("Script does not exist inside ScriptDirectory.");
        for (var current = new FileInfo(path) as FileSystemInfo; current is not null; current = current is FileInfo fi ? fi.Directory : ((DirectoryInfo)current).Parent)
        {
            if (current.LinkTarget != null) throw new ArgumentException("Script path must not contain symbolic links.");
            if (current.FullName == root) break;
        }
        return path;
    }
    private async Task RunTriggersAsync()
    {
        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync(_lifetime.Token))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(_config.ScriptTimeoutSeconds));
                var previous = _context.Value;
                _context.Value = (job.Record.CorrelationId, job.Record.Depth + 1);
                try
                {
                    if (job.Rule.Action == "event") await InjectAsync(job.Rule.Event! with { CorrelationId = job.Record.CorrelationId }, job.Record.Depth + 1).WaitAsync(timeout.Token);
                    else if (job.Rule.Action == "webhook")
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, job.Rule.Url) { Content = new StringContent(JsonSerializer.Serialize(job.Record, Catalog.Json), Encoding.UTF8, "application/json") };
                        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        Record("trigger", "webhookResult", new { ruleId = job.Rule.Id, status = (int)response.StatusCode });
                    }
                    else await RunScriptAsync(job.Rule, job.Record, timeout.Token);
                }
                catch (Exception) when (_disposed || _lifetime.IsCancellationRequested) { break; }
                catch (Exception ex) { Record("trigger", "actionError", new { ruleId = job.Rule.Id, error = ex.Message }); }
                finally { _context.Value = previous; }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    private async Task RunScriptAsync(TriggerRule rule, DebugRecord record, CancellationToken cancellation)
    {
        var path = ResolveScript(rule.Script);
        using var process = new Process { StartInfo = new(_config.ScriptRunner) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetFullPath(_config.ScriptDirectory) } };
        if (Path.GetFileNameWithoutExtension(_config.ScriptRunner).Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(_config.ScriptRunner).Equals("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-File");
        }
        process.StartInfo.ArgumentList.Add(path);
        if (!process.Start()) throw new IOException("Script did not start.");
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(record, Catalog.Json).AsMemory(), cancellation);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellation);
            var output = await stdout.WaitAsync(cancellation); var error = await stderr.WaitAsync(cancellation);
            Record("trigger", "scriptResult", new { ruleId = rule.Id, exitCode = process.ExitCode, stdout = output, stderr = error });
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            // Drain redirected pipes without retaining unbounded output.
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var result = new StringBuilder(); var buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (result.Length < 8192) result.Append(buffer, 0, Math.Min(count, 8192 - result.Length));
        return result.ToString();
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var queue in _subscribers.Values) queue.Writer.TryComplete();
            _subscribers.Clear(); _rules.Clear(); _ruleExecutions.Clear(); _records.Clear(); EventReceived = null;
        }
        await _lifetime.CancelAsync(); _jobs.Writer.TryComplete();
        try { await _worker; } catch (OperationCanceledException) { }
        while (_jobs.Reader.TryRead(out _)) { }
        _http.Dispose(); _lifetime.Dispose();
    }
}
