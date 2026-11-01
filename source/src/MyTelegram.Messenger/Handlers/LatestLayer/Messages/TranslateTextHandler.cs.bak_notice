using MyTelegram.Messenger.Services.Translation;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Translate a given text.<a href="https://corefork.telegram.org/api/entities">Styled text entities</a> will only be preserved for <a href="https://corefork.telegram.org/api/premium">Telegram Premium</a> users.
/// Possible errors
/// Code Type Description
/// 400 INPUT_TEXT_EMPTY The specified text is empty.
/// 400 INPUT_TEXT_TOO_LONG The specified text is too long.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 TO_LANG_INVALID The specified destination language is invalid.
/// 500 TRANSLATE_REQ_FAILED Translation failed, please try again later.
/// 400 TRANSLATE_REQ_QUOTA_EXCEEDED Translation is currently unavailable due to a temporary server-side lack of resources.
/// 406 TRANSLATIONS_DISABLED Translations are unavailable, a detailed and localized description for the error will be emitted via an <a href="https://corefork.telegram.org/api/errors#406-not-acceptable">updateServiceNotification as specified here »</a>.
/// 500 TRANSLATION_TIMEOUT A timeout occurred while translating the specified text.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.translateText"/> </c></para>
/// </summary>
/// <remarks>
/// <para>Two input forms, and the wire cannot tell them apart by flag alone — <c>peer</c> and <c>id</c>
/// share flag bit 0, <c>text</c> has its own. The first translates chat messages, the second any text a
/// client holds (an instant-view article, a poll option).</para>
///
/// <para><b>The answer is positional.</b> Every client pairs the returned vector with its request list
/// by index — tdlib refuses a count mismatch outright ("Receive invalid number of results"), Android
/// hands <c>translated.get(i)</c> to the callback registered for <c>ids.get(i)</c>. So there is exactly
/// one entry per input, in input order, and a message with no text contributes an empty entry rather
/// than being skipped.</para>
///
/// <para><b>Entities only for Premium</b>, as the API states. A non-Premium caller gets the plain
/// translation with an empty — never null — entity vector.</para>
///
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class TranslateTextHandler(
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IUserAppService userAppService,
    ITextTranslationClient translationClient,
    ITranslationEntityCodec entityCodec,
    ITranslationCache translationCache,
    IObjectMessageSender objectMessageSender,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<TranslateTextHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestTranslateText,
        MyTelegram.Schema.Messages.ITranslatedText>
{
    /// <summary>One text on its way to the provider.</summary>
    private sealed record Item(string Text, List<IMessageEntity> Entities)
    {
        public string CacheKey { get; set; } = string.Empty;
        public TranslatedText? Result { get; set; }
    }

    protected override async Task<MyTelegram.Schema.Messages.ITranslatedText> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Messages.RequestTranslateText obj)
    {
        var config = options.CurrentValue.Translation;

        var targetLanguage = TranslationLanguageMap.Resolve(obj.ToLang);

        if (targetLanguage == null)
        {
            RpcErrors.RpcErrors400.ToLangInvalid.ThrowRpcError();
        }

        // The count is checked against the request, before anything is loaded. Doing it after the
        // message lookup means a batch of 21 ids whose last id is stale answers MSG_ID_INVALID, and the
        // client repairs the wrong thing — it was over the limit, not pointing at a deleted message.
        var requested = obj.Id?.Count > 0 ? obj.Id.Count : obj.Text?.Count ?? 0;

        if (requested > config.MaxMessagesPerRequest)
        {
            RpcErrors.RpcErrors400.InputTextTooLong.ThrowRpcError();
        }

        var items = obj.Id?.Count > 0
            ? await LoadMessagesAsync(input, obj)
            : LoadTexts(obj);

        if (items.Count == 0)
        {
            RpcErrors.RpcErrors400.InputTextEmpty.ThrowRpcError();
        }

        // UTF-16 code units, the unit the limits the clients enforce are expressed in.
        if (items.Sum(p => p.Text.Length) > config.MaxCharactersPerRequest)
        {
            RpcErrors.RpcErrors400.InputTextTooLong.ThrowRpcError();
        }

        if (!translationClient.IsEnabled)
        {
            await ReportTranslationsUnavailableAsync(input);
            RpcErrors.RpcErrors406.TranslationsDisabled.ThrowRpcError();
        }

        var user = await userAppService.GetAsync(input.UserId);
        var withEntities = config.PreserveEntities && user?.Premium == true;
        var tone = TranslationLanguageMap.ResolveFormality(obj.Tone);

        await TranslateAsync(items, targetLanguage!, obj.Tone, tone, withEntities);

        return new TTranslateResult
        {
            Result = [.. items.Select(p => (ITextWithEntities)new TTextWithEntities
            {
                Text = p.Result?.Text ?? p.Text,
                Entities = withEntities ? p.Result?.Entities ?? [] : []
            })]
        };
    }
    /// <summary>
    /// The <c>peer</c> + <c>id</c> form. One query for the whole batch: clients send twenty ids at a
    /// time, and an id-per-round-trip here would be twenty Mongo reads per screen of a translated chat.
    /// </summary>
    private async Task<List<Item>> LoadMessagesAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestTranslateText obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // Private chats number messages per user, so the caller's own box is the one that holds their
        // copy; a channel numbers them once for everybody. Same rule messages.getMessages applies.
        var ownerPeerId = peer!.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;

        var requested = obj.Id!.ToList();
        var messageIds = requested.Select(p => MessageId.Create(ownerPeerId, p).Value).ToList();

        var messages = await queryProcessor.ProcessAsync(new GetMessagesByIdListQuery(messageIds));
        var byId = messages.ToDictionary(p => p.MessageId);

        var items = new List<Item>(requested.Count);

        foreach (var id in requested)
        {
            if (!byId.TryGetValue(id, out var message))
            {
                RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
            }

            items.Add(new Item(message!.Message ?? string.Empty, ReadEntities(message)));
        }

        return items;
    }

    /// <summary>
    /// Rows written before <c>Entities2</c> carry the entity vector as a TL blob. Both are read, so a
    /// message stored by an older build still translates with its formatting.
    /// </summary>
    private static List<IMessageEntity> ReadEntities(IMessageReadModel message)
    {
        if (message.Entities2?.Count > 0)
        {
            return [.. message.Entities2];
        }

        if (message.Entities is { Length: > 0 } blob)
        {
            var decoded = blob.ToTObject<TVector<IMessageEntity>>();

            if (decoded?.Count > 0)
            {
                return [.. decoded];
            }
        }

        return [];
    }
    /// <summary>
    /// The <c>text</c> form. Every entry empty is a client that asked for nothing, which is what
    /// <c>INPUT_TEXT_EMPTY</c> is for; an empty <i>caption</i> in the message form is not, so the check
    /// lives here rather than in the caller.
    /// </summary>
    private static List<Item> LoadTexts(MyTelegram.Schema.Messages.RequestTranslateText obj)
    {
        var texts = obj.Text;

        if (texts == null || texts.Count == 0)
        {
            return [];
        }

        var items = texts
            .Select(p => new Item(p.Text ?? string.Empty,
                p.Entities?.Count > 0 ? [.. p.Entities] : []))
            .ToList();

        return items.All(p => string.IsNullOrEmpty(p.Text)) ? [] : items;
    }

    /// <summary>
    /// Fills every item's result: from the cache where possible, from the provider for the rest, in one
    /// call. An empty text is not sent anywhere — there is nothing to translate and a provider would
    /// bill for the round trip.
    /// </summary>
    private async Task TranslateAsync(List<Item> items, string targetLanguage, string? tone,
        string? formality, bool withEntities)
    {
        var pending = new List<Item>();

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Text))
            {
                item.Result = new TranslatedText(string.Empty, []);

                continue;
            }

            item.CacheKey = translationCache.BuildKey(item.Text, item.Entities, targetLanguage, tone,
                withEntities);
            item.Result = await translationCache.GetAsync(item.CacheKey);

            if (item.Result == null)
            {
                pending.Add(item);
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        // One provider call for the batch. Whether a given text travels as markup is decided per text:
        // a message with no entities has nothing to reposition, but the whole batch has to agree on
        // tag_handling, so any entity in the batch puts all of it through the codec.
        var asHtml = withEntities && pending.Any(p => p.Entities.Count > 0);
        var payload = new List<string>(pending.Count);

        foreach (var item in pending)
        {
            var encoded = asHtml ? entityCodec.Encode(item.Text, item.Entities) : null;

            payload.Add(encoded ?? (asHtml ? Escape(item.Text) : item.Text));
        }

        var outcome = await translationClient.TranslateAsync(payload, targetLanguage, formality, asHtml);

        if (!outcome.Succeeded)
        {
            logger.LogWarning("Translation to {Language} failed: {Failure} {Error}", targetLanguage,
                outcome.Failure, outcome.Error);

            switch (outcome.Failure)
            {
                case TextTranslationFailure.QuotaExceeded:
                    RpcErrors.RpcErrors400.TranslateReqQuotaExceeded.ThrowRpcError();

                    break;
                case TextTranslationFailure.Timeout:
                    RpcErrors.RpcErrors500.TranslationTimeout.ThrowRpcError();

                    break;
                default:
                    RpcErrors.RpcErrors500.TranslateReqFailed.ThrowRpcError();

                    break;
            }
        }

        for (var i = 0; i < pending.Count; i++)
        {
            var item = pending[i];
            var translated = outcome.Texts![i];

            item.Result = asHtml
                ? entityCodec.Decode(translated, item.Entities)
                : new TranslatedText(translated, []);

            await translationCache.SetAsync(item.CacheKey, item.Result);
        }
    }

    /// <summary>
    /// A text with no entities still has to be escaped when the batch travels as markup, or a literal
    /// <c>&lt;</c> in a message becomes a tag the provider tries to balance.
    /// </summary>
    private static string Escape(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
    /// <summary>
    /// The <c>406</c> half of the contract: "a detailed and localized description for the error will be
    /// emitted via an updateServiceNotification". Without it a client shows a bare error code for a
    /// deployment that simply has no translation backend configured — and answering fabricated text
    /// instead, which is what this handler used to do, is worse still: to every client that is a
    /// successful translation.
    /// </summary>
    private async Task ReportTranslationsUnavailableAsync(IRequestInput input)
    {
        const string message = "Message translation is not available on this server.";

        var updates = new TUpdates
        {
            Updates =
            [
                new TUpdateServiceNotification
                {
                    InboxDate = CurrentDate,
                    Type = "TranslationsDisabled",
                    Message = message,
                    Media = new TMessageMediaEmpty(),
                    Entities = []
                }
            ],
            Chats = [],
            Users = [],
            Date = CurrentDate
        };

        // To the caller, including the session that asked: it is the one holding the error.
        await objectMessageSender.PushMessageToPeerAsync(input.UserId.ToUserPeer(), updates);
    }



}
