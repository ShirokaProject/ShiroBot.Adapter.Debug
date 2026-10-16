using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShiroBot.SDK.Adapter;

namespace ShiroBot.Adapter.Debug;

internal sealed record MethodCall(string MethodId, JsonElement Arguments, string? TargetId = null);
internal sealed record MockResult(string MethodId, JsonElement Result);

internal sealed class MethodCatalog(DebugRuntime runtime, DebugServices services, DebugConfig config)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonElement> _results = new();
    private readonly Dictionary<Type, object> _proxies = new();
    private readonly Dictionary<string, object> _targets = new();
    private readonly Dictionary<string, MethodInfo> _methods = runtime.Catalog.Interfaces
        .SelectMany(t => t.GetMethods().Concat(t.GetInterfaces().SelectMany(i => i.GetMethods())))
        .Distinct().Where(m => !m.IsSpecialName).ToDictionary(Id);
    internal static string Id(MethodInfo method) => $"{method.DeclaringType!.FullName}.{method.Name}({string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType)))})";
    private static string TypeName(Type type) => type.IsGenericType
        ? $"{type.GetGenericTypeDefinition().FullName![..type.GetGenericTypeDefinition().FullName!.IndexOf('`')]}<{string.Join(",", type.GetGenericArguments().Select(TypeName))}>"
        : type.FullName ?? type.Name;
    private static Type ResultType(Type type) => type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        ? type.GetGenericArguments()[0] : type == typeof(Task) || type == typeof(ValueTask) ? typeof(void) : type;
    private static bool AdapterContract(Type type) => type.Namespace == "ShiroBot.SDK.Adapter" && type != typeof(IEventService)
        || type.Namespace?.StartsWith("ShiroBot.Model.") == true;
    private static bool Supported(MethodInfo method) => (AdapterContract(method.DeclaringType!) || method.DeclaringType == typeof(IAsyncDisposable)) && !method.ContainsGenericParameters
        && !method.GetParameters().Any(p => p.ParameterType.IsByRef || typeof(Delegate).IsAssignableFrom(p.ParameterType));
    public object Describe() => _methods.Values.OrderBy(Id).Select(m => new
    {
        methodId = Id(m), service = m.DeclaringType!.FullName, method = m.Name,
        callable = Supported(m), mode = services.GetType().IsAssignableTo(m.DeclaringType) ? "simulation" : AdapterContract(m.DeclaringType) ? "mock" : "hostContract",
        reason = Supported(m) ? null : "Host/plugin lifecycle, generic, delegate or by-ref contract; no adapter call target.",
        returnType = TypeName(m.ReturnType), resultType = TypeName(ResultType(m.ReturnType)),
        parameters = m.GetParameters().Select(p => new { name = p.Name, type = TypeName(p.ParameterType),
            optional = p.HasDefaultValue || p.ParameterType == typeof(CancellationToken),
            injected = p.ParameterType == typeof(CancellationToken),
            transport = typeof(Stream).IsAssignableFrom(p.ParameterType) ? "{base64: string}" : "json" }),
        example = new { methodId = Id(m), arguments = ExampleArguments(m) }
    }).ToArray();
    private JsonObject ExampleArguments(MethodInfo method)
    {
        var obj = new JsonObject();
        foreach (var p in method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)))
            obj[p.Name!] = typeof(Stream).IsAssignableFrom(p.ParameterType) ? new JsonObject { ["base64"] = "" }
                : p.HasDefaultValue ? JsonSerializer.SerializeToNode(p.DefaultValue, runtime.Catalog.Json)
                : runtime.Catalog.Example(p.ParameterType, config.Platform);
        return obj;
    }
    public object? Resolve(Type type)
    {
        if (type.IsInstanceOfType(services)) return services;
        if (!type.IsInterface || !AdapterContract(type) || type.ContainsGenericParameters) return null;
        lock (_gate)
        {
            if (!_proxies.TryGetValue(type, out var proxy))
                _proxies[type] = proxy = CreateProxy(type);
            return proxy;
        }
    }
    private object CreateProxy(Type type)
    {
        var proxy = DispatchProxy.Create(type, typeof(DebugMethodProxy));
        ((DebugMethodProxy)proxy).InvokeMethod = MockInvoke;
        return proxy;
    }
    public object Results() { lock (_gate) return _results.Select(p => new MockResult(p.Key, p.Value)).ToArray(); }
    public void SetResult(MockResult result)
    {
        if (!_methods.TryGetValue(result.MethodId, out var method) || !Supported(method)) throw new ArgumentException("Unknown or non-callable methodId.");
        if (method.DeclaringType!.IsInstanceOfType(services)) throw new ArgumentException("Built-in simulation uses its message snapshots; configure mock-mode methods only.");
        var type = ResultType(method.ReturnType);
        if (type == typeof(void) || type.IsInterface || typeof(Stream).IsAssignableFrom(type)) throw new ArgumentException("This return type cannot be configured as JSON.");
        _ = result.Result.Deserialize(type, runtime.Catalog.Json);
        if (System.Text.Encoding.UTF8.GetByteCount(result.Result.GetRawText()) > config.MaxRecordBytes) throw new ArgumentException("Mock result exceeds MaxRecordBytes.");
        lock (_gate) _results[result.MethodId] = result.Result.Clone();
    }
    public bool DeleteResult(string id) { lock (_gate) return _results.Remove(id); }
    private object? MockInvoke(MethodInfo method, object?[] args)
    {
        foreach (var arg in args.OfType<CancellationToken>()) arg.ThrowIfCancellationRequested();
        var input = new Dictionary<string, object?>();
        foreach (var (p, i) in method.GetParameters().Select((p, i) => (p, i)))
            if (p.ParameterType != typeof(CancellationToken)) input[p.Name!] = args[i] is Stream stream ? new { stream = true, readable = stream.CanRead } : args[i];
        runtime.Record("outbound", "sdkCall", new { methodId = Id(method), arguments = input, simulated = true });
        var type = ResultType(method.ReturnType);
        object? value;
        lock (_gate) value = _results.TryGetValue(Id(method), out var json) ? json.Deserialize(type, runtime.Catalog.Json) : Default(type);
        if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
        if (method.ReturnType == typeof(ValueTask)) return ValueTask.CompletedTask;
        if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type).Invoke(null, [value]);
        if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            return Activator.CreateInstance(method.ReturnType, [value]);
        return value;
    }
    private object? Default(Type type)
    {
        if (type == typeof(void)) return null;
        if (type == typeof(string)) return "debug-" + Guid.NewGuid().ToString("N");
        if (type == typeof(bool)) return false; // Capability probes require explicit fixture opt-in.
        if (type.IsInterface && AdapterContract(type)) return CreateProxy(type);
        if (typeof(Stream).IsAssignableFrom(type)) return new MemoryStream();
        if (type.IsInterface && type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>)) return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(type.GetGenericArguments()));
            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) return Array.CreateInstance(type.GetGenericArguments()[0], 0);
        }
        if (type.IsValueType && type.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true)
            return Activator.CreateInstance(type, type.GetGenericArguments().Select(Default).ToArray());
        var example = runtime.Catalog.Example(type, config.Platform);
        return example?.Deserialize(type, runtime.Catalog.Json);
    }
    public async Task<object> CallAsync(MethodCall call, CancellationToken cancellation)
    {
        if (!_methods.TryGetValue(call.MethodId, out var method) || !Supported(method)) throw new ArgumentException("Unknown or non-callable methodId.");
        var target = Resolve(method.DeclaringType!);
        if (call.TargetId is not null)
        {
            lock (_gate) target = _targets.GetValueOrDefault(call.TargetId);
            if (target is null || !method.DeclaringType!.IsInstanceOfType(target)) throw new ArgumentException("Unknown, expired or incompatible targetId.");
        }
        if (target is null) throw new ArgumentException("This method requires a targetId from a returned service.");
        if (call.Arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("arguments must be an object.");
        var parameters = method.GetParameters();
        if (call.Arguments.EnumerateObject().Any(p => !parameters.Any(x => x.Name == p.Name && x.ParameterType != typeof(CancellationToken)))) throw new ArgumentException("Unknown argument.");
        var ownedStreams = new List<Stream>();
        try
        {
            var args = parameters.Select(p =>
            {
                if (p.ParameterType == typeof(CancellationToken)) return (object)cancellation;
                if (!call.Arguments.TryGetProperty(p.Name!, out var json))
                    return p.HasDefaultValue ? p.DefaultValue : throw new ArgumentException("Missing argument: " + p.Name);
                if (typeof(Stream).IsAssignableFrom(p.ParameterType))
                {
                    if (!json.TryGetProperty("base64", out var data)) throw new ArgumentException("Stream argument requires base64.");
                    var stream = new MemoryStream(Convert.FromBase64String(data.GetString() ?? ""), writable: false);
                    ownedStreams.Add(stream); return stream;
                }
                var value = json.Deserialize(p.ParameterType, runtime.Catalog.Json);
                if (value is null && new NullabilityInfoContext().Create(p).ReadState == NullabilityState.NotNull) throw new ArgumentException("Argument cannot be null: " + p.Name);
                return value;
            }).ToArray();
            object? value;
            try { value = method.Invoke(target, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
            if (value is Task task) { await task.ConfigureAwait(false); value = ResultType(method.ReturnType) == typeof(void) ? null : task.GetType().GetProperty("Result")!.GetValue(task); }
            else if (value is ValueTask valueTask) { await valueTask; value = null; }
            else if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            { var taskValue = (Task)method.ReturnType.GetMethod("AsTask")!.Invoke(value, null)!; await taskValue; value = taskValue.GetType().GetProperty("Result")!.GetValue(taskValue); }
            if (call.TargetId is not null && method.DeclaringType == typeof(IAsyncDisposable))
                lock (_gate) _targets.Remove(call.TargetId);
            if (value is not null && ResultType(method.ReturnType).IsInterface && AdapterContract(ResultType(method.ReturnType)))
            {
                var id = Guid.NewGuid().ToString("N");
                lock (_gate) { if (_targets.Count >= 64) _targets.Remove(_targets.Keys.First()); _targets[id] = value; }
                value = new { targetId = id, service = ResultType(method.ReturnType).FullName };
            }
            return new { methodId = call.MethodId, simulated = true, result = value };
        }
        finally { foreach (var stream in ownedStreams) stream.Dispose(); }
    }
    public void Clear() { lock (_gate) { _results.Clear(); _proxies.Clear(); _targets.Clear(); } }
}

// DispatchProxy must be public, non-sealed and have a public parameterless constructor.
public class DebugMethodProxy : DispatchProxy
{
    internal Func<MethodInfo, object?[], object?> InvokeMethod { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => InvokeMethod(method!, args ?? []);
}
