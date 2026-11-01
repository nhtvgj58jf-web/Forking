using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

public static class CraftConfig
{
    // Крафт пока только для Resistance Dog (GiftId 42). Реальные шансы на подарок: 220 или 230.
    public const long DogGiftId = 42;
    public const int DogDefaultChancePermille = 220;

    // help.getAppConfig -> stargifts_craft_attribute_permilles, индекс [число подарков - 1][сколько подарков имеют этот атрибут - 1]
    public static readonly int[][] AttributePermilles = [[100], [80, 400], [70, 350, 700], [60, 300, 600, 1000]];

    public static int? ChanceFor(UniqueStarGiftDocument doc) =>
        doc.CraftChancePermille ?? (doc.GiftId == DogGiftId ? DogDefaultChancePermille : null);

    // craft-only модели Resistance Dog из дампа настоящего Telegram. Используются, пока star-gift-craft-config пуста.
    private static readonly (string Name, string Tier)[] DogModels =
    [
        ("Yin Yang", "legendary"), ("Designer Dog", "legendary"), ("Satoshi", "legendary"),
        ("Guard Dog", "legendary"), ("Aura", "legendary"), ("Ice Walker", "legendary"),
        ("Electric Purple", "epic"), ("Chrome", "epic"), ("Grim Reaper", "epic"), ("Short Circuit", "epic"),
        ("Graffiti", "epic"), ("Anubis", "epic"), ("Black Diamond", "epic"),
        ("Velour", "rare"), ("Justice", "rare"), ("Inferno", "rare"), ("Amethyst", "rare"), ("Angelic", "rare"),
        ("Monument", "rare"), ("The King", "rare"), ("Volcano", "rare"), ("Rebel", "rare"), ("Ruby", "rare"),
        ("Shadow Step", "rare"), ("Pink Cloud", "rare"), ("Outline", "rare"), ("Violent Pink", "rare"),
        ("Emerald", "rare"), ("Titanium", "rare"), ("Crystal", "rare"),
        ("Resisted", "uncommon"),
    ];

    public static List<CraftAttributeConfigEntry> FallbackModels(long giftId) =>
        giftId != DogGiftId
            ? []
            : DogModels.Select(m => new CraftAttributeConfigEntry { Type = "model", GiftId = giftId, Name = m.Name, RarityTier = m.Tier }).ToList();
}

/// <summary>
/// payments.craftStarGift: 1-4 NFT одной коллекции. Шанс успеха = сумма craft_chance_permille.
/// Первый подарок остаётся результатом (id, номер, slug), остальные сгорают. При неудаче сгорают все.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.craftStarGift"/> </c></para>
/// </summary>
internal sealed class CraftStarGiftHandler(IMongoDatabase mongoDatabase, IMessageAppService messageAppService, IPtsHelper ptsHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestCraftStarGift, MyTelegram.Schema.IUpdates>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestCraftStarGift obj)
    {
        var inputGifts = obj.Stargift ?? [];
        if (inputGifts.Count == 0)
            throw new RpcException(new RpcError(400, "SAVED_ID_EMPTY"));
        if (inputGifts.Count > 4)
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var uniqueCol = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");
        var currentTime = DateTime.UtcNow.ToTimestamp();

        var gifts = new List<UniqueStarGiftDocument>();
        foreach (var input_gift in inputGifts)
        {
            UniqueStarGiftDocument? unique = null;

            if (input_gift is TInputSavedStarGiftUser u)
            {
                var saved = await savedCol.Find(
                    Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, input.UserId)
                    & Builders<SavedStarGiftDocument>.Filter.Eq(d => d.IsUnique, true)
                    & SavedStarGiftMsgIdHelper.MatchMsgId(u.MsgId)).FirstOrDefaultAsync();
                if (saved != null)
                    unique = await uniqueCol.Find(d => d.Slug == saved.UniqueSlug).FirstOrDefaultAsync();
            }
            else if (input_gift is TInputSavedStarGiftSlug s)
            {
                unique = await uniqueCol.Find(d => d.Slug == s.Slug && d.OwnerUserId == input.UserId).FirstOrDefaultAsync();
            }
            else if (input_gift is TInputSavedStarGiftChat c)
            {
                unique = await uniqueCol.Find(d => d.UniqueId == c.SavedId && d.OwnerUserId == input.UserId).FirstOrDefaultAsync();
            }

            if (unique == null || unique.Burned)
                throw new RpcException(new RpcError(400, "STARGIFT_ALREADY_BURNED"));

            var savedLock = await savedCol.Find(d => d.IsUnique && d.UniqueSlug == unique.Slug).FirstOrDefaultAsync();
            var lockedUntil = LaterTimestamp(unique.TransferLockedUntil, savedLock?.CanCraftAt);
            if (lockedUntil.HasValue && lockedUntil.Value > currentTime)
                throw new RpcException(new RpcError(400, $"STARGIFT_CRAFT_TOO_EARLY_{lockedUntil.Value - currentTime}"));
            gifts.Add(unique);
        }

        if (gifts.Select(g => g.UniqueId).Distinct().Count() != gifts.Count)
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        var giftId = gifts[0].GiftId;
        if (gifts.Any(g => g.GiftId != giftId))
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        // Первый подарок не может быть на блокчейне (номер не перенесётся).
        if (gifts[0].WasOnBlockchain)
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        var giftDoc = await mongoDatabase.GetCollection<StarGiftDocument>("star-gifts")
            .Find(d => d.GiftId == giftId).FirstOrDefaultAsync();
        if (giftDoc == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        // Шанс успеха = сумма craft_chance_permille, максимум 1000.
        var chances = gifts.Select(CraftConfig.ChanceFor).ToList();
        if (chances.Any(c => c == null))
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        var totalChance = Math.Min(1000, chances.Sum(c => c!.Value));
        var succeeded = Random.Shared.Next(1000) < totalChance;

        var firstChance = chances[0]!.Value;
        var sourceAttrs = gifts.Select(g => g.Attributes).ToList();
        var totalInitialPrice = gifts.Sum(g => g.InitialSaleStars);
        var now = DateTime.UtcNow.ToTimestamp();

        // Сгорают все, кроме первого при успехе. AvailabilityRemains не трогаем.
        var burned = succeeded ? gifts.Skip(1).ToList() : gifts;
        foreach (var gift in burned)
        {
            gift.Burned = true;
            gift.OwnerUserId = 0;
            await uniqueCol.ReplaceOneAsync(d => d.Id == gift.Id, gift);
            await savedCol.DeleteOneAsync(d => d.IsUnique && d.UniqueSlug == gift.Slug);
        }

        if (!succeeded)
        {
            return new TUpdates
            {
                Updates = new TVector<IUpdate> { new TUpdateStarGiftCraftFail() },
                Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = now, Seq = 0
            };
        }

        var first = gifts[0];
        first.Attributes = await BuildAttributesAsync(giftDoc!, sourceAttrs, gifts.Count);
        first.Date = now;
        first.TransferLockedUntil = null;
        first.Crafted = true;
        first.CraftChancePermille = firstChance;
        first.InitialSaleStars = totalInitialPrice;
        await uniqueCol.ReplaceOneAsync(d => d.Id == first.Id, first);

        await savedCol.UpdateOneAsync(
            d => d.IsUnique && d.UniqueSlug == first.Slug && d.OwnerUserId == input.UserId,
            Builders<SavedStarGiftDocument>.Update
                .Set(d => d.Date, now)
                .Set(d => d.Stars, totalInitialPrice)
                .Set(d => d.CanTransferAt, (int?)null)
                .Set(d => d.CanResellAt, (int?)null)
                .Set(d => d.CanCraftAt, (int?)null));

        var msgUpdate = await SendCraftMessageAsync(input, first, now);

        return new TUpdates
        {
            Updates = msgUpdate != null ? new TVector<IUpdate> { msgUpdate } : new TVector<IUpdate>(),
            Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = now, Seq = 0
        };
    }

    private async Task<UniqueGiftAttribute[]> BuildAttributesAsync(StarGiftDocument giftDoc, List<UniqueGiftAttribute[]> sources, int count)
    {
        // Случайный набор из обычного конфига (модель, узор, фон).
        var random = await UniqueStarGiftHelper.GenerateAttributesAsync(mongoDatabase, giftDoc, crafted: false);

        var craftModels = await mongoDatabase.GetCollection<CraftAttributeConfigEntry>("star-gift-craft-config")
            .Find(Builders<CraftAttributeConfigEntry>.Filter.Eq(e => e.Type, "model")
                  & Builders<CraftAttributeConfigEntry>.Filter.Eq(e => e.GiftId, giftDoc.GiftId)).ToListAsync();
        if (craftModels.Count == 0)
            craftModels = CraftConfig.FallbackModels(giftDoc.GiftId);

        var result = new List<UniqueGiftAttribute>();
        foreach (var type in new[] { "model", "pattern", "backdrop" })
        {
            var inherited = PickInherited(sources, type, count);
            if (inherited != null)
            {
                result.Add(inherited);
                continue;
            }

            var fresh = random.First(a => a.Type == type);
            if (type == "model")
                fresh = MakeCraftedModel(fresh, craftModels);
            result.Add(fresh);
        }
        return result.ToArray();
    }

    // Атрибут из исходных подарков сохраняется с шансом AttributePermilles[n-1][k-1],
    // где k = сколько исходных подарков имеют этот атрибут.
    private static UniqueGiftAttribute? PickInherited(List<UniqueGiftAttribute[]> sources, string type, int count)
    {
        var passed = new List<UniqueGiftAttribute>();
        foreach (var group in sources.SelectMany(s => s.Where(a => a.Type == type)).GroupBy(a => a.Name))
        {
            var k = Math.Min(group.Count(), count);
            if (Random.Shared.Next(1000) < CraftConfig.AttributePermilles[count - 1][k - 1])
                passed.Add(group.First());
        }
        return passed.Count == 0 ? null : Clone(passed[Random.Shared.Next(passed.Count)]);
    }

    private static UniqueGiftAttribute MakeCraftedModel(UniqueGiftAttribute normal, List<CraftAttributeConfigEntry> craftModels)
    {
        if (craftModels.Count == 0)
        {
            var c = Clone(normal);
            c.Crafted = true;
            return c;
        }

        // Реальные веса craft-only моделей неизвестны, берём равномерно.
        var e = craftModels[Random.Shared.Next(craftModels.Count)];
        var hasDoc = e.DocumentId.HasValue;
        return new UniqueGiftAttribute
        {
            Type = "model",
            Name = e.Name,
            Crafted = true,
            RarityTier = e.RarityTier,
            RarityPermille = e.RarityPermille > 0 ? e.RarityPermille : normal.RarityPermille,
            // Анимации craft-only моделей пока нет: берём анимацию обычной модели.
            DocumentId = hasDoc ? e.DocumentId : normal.DocumentId,
            DocumentAccessHash = hasDoc ? e.DocumentAccessHash : normal.DocumentAccessHash,
            FileReference = hasDoc ? e.FileReference : normal.FileReference,
            DocumentDate = hasDoc ? e.DocumentDate : normal.DocumentDate,
            MimeType = hasDoc ? e.MimeType : normal.MimeType,
            DocumentSize = hasDoc ? e.DocumentSize : normal.DocumentSize,
            DcId = hasDoc ? e.DcId : normal.DcId,
        };
    }

    private static UniqueGiftAttribute Clone(UniqueGiftAttribute a) => new()
    {
        Type = a.Type, Name = a.Name, RarityPermille = a.RarityPermille, Crafted = a.Crafted, RarityTier = a.RarityTier,
        DocumentId = a.DocumentId, DocumentAccessHash = a.DocumentAccessHash, FileReference = a.FileReference,
        DocumentDate = a.DocumentDate, MimeType = a.MimeType, DocumentSize = a.DocumentSize, DcId = a.DcId,
        CollectibleId = a.CollectibleId, BackdropId = a.BackdropId, CenterColor = a.CenterColor,
        EdgeColor = a.EdgeColor, PatternColor = a.PatternColor, TextColor = a.TextColor,
    };

    // Сообщение messageActionStarGiftUnique с флагом craft (как при улучшении).
    private async Task<IUpdate?> SendCraftMessageAsync(IRequestInput input, UniqueStarGiftDocument first, int date)
    {
        var userId = input.UserId;
        var msgCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var lastBefore = await msgCol
            .Find(new BsonDocument { { "OwnerPeerId", userId }, { "SendMessageType", 2 } })
            .Sort(new BsonDocument("MessageId", -1))
            .Project(new BsonDocument("MessageId", 1))
            .FirstOrDefaultAsync();
        var lastMsgIdBefore = lastBefore != null && lastBefore.Contains("MessageId") ? lastBefore["MessageId"].ToInt32() : 0;

        var action = new TMessageActionStarGiftUnique
        {
            Gift = UniqueStarGiftHelper.ToTl(first),
            Craft = true,
            Saved = true,
        };

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0, PermAuthKeyId = 0 },
            userId,
            new Peer(PeerType.User, userId),
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action
        );
        await messageAppService.SendMessageAsync([sendInput]);

        for (var i = 0; i < 30; i++)
        {
            var created = await msgCol
                .Find(new BsonDocument
                {
                    { "OwnerPeerId", userId }, { "SendMessageType", 2 },
                    { "MessageId", new BsonDocument("$gt", lastMsgIdBefore) }
                })
                .Sort(new BsonDocument("MessageId", -1))
                .Project(new BsonDocument { { "MessageId", 1 }, { "Pts", 1 }, { "Date", 1 } })
                .FirstOrDefaultAsync();
            if (created != null && created.Contains("Pts"))
            {
                return new TUpdateNewMessage
                {
                    Message = new TMessageService
                    {
                        Id = created["MessageId"].ToInt32(),
                        FromId = new TPeerUser { UserId = userId },
                        PeerId = new TPeerUser { UserId = userId },
                        Date = created.Contains("Date") ? created["Date"].ToInt32() : date,
                        Action = action,
                        Out = true,
                    },
                    Pts = created["Pts"].ToInt32(),
                    PtsCount = 1,
                };
            }
            await Task.Delay(100);
        }
        return null;
    }

    private static int? LaterTimestamp(params int?[] values)
    {
        int? result = null;
        foreach (var value in values)
        {
            if (value.HasValue && (!result.HasValue || value.Value > result.Value))
                result = value.Value;
        }
        return result;
    }
}
