using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
namespace ShiroBot.Adapter.Debug;

internal sealed class DebugServices(Func<DebugRuntime> runtime, Func<DebugConfig> config) : IMessageService, IChannelService, IUserService,
    IFileService, IMessageInteractionService, IMessageReactionService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MessageEvent> _messages = new();
    private readonly Queue<string> _order = new();
    private void Store(MessageEvent message)
    {
        var key = $"{message.Channel.Type}:{message.Channel.Id}:{message.MessageId}";
        if (System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(message, runtime().Catalog.Json)) > config().MaxRecordBytes) return;
        lock (_gate)
        {
            if (!_messages.ContainsKey(key)) _order.Enqueue(key);
            _messages[key] = message;
            while (_order.Count > config().MaxRecords) _messages.Remove(_order.Dequeue());
        }
    }
    public void Remember(BotEvent value) { if (value is MessageEvent message) Store(message); }
    public void Clear() { lock (_gate) { _messages.Clear(); _order.Clear(); } }
    public MessageCapabilities GetMessageCapabilities(Channel channel) => new()
    {
        NativeFeatures = (MessageFeatures)32767, CanMixMarkdown = true, CanCombineButtonsWithMedia = true
    };
    public Task<SentMessage> SendMessageAsync(Channel channel, IReadOnlyList<MessageSegment> segments, CancellationToken cancellationToken = default) =>
        SendMessageAsync(channel, new OutgoingMessage { Segments = segments }, cancellationToken);
    public Task<SentMessage> SendMessageAsync(Channel channel, OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = "sent-" + Guid.NewGuid().ToString("N");
        var source = runtime().SourceContext(channel, message.ReplyTo?.MessageId ?? message.Segments.OfType<QuoteSegment>().FirstOrDefault()?.MessageId);
        runtime().Record("outbound", "sendMessage", new { messageId = id, channel, message }, source.Id, source.Depth);
        Store(new MessageEvent { Platform = config().Platform, SelfId = config().SelfId, MessageId = id, Channel = channel,
            Sender = new User(config().SelfId) { IsBot = true }, Segments = message.Segments, Timestamp = DateTimeOffset.UtcNow });
        return Task.FromResult(new SentMessage(id) { Timestamp = DateTimeOffset.UtcNow });
    }
    private Task Operation(string type, object data, CancellationToken ct) { ct.ThrowIfCancellationRequested(); runtime().Record("outbound", type, data); return Task.CompletedTask; }
    public Task DeleteMessageAsync(Channel channel, string messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = $"{channel.Type}:{channel.Id}:{messageId}";
            _messages.Remove(key);
            var count = _order.Count;
            for (var i = 0; i < count; i++) { var old = _order.Dequeue(); if (old != key) _order.Enqueue(old); }
        }
        return Operation("deleteMessage", new { channel, messageId }, cancellationToken);
    }
    public Task<MessageEvent?> GetMessageAsync(Channel channel, string messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_messages.GetValueOrDefault($"{channel.Type}:{channel.Id}:{messageId}"));
    }
    public Task<IReadOnlyList<MessageEvent>> GetHistoryMessagesAsync(Channel channel, string? beforeMessageId = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var values = _order.Reverse().Where(_messages.ContainsKey).Select(k => _messages[k]).Where(m => m.Channel.Id == channel.Id && m.Channel.Type == channel.Type);
            if (beforeMessageId != null) values = values.SkipWhile(m => m.MessageId != beforeMessageId).Skip(1);
            return Task.FromResult<IReadOnlyList<MessageEvent>>(values.Take(Math.Clamp(limit, 1, 1000)).ToArray());
        }
    }
    public Task<string> GetResourceUrlAsync(string resourceId, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(resourceId); }
    public Task<IReadOnlyList<Channel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult<IReadOnlyList<Channel>>(_messages.Values.Select(m => m.Channel).DistinctBy(c => (c.Type, c.Id)).ToArray());
    }
    public async Task<Channel?> GetChannelAsync(string channelId, CancellationToken cancellationToken = default) => (await GetChannelsAsync(cancellationToken)).FirstOrDefault(c => c.Id == channelId);
    public Task<IReadOnlyList<Member>> GetMembersAsync(string channelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult<IReadOnlyList<Member>>(_messages.Values.Where(m => m.Channel.Id == channelId).Select(m => m.Member ?? new Member(m.Sender)).DistinctBy(m => m.User.Id).ToArray());
    }
    public async Task<Member?> GetMemberAsync(string channelId, string userId, CancellationToken cancellationToken = default) => (await GetMembersAsync(channelId, cancellationToken)).FirstOrDefault(m => m.User.Id == userId);
    public Task SetChannelNameAsync(string channelId, string name, CancellationToken cancellationToken = default) => Operation("setChannelName", new { channelId, name }, cancellationToken);
    public Task KickMemberAsync(string channelId, string userId, CancellationToken cancellationToken = default) => Operation("kickMember", new { channelId, userId }, cancellationToken);
    public Task MuteMemberAsync(string channelId, string userId, TimeSpan duration, CancellationToken cancellationToken = default) => Operation("muteMember", new { channelId, userId, duration }, cancellationToken);
    public Task LeaveChannelAsync(string channelId, CancellationToken cancellationToken = default) => Operation("leaveChannel", new { channelId }, cancellationToken);
    public Task<User> GetSelfAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(new User(config().SelfId) { IsBot = true, Name = "调试机器人" }); }
    public Task<User?> GetUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_messages.Values.Select(m => m.Sender).FirstOrDefault(u => u.Id == userId));
    }
    public Task<IReadOnlyList<User>> GetFriendsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult<IReadOnlyList<User>>(_messages.Values.Where(m => m.IsDirect).Select(m => m.Sender).DistinctBy(u => u.Id).ToArray());
    }
    public Task AcceptFriendRequestAsync(string token, CancellationToken cancellationToken = default) => Operation("acceptFriendRequest", new { token }, cancellationToken);
    public Task RejectFriendRequestAsync(string token, string? reason = null, CancellationToken cancellationToken = default) => Operation("rejectFriendRequest", new { token, reason }, cancellationToken);
    public FileCapabilities GetFileCapabilities(Channel channel) => new() { CanUpload = true, UploadPublishes = false };
    public Task<FileUploadResult> UploadAsync(Channel channel, FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileId = "debug-file-" + Guid.NewGuid().ToString("N");
        runtime().Record("outbound", "upload", new { channel, request, fileId, simulated = true });
        return Task.FromResult(new FileUploadResult { FileId = fileId, IsPublished = false });
    }
    public Task AcknowledgeAsync(InteractionEvent interaction, CancellationToken cancellationToken = default) =>
        Operation("acknowledge", new { interaction }, cancellationToken);
    public ReactionCapabilities GetReactionCapabilities(Channel channel) => ReactionCapabilities.Unicode | ReactionCapabilities.PlatformEmoji;
    public Task SetReactionAsync(MessageReference message, ReactionEmoji emoji, bool isAdd = true, CancellationToken cancellationToken = default) =>
        Operation("setReaction", new { message, emoji, isAdd }, cancellationToken);
}
