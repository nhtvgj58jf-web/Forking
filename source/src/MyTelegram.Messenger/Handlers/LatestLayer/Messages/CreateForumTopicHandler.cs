using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

///<summary>
/// Create a <a href="https://corefork.telegram.org/api/forum">forum topic</a>; requires <a href="https://corefork.telegram.org/api/rights"><code>manage_topics</code> rights</a>.
/// <para>Possible errors</para>
/// Code Type Description
/// 400 CHANNEL_FORUM_MISSING This supergroup is not a forum.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// See <a href="https://corefork.telegram.org/method/messages.createForumTopic" />
///</summary>
internal sealed class CreateForumTopicHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IMessageAppService messageAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IIdGenerator idGenerator) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestCreateForumTopic, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestCreateForumTopic obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        var channelId = peer.PeerId;

        // Check if channel is a forum
        var channelCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channelDoc = await channelCol.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)).FirstOrDefaultAsync();

        if (channelDoc == null)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        if (!channelDoc.Contains("Forum") || !channelDoc["Forum"].AsBoolean)
            RpcErrors.RpcErrors400.ChannelForumMissing.ThrowRpcError();

        // Check manage_topics permission
        if (!await channelAdminRightsChecker.HasChatAdminRightAsync(channelId, input.UserId, p => p.ManageTopics))
            RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();

        // Topic id == id of its creation service message, so it must come from the real per-channel message id sequence.
        var topicId = await idGenerator.NextIdAsync(IdType.MessageId, channelId);

        // Create topic document
        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
        var topicDoc = new BsonDocument
        {
            ["_id"] = $"topic-{channelId}-{topicId}",
            ["ChannelId"] = channelId,
            ["TopicId"] = topicId,
            ["Title"] = obj.Title,
            ["IconColor"] = obj.IconColor ?? 0x6FB9F0,
            ["IconEmojiId"] = obj.IconEmojiId ?? 0L,
            ["CreatorId"] = input.UserId,
            ["Date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["Closed"] = false,
            ["Hidden"] = false,
            ["Pinned"] = false,
            ["Short"] = obj.SendAs != null,
            ["TopMessageId"] = topicId,
            ["TitleMissing"] = obj.TitleMissing
        };

        await topicsCol.InsertOneAsync(topicDoc);

        // Log to admin log
        var forumTopic = new TForumTopic
        {
            Id = topicId,
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Title = obj.Title,
            IconColor = obj.IconColor ?? 0x6FB9F0,
            IconEmojiId = obj.IconEmojiId,
            TopMessage = topicId,
            ReadInboxMaxId = 0,
            ReadOutboxMaxId = 0,
            UnreadCount = 0,
            UnreadMentionsCount = 0,
            UnreadReactionsCount = 0,
            FromId = new TPeerUser { UserId = input.UserId },
            NotifySettings = new TPeerNotifySettings(),
            Peer = new TPeerChannel { ChannelId = channelId }
        };
        await Helpers.AdminLogHelper.LogCreateTopic(mongoDatabase, channelId, input.UserId, forumTopic);

        // Send service message for topic creation
        var action = new TMessageActionTopicCreate
        {
            Title = obj.Title,
            IconColor = obj.IconColor ?? 0x6FB9F0,
            IconEmojiId = obj.IconEmojiId,
            TitleMissing = obj.TitleMissing
        };

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            new Peer(PeerType.Channel, channelId),
            string.Empty,
            obj.RandomId,
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action,
            topMsgId: topicId,
            messageId: topicId
        );

        await messageAppService.SendMessageAsync([sendInput]);

        // Return updateMessageID so client knows the topic ID
        return new TUpdates
        {
            Users = new TVector<IUser>(),
            Updates = new TVector<IUpdate>
            {
                new TUpdateMessageID
                {
                    Id = topicId,
                    RandomId = obj.RandomId
                }
            },
            Chats = new TVector<IChat>(),
            Date = CurrentDate
        };
    }
}
