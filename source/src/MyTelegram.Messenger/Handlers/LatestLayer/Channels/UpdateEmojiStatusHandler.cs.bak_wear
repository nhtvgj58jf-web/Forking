using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Set an <a href="https://corefork.telegram.org/api/emoji-status">emoji status</a> for a channel or supergroup.
/// Possible errors
/// Code Type Description
/// 400 BOOSTS_REQUIRED The specified channel must first be <a href="https://corefork.telegram.org/api/boost">boosted by its users</a> in order to perform this action.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 COLLECTIBLE_INVALID The specified collectible is invalid.
/// 400 DOCUMENT_INVALID The specified document is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.updateEmojiStatus"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateEmojiStatusHandler(
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IBoostLevelCalculator boostLevelCalculator,
    IChannelEmojiStatusValidator channelEmojiStatusValidator,
    IAppConfigHelper appConfigHelper,
    IQueryProcessor queryProcessor,
    IEmojiStatusResolver emojiStatusResolver,
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestUpdateEmojiStatus, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestUpdateEmojiStatus obj)
    {
        var channel = peerHelper.GetChannel(obj.Channel);
        await channelAdminRightsChecker.CheckAdminRightAsync(channel.PeerId, input.UserId,
            p => p.ChangeInfo, RpcErrors.RpcErrors400.ChatAdminRequired);

        var channelReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(channel.PeerId));
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        EmojiStatus? emojiStatus;
        switch (obj.EmojiStatus)
        {
            case TEmojiStatusEmpty:
                emojiStatus = null;
                break;
            case TEmojiStatus status:
            {
                await CheckBoostLevelAsync(channelReadModel!);
                if (!await channelEmojiStatusValidator.IsAllowedAsync(status.DocumentId))
                {
                    RpcErrors.RpcErrors400.DocumentInvalid.ThrowRpcError();
                }

                emojiStatus = new EmojiStatus(status.DocumentId, status.Until);
                break;
            }
            case TInputEmojiStatusCollectible collectible:
            {
                await CheckBoostLevelAsync(channelReadModel!);

                // The collectible must be owned by the user setting it, otherwise anyone could
                // decorate their channel with somebody else's gift.
                var doc = await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
                    .Find(d => d.UniqueId == collectible.CollectibleId
                               && d.OwnerUserId == input.UserId
                               && !d.Burned)
                    .FirstOrDefaultAsync();
                if (doc == null)
                {
                    RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
                }

                var model = doc!.Attributes.FirstOrDefault(a => a.Type == "model");
                var documentId = model?.DocumentId ?? doc.DocumentId;
                emojiStatus = new EmojiStatus(documentId, collectible.Until, collectible.CollectibleId);
                break;
            }
            default:
                RpcErrors.RpcErrors400.DocumentInvalid.ThrowRpcError();
                return null!;
        }

        var prevStatus = await emojiStatusResolver.ResolveAsync(channelReadModel!.EmojiStatus, input.Layer);

        // A collectible status repaints the whole profile page, so it is mutually exclusive with a
        // custom profile palette: setting one clears the other.
        if (emojiStatus?.CollectibleId != null)
        {
            await commandBus.PublishAsync(new UpdateChannelColorCommand(
                ChannelId.Create(channel.PeerId),
                input.ToRequestInfo() with { ReqMsgId = 0 },
                new PeerColor(null, null),
                null,
                true));
        }

        await commandBus.PublishAsync(new UpdateChannelEmojiStatusCommand(
            ChannelId.Create(channel.PeerId),
            input.ToRequestInfo(),
            emojiStatus));

        await AdminLogHelper.LogChangeEmojiStatus(mongoDatabase, channel.PeerId, input.UserId,
            prevStatus,
            await emojiStatusResolver.ResolveAsync(emojiStatus, input.Layer));

        // The updateChannel push telling every member about the new status is emitted by
        // ChannelDomainEventHandler once ChannelEmojiStatusUpdatedEvent is committed.
        return null!;
    }

    /// <summary>
    /// Channels and supergroups must reach the boost level advertised in the app config
    /// (<c>channel_emoji_status_level_min</c> / <c>group_emoji_status_level_min</c>) before they may
    /// set an emoji status.
    /// </summary>
    private async Task CheckBoostLevelAsync(IChannelReadModel channelReadModel)
    {
        var minLevel = appConfigHelper.GetInt32Value(
            channelReadModel.Broadcast ? "channel_emoji_status_level_min" : "group_emoji_status_level_min",
            8);
        if (minLevel <= 0)
        {
            return;
        }

        var boostLevel = await boostLevelCalculator.GetLevelAsync(channelReadModel.ChannelId);
        if (boostLevel < minLevel)
        {
            RpcErrors.RpcErrors400.BoostsRequired.ThrowRpcError();
        }
    }
}
