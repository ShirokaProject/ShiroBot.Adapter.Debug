using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShiroBot.Adapter.Debug;

/// <summary>Limits diagnostic snapshots before encoding oversized string values.</summary>
internal sealed class BoundedJson
{
    private readonly JsonSerializerOptions _options;
    private readonly int _limit;
    public BoundedJson(JsonSerializerOptions options, int limit)
    {
        _limit = limit;
        _options = new(options);
        _options.Converters.Insert(0, new LimitedStringConverter(limit));
    }
    public JsonElement? Snapshot(object value)
    {
        using var buffer = new LimitedBuffer(_limit);
        try
        {
            JsonSerializer.Serialize(buffer, value, value.GetType(), _options);
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
            return document.RootElement.Clone();
        }
        catch (SnapshotTooLargeException) { return null; }
    }
    public bool Fits(object value)
    {
        using var buffer = new LimitedBuffer(_limit);
        try { JsonSerializer.Serialize(buffer, value, value.GetType(), _options); return true; }
        catch (SnapshotTooLargeException) { return false; }
    }
    private sealed class SnapshotTooLargeException : Exception;
    private sealed class LimitedBuffer(int limit) : MemoryStream(Math.Min(limit, 256))
    {
        public override void Write(byte[] buffer, int offset, int count)
        { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { Check(buffer.Length); base.Write(buffer); }
        private void Check(int count) { if (Length + count > limit) throw new SnapshotTooLargeException(); }
    }
    private sealed class LimitedStringConverter(int limit) : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetString();
        private void Check(string value)
        {
            if (value.Length > limit || Encoding.UTF8.GetByteCount(value) > limit) throw new SnapshotTooLargeException();
        }
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        { Check(value); writer.WriteStringValue(value); }
        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetString()!;
        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        { Check(value); writer.WritePropertyName(value); }
    }
}
