using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Update the <a href="https://corefork.telegram.org/api/colors">accent color and background custom emoji »</a> of the current account.
/// Possible errors
/// Code Type Description
/// 400 COLOR_INVALID The specified color palette ID was invalid.
/// 400 DOCUMENT_INVALID The specified document is invalid.
/// 403 PREMIUM_ACCOUNT_REQUIRED A premium account is required to execute this action.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateColor"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateColorHandler(
    ICommandBus commandBus,
    IUserAppService userAppService,
    IPeerColorPaletteProvider peerColorPaletteProvider,
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUpdateColor, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestUpdateColor obj)
    {
        // Picking a palette is a premium feature, check before touching the database.
        await userAppService.CheckAccountPremiumStatusAsync(input.UserId);

        PeerColor? color = null;
        switch (obj.Color)
        {
            // Clearing the color.
            case null:
                break;

            case TPeerColor peerColor:
                if (peerColor.Color.HasValue &&
                    peerColorPaletteProvider.GetOption(peerColor.Color.Value, obj.ForProfile) == null)
                {
                    RpcErrors.RpcErrors400.ColorInvalid.ThrowRpcError();
                }

                color = new PeerColor(peerColor.Color, peerColor.BackgroundEmojiId);
                break;

            case TInputPeerColorCollectible collectible:
            {
                var doc = await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
                    .Find(d => d.OwnerUserId == input.UserId && d.UniqueId == collectible.CollectibleId && !d.Burned)
                    .FirstOrDefaultAsync();
                if (doc == null)
                {
                    RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
                }

                color = CollectiblePeerColorHelper.ToPeerColor(doc!);
                break;
            }

            default:
                RpcErrors.RpcErrors400.ColorInvalid.ThrowRpcError();
                break;
        }

        var command = new UpdateColorCommand(UserId.Create(input.UserId), input.ToRequestInfo(), color, obj.ForProfile);
        await commandBus.PublishAsync(command, CancellationToken.None);
        userAppService.InvalidateCache(input.UserId);

        // A collectible profile palette and a collectible emoji status both repaint the profile page
        // and are mutually exclusive, so applying one clears the other.
        if (obj.ForProfile && color?.CollectibleId != null)
        {
            var user = await userAppService.GetAsync(input.UserId);
            if (user?.EmojiStatusCollectibleId != null)
            {
                await commandBus.PublishAsync(new UpdateEmojiStatusCommand(
                    UserId.Create(input.UserId),
                    input.ToRequestInfo() with { ReqMsgId = input.ReqMsgId + 1 },
                    null));
                userAppService.InvalidateCache(input.UserId);
            }
        }

        return new TBoolTrue();
    }
}
