namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Set an <a href="https://corefork.telegram.org/api/emoji-status">emoji status</a>
/// Possible errors
/// Code Type Description
/// 400 COLLECTIBLE_INVALID The specified collectible is invalid.
/// 400 DOCUMENT_INVALID The specified document is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateEmojiStatus"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateEmojiStatusHandler(
    ICommandBus commandBus,
    IUserAppService userAppService,
    IEmojiStatusInputResolver emojiStatusInputResolver) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUpdateEmojiStatus, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestUpdateEmojiStatus obj)
    {
        var emojiStatus = await emojiStatusInputResolver.ResolveAsync(obj.EmojiStatus, input.UserId);

        // A collectible status repaints the whole profile page, so it is mutually exclusive with a
        // custom profile palette: setting one clears the other.
        if (emojiStatus?.CollectibleId != null)
        {
            await commandBus.PublishAsync(new UpdateColorCommand(
                UserId.Create(input.UserId),
                input.ToRequestInfo() with { ReqMsgId = input.ReqMsgId + 1 },
                null,
                true));
        }

        await commandBus.PublishAsync(new UpdateEmojiStatusCommand(
            UserId.Create(input.UserId),
            input.ToRequestInfo(),
            emojiStatus));
        userAppService.InvalidateCache(input.UserId);

        // The rpc result and the updateUserEmojiStatus / updateRecentEmojiStatuses pushes are
        // emitted by UserDomainEventHandler once UserEmojiStatusUpdatedEvent is committed.
        return null!;
    }
}
