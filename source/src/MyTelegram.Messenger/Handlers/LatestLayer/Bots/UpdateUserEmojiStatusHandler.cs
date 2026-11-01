using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Change the emoji status of a user (invoked by bots, see <a href="https://corefork.telegram.org/api/emoji-status#setting-an-emoji-status-from-a-bot">here for more info on the full flow</a>)
/// Possible errors
/// Code Type Description
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// 403 USER_PERMISSION_DENIED The user hasn't granted or has revoked the bot's access to change their emoji status.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.updateUserEmojiStatus"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateUserEmojiStatusHandler(
    ICommandBus commandBus,
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor,
    IUserAppService userAppService,
    IEmojiStatusInputResolver emojiStatusInputResolver) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestUpdateUserEmojiStatus, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestUpdateUserEmojiStatus obj)
    {
        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();
        }

        if (obj.UserId is not TInputUser inputUser)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        var targetUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputUser.UserId));
        if (targetUser == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        var permissionCol = mongoDatabase.GetCollection<BsonDocument>("bot_emoji_status_permissions");
        var permissionFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("BotId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("UserId", inputUser.UserId)
        );
        var permissionDoc = await permissionCol.Find(permissionFilter).FirstOrDefaultAsync();
        if (permissionDoc == null || !permissionDoc.Contains("Enabled") || !permissionDoc["Enabled"].AsBoolean)
        {
            RpcErrors.RpcErrors403.UserPermissionDenied.ThrowRpcError();
        }

        // Collectibles belong to the target user, not to the bot setting the status.
        var emojiStatus = await emojiStatusInputResolver.ResolveAsync(obj.EmojiStatus, inputUser.UserId);

        if (emojiStatus?.CollectibleId != null)
        {
            await commandBus.PublishAsync(new UpdateColorCommand(
                UserId.Create(inputUser.UserId),
                input.ToRequestInfo() with { ReqMsgId = input.ReqMsgId + 1 },
                null,
                true));
        }

        await commandBus.PublishAsync(new UpdateEmojiStatusCommand(
            UserId.Create(inputUser.UserId),
            input.ToRequestInfo(),
            emojiStatus));
        userAppService.InvalidateCache(inputUser.UserId);

        // The rpc result and the updateUserEmojiStatus pushes are emitted by UserDomainEventHandler
        // once UserEmojiStatusUpdatedEvent is committed.
        return null!;
    }
}
