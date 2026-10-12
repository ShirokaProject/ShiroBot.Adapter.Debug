using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ShiroBot.SDK.Models;
using ShiroBot.Model.QQ;
namespace ShiroBot.Adapter.Debug;

internal sealed class EventCatalog
{
    public JsonSerializerOptions Json { get; }
    private readonly Dictionary<string, Type> _types;
    public EventCatalog()
    {
        // Explicit references ensure the host's shared models are loaded before discovery.
        var assemblies = new[] { typeof(BotEvent).Assembly, typeof(QEventPayload).Assembly,
            Assembly.Load("ShiroBot.Model.Discord"), Assembly.Load("ShiroBot.Model.Telegram") }
            .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("ShiroBot.Model.") == true)).Distinct();
        _types = assemblies.SelectMany(SafeTypes).Where(t => t.IsPublic && !t.IsAbstract && !t.ContainsGenericParameters)
            .GroupBy(t => t.FullName!).ToDictionary(g => g.Key, g => g.First());
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            // Computed SDK properties (Message.Reference in particular) are not wire fields.
            foreach (var property in info.Properties.ToArray())
                if (property.AttributeProvider is PropertyInfo pi && pi.SetMethod is null && pi.DeclaringType?.Namespace == "ShiroBot.SDK.Models") info.Properties.Remove(property);
            if (!info.Type.IsAbstract) return;
            var children = _types.Values.Where(t => info.Type.IsAssignableFrom(t)).ToArray();
            if (children.Length == 0) return;
            info.PolymorphismOptions = new JsonPolymorphismOptions { TypeDiscriminatorPropertyName = "$type" };
            foreach (var child in children) info.PolymorphismOptions.DerivedTypes.Add(new(child, child.FullName!));
        });
        Json = new(JsonSerializerDefaults.Web) { TypeInfoResolver = resolver, MaxDepth = 32 };
        Json.Converters.Add(new JsonStringEnumConverter());
        Json.Converters.Add(new TypedRawConverter(_types));
    }
    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>(); }
    }
    public object Describe(string platform) => new
    {
        events = _types.Values.Where(t => typeof(BotEvent).IsAssignableFrom(t)).OrderBy(t => t.FullName).Select(t => DescribeType(t, platform)),
        payloads = _types.Values.Where(t => !typeof(BotEvent).IsAssignableFrom(t) && t.Namespace?.StartsWith("ShiroBot.Model.") == true).OrderBy(t => t.FullName).Select(t => DescribeType(t, platform)),
        segments = _types.Values.Where(t => typeof(MessageSegment).IsAssignableFrom(t)).Select(t => DescribeType(t, platform)),
        kinds = typeof(QEventKinds).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => new { name = f.Name, value = f.GetRawConstantValue() }),
        operations = new[] { "sendMessage", "deleteMessage", "setChannelName", "kickMember", "muteMember", "leaveChannel", "acceptFriendRequest", "rejectFriendRequest" },
        rawFormat = new { type = "ShiroBot.Model.QQ.QGroupNudgeEvent", data = new { } }
    };
    private object DescribeType(Type t, string platform) => new
    {
        type = t.FullName, name = t.Name,
        fields = t.GetProperties().Where(p => p.SetMethod != null).Select(p => new { name = JsonNamingPolicy.CamelCase.ConvertName(p.Name), type = p.PropertyType.Name,
            required = p.CustomAttributes.Any(a => a.AttributeType.Name == "RequiredMemberAttribute"), values = p.PropertyType.IsEnum ? Enum.GetNames(p.PropertyType) : null }),
        example = Example(t, platform, 0)
    };
    private JsonNode? Example(Type type, string platform, int depth)
    {
        if (depth > 5) return null;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return JsonValue.Create("example");
        if (type == typeof(DateTimeOffset) || type == typeof(DateTime)) return JsonValue.Create(DateTimeOffset.UtcNow);
        if (type == typeof(TimeSpan)) return JsonValue.Create("00:00:01");
        if (type.IsEnum) return JsonValue.Create(Enum.GetNames(type).FirstOrDefault());
        if (type == typeof(bool)) return JsonValue.Create(false);
        if (type.IsPrimitive || type == typeof(decimal)) return JsonValue.Create(1);
        if (type == typeof(object) || type == typeof(JsonElement)) return null;
        if (type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
        {
            var item = type.GetGenericArguments()[0];
            return new JsonArray(Example(item, platform, depth + 1));
        }
        if (type.IsArray) return new JsonArray(Example(type.GetElementType()!, platform, depth + 1));
        if (type.IsAbstract)
        {
            var child = type == typeof(MessageSegment) ? typeof(TextSegment) : _types.Values.FirstOrDefault(t => type.IsAssignableFrom(t));
            if (child is null) return null;
            var obj = new JsonObject { ["$type"] = child.FullName };
            foreach (var pair in (JsonObject)Example(child, platform, depth + 1)!) obj[pair.Key] = pair.Value?.DeepClone();
            return obj;
        }
        var result = new JsonObject();
        foreach (var p in type.GetProperties().Where(p => p.SetMethod != null))
        {
            var key = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            result[key] = p.Name switch
            {
                "Platform" => JsonValue.Create(platform), "SelfId" => JsonValue.Create("debug-bot"),
                "MessageId" => JsonValue.Create("message-" + Guid.NewGuid().ToString("N")[..8]),
                "InstanceId" or "Raw" => null,
                _ => Example(p.PropertyType, platform, depth + 1)
            };
        }
        return result;
    }
    public BotEvent Parse(string type, JsonElement data, DebugConfig config)
    {
        if (!_types.TryGetValue(type, out var model) || !typeof(BotEvent).IsAssignableFrom(model)) throw new ArgumentException("Unknown event type.");
        var node = JsonNode.Parse(data.GetRawText()) as JsonObject ?? throw new ArgumentException("Event data must be an object.");
        node["platform"] = config.Platform; node["selfId"] ??= config.SelfId; node.Remove("instanceId");
        var value = (BotEvent?)node.Deserialize(model, Json) ?? throw new ArgumentException("Empty event.");
        Validate(value, 0);
        if (value is MessageEvent message && message.Timestamp == default) value = message with { Timestamp = DateTimeOffset.UtcNow };
        return value;
    }
    private static void Validate(object? value, int depth)
    {
        if (value is null || depth > 16 || value is string || value is JsonElement) return;
        if (value is System.Collections.IEnumerable list) { foreach (var item in list) Validate(item, depth + 1); return; }
        foreach (var p in value.GetType().GetProperties().Where(p => p.SetMethod != null))
        {
            var child = p.GetValue(value);
            if (p.CustomAttributes.Any(a => a.AttributeType.Name == "RequiredMemberAttribute") && (child is null || child is string s && string.IsNullOrWhiteSpace(s)))
                throw new ArgumentException($"{value.GetType().Name}.{p.Name} is required.");
            Validate(child, depth + 1);
        }
    }
    private sealed class TypedRawConverter(Dictionary<string, Type> types) : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var name) && root.TryGetProperty("data", out var data))
            {
                if (!types.TryGetValue(name.GetString() ?? "", out var model) || model.Namespace?.StartsWith("ShiroBot.Model.") != true) throw new JsonException("Unknown Raw model type.");
                return data.Deserialize(model, options);
            }
            return root.Clone();
        }
        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (value.GetType() == typeof(object)) { writer.WriteStartObject(); writer.WriteEndObject(); }
            else if (value is JsonElement json) json.WriteTo(writer);
            else JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }
    }
}
