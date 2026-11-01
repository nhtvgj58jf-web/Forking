using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Open a <a href="https://corefork.telegram.org/api/bots/webapps#main-mini-apps">Main Mini App</a>.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.requestMainWebView"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class RequestMainWebViewHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IAccessHashHelper2 accessHashHelper,
    IWebViewSessionStore webViewSessionStore,
    IWebAppInitDataBuilder webAppInitDataBuilder,
    ITopPeerUsageRecorder topPeerUsageRecorder,
    ILayeredService<IWebViewResultUrlResponseConverter> webViewResultsLayeredService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestRequestMainWebView, MyTelegram.Schema.IWebViewResult>
{
    protected override async Task<MyTelegram.Schema.IWebViewResult> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestRequestMainWebView obj)
    {
        if (obj.Bot is not TInputUser inputBot)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            return null!;
        }

        await accessHashHelper.CheckAccessHashAsync(input, inputBot.UserId, inputBot.AccessHash);

        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputBot.UserId));
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        // Only bots that actually declare a main mini app expose the "Open App" button.
        if (!botReadModel!.BotHasMainApp)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var url = await webViewSessionStore.ResolveBotUrlAsync(inputBot.UserId);
        if (url == null)
        {
            // BotHasMainApp is set but no URL was configured; treat as no main app.
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        var queryId = await webViewSessionStore.CreateSessionAsync(inputBot.UserId, input.UserId, peer, url!);
        url = await webAppInitDataBuilder.AppendInitDataAsync(url!, inputBot.UserId, input.UserId, queryId);

        // Opening a mini app is what topPeerCategoryBotsApp ranks; nothing else records it.
        // See https://corefork.telegram.org/api/top-rating
        await topPeerUsageRecorder.RecordAsync(input.UserId, TopPeerCategory.BotsApp, PeerType.User,
            inputBot.UserId);

        var result = new TWebViewResultUrl
        {
            QueryId = queryId,
            Url = url!,
            Fullscreen = obj.Fullscreen
        };

        return webViewResultsLayeredService.GetConverter(input.Layer).ToLayeredData(result);
    }
}
