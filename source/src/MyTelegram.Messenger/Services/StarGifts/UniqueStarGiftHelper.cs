using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.StarGifts;

public static class UniqueStarGiftHelper
{
    public static TStarGiftUnique ToTl(UniqueStarGiftDocument doc, Func<long, bool>? documentExists = null)
    {
        var attrs = new TVector<IStarGiftAttribute>();
        var sourceAttributes = EnsureRenderableAttributeSet(doc.Attributes);
        foreach (var a in sourceAttributes)
        {
            // Do not drop model/pattern just because the document read model is
            // missing: clients need the complete collectible attribute tuple
            // (model + pattern + backdrop).  Some seeded/test gifts have valid
            // document ids that are not present in eventflow-documentreadmodel;
            // filtering them made NFTs render as background-only.
            IStarGiftAttribute attr = a.Type switch
            {
                "backdrop" => new TStarGiftAttributeBackdrop
                {
                    Name = a.Name,
                    BackdropId = a.BackdropId ?? 0,
                    CenterColor = a.CenterColor ?? 0,
                    EdgeColor = a.EdgeColor ?? 0,
                    PatternColor = a.PatternColor ?? 0,
                    TextColor = a.TextColor ?? 0,
                    RarityPermille = a.RarityPermille,
                },
                "pattern" => new TStarGiftAttributePattern
                {
                    Name = a.Name,
                    Document = MakeDoc(a),
                    RarityPermille = a.RarityPermille,
                },
                _ => new TStarGiftAttributeModel
                {
                    Crafted = a.Crafted,
                    Rarity = CraftRarity(a.Crafted ? a.RarityTier : null),
                    Name = a.Name,
                    Document = MakeDoc(a),
                    RarityPermille = a.RarityPermille,
                },
            };
            attrs.Add(attr);
        }

        // Add original details attribute only while the collectible still keeps
        // the original provenance. After drop-original-details the client must
        // not receive either the sender or the original recipient row.
        if (!doc.OriginalDetailsDropped)
        {
            var originalRecipientId = doc.OriginalRecipientUserId > 0 ? doc.OriginalRecipientUserId : doc.OwnerUserId;
            attrs.Add(new TStarGiftAttributeOriginalDetails
            {
                RecipientId = originalRecipientId > 0
                    ? (IPeer)new TPeerUser { UserId = originalRecipientId }
                    : new TPeerChannel { ChannelId = doc.OwnerChannelId },
                SenderId = doc.NameHidden || doc.FromUserId == 0 ? null : new TPeerUser { UserId = doc.FromUserId },
                Date = doc.Date,
                Message = doc.MessageText != null ? new TTextWithEntities { Text = doc.MessageText, Entities = doc.MessageEntities ?? [] } : null,
            });
        }

        // Build Layer 206 resale-amount vector. A unique gift may be listed
        // simultaneously in Stars and/or TON; the vector may contain 0, 1 or 2
        // entries. When ResaleTonOnly is set we emit only the TON amount.
        TVector<IStarsAmount>? resellAmount = null;
        if (doc.ResellStars > 0 || doc.ResellTon > 0)
        {
            resellAmount = new TVector<IStarsAmount>();
            if (doc.ResellStars > 0 && !doc.ResaleTonOnly)
                resellAmount.Add(new TStarsAmount { Amount = doc.ResellStars, Nanos = 0 });
            if (doc.ResellTon > 0)
                resellAmount.Add(new TStarsTonAmount { Amount = doc.ResellTon });
            if (resellAmount.Count == 0) resellAmount = null;
        }

        return new TStarGiftUnique
        {
            Id = doc.UniqueId,
            GiftId = doc.GiftId,
            Title = doc.Title,
            Slug = doc.Slug,
            Num = doc.Num,
            OwnerId = doc.OwnerUserId > 0
                ? (IPeer)new TPeerUser { UserId = doc.OwnerUserId }
                : new TPeerChannel { ChannelId = doc.OwnerChannelId },
            Attributes = attrs,
            AvailabilityIssued = doc.AvailabilityIssued,
            AvailabilityTotal = doc.AvailabilityTotal,
            ResellAmount = resellAmount,
            ResaleTonOnly = doc.ResaleTonOnly,
            ValueAmount = doc.InitialSaleStars > 0 ? doc.InitialSaleStars : null,
            ValueCurrency = doc.InitialSaleStars > 0 ? "XTR" : null,
            ValueUsdAmount = doc.InitialSaleStars > 0 ? doc.InitialSaleStars : null,
            OfferMinStars = doc.OfferMinStars > 0 ? doc.OfferMinStars : null,
            CraftChancePermille = MyTelegram.Messenger.Handlers.LatestLayer.Payments.CraftConfig.ChanceFor(doc),
            Crafted = doc.Crafted,
        };
    }


    private static UniqueGiftAttribute[] EnsureRenderableAttributeSet(UniqueGiftAttribute[] attributes)
    {
        var result = attributes.ToList();

        var hasModel = result.Any(a => a.Type == "model");
        var hasPattern = result.Any(a => a.Type == "pattern");
        var hasBackdrop = result.Any(a => a.Type == "backdrop");
        var docSource = result.FirstOrDefault(a =>
            (a.Type == "model" || a.Type == "pattern") && a.DocumentId.HasValue);

        if (!hasModel && docSource != null)
            result.Insert(0, CopyDocumentAttribute(docSource, "model", docSource.Name));

        if (!hasPattern && docSource != null)
            result.Add(CopyDocumentAttribute(docSource, "pattern", docSource.Name));

        if (!hasBackdrop)
        {
            result.Add(new UniqueGiftAttribute
            {
                Type = "backdrop",
                Name = "Default",
                RarityPermille = 100,
                BackdropId = 1,
                CenterColor = 0x2980B9,
                EdgeColor = 0x1A5276,
                PatternColor = 0x3498DB,
                TextColor = 0xFFFFFF,
            });
        }

        return result.ToArray();
    }

    private static UniqueGiftAttribute CopyDocumentAttribute(UniqueGiftAttribute source, string type, string name) => new()
    {
        Type = type,
        Name = name,
        RarityPermille = source.RarityPermille > 0 ? source.RarityPermille : 100,
        Crafted = source.Crafted,
        RarityTier = source.RarityTier,
        DocumentId = source.DocumentId,
        DocumentAccessHash = source.DocumentAccessHash,
        FileReference = source.FileReference,
        DocumentDate = source.DocumentDate,
        MimeType = source.MimeType,
        DocumentSize = source.DocumentSize,
        DcId = source.DcId,
    };

    private static IStarGiftAttributeRarity? CraftRarity(string? tier) => tier switch { "uncommon" => new TStarGiftAttributeRarityUncommon(), "rare" => new TStarGiftAttributeRarityRare(), "epic" => new TStarGiftAttributeRarityEpic(), "legendary" => new TStarGiftAttributeRarityLegendary(), _ => null };

    private static IDocument MakeDoc(UniqueGiftAttribute a) => new TDocument
    {
        Id = a.DocumentId ?? 0,
        AccessHash = a.DocumentAccessHash ?? 0,
        FileReference = a.FileReference ?? [],
        Date = a.DocumentDate ?? 0,
        MimeType = a.MimeType ?? "application/x-tgsticker",
        Size = a.DocumentSize ?? 0,
        DcId = a.DcId ?? 2,
        Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }, new TDocumentAttributeImageSize { W = 512, H = 512 }, new TDocumentAttributeFilename { FileName = "AnimatedSticker.tgs" }],
    };

    public static async Task<long> NextUniqueIdAsync(IMongoDatabase db)
    {
        var counter = await db.GetCollection<MongoDB.Bson.BsonDocument>("counters")
            .FindOneAndUpdateAsync(
                MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", "unique-star-gift"),
                MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Update.Inc("seq", 1L),
                new FindOneAndUpdateOptions<MongoDB.Bson.BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }
            );
        return counter["seq"].AsInt64;
    }

    public static async Task<int> NextNumForGiftAsync(IMongoDatabase db, long giftId)
    {
        var counter = await db.GetCollection<MongoDB.Bson.BsonDocument>("counters")
            .FindOneAndUpdateAsync(
                MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", $"unique-gift-num-{giftId}"),
                MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Update.Inc("seq", 1),
                new FindOneAndUpdateOptions<MongoDB.Bson.BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }
            );
        return counter["seq"].AsInt32;
    }

    // Generate random attributes from DB config, fallback to gift sticker if no config
    public static async Task<UniqueGiftAttribute[]> GenerateAttributesAsync(IMongoDatabase db, StarGiftDocument gift, bool crafted = false)
    {
        // Upgrade config is seeded with PascalCase fields, craft config with
        // snake_case fields; read them through their matching DTOs and normalize
        // to UpgradeConfigEntry for the random picker.
        List<UpgradeConfigEntry> all;
        if (crafted)
        {
            var craftCol = db.GetCollection<CraftAttributeConfigEntry>("star-gift-craft-config");
            var craftFilter = Builders<CraftAttributeConfigEntry>.Filter.Or(
                Builders<CraftAttributeConfigEntry>.Filter.Eq(e => e.GiftId, gift.GiftId),
                Builders<CraftAttributeConfigEntry>.Filter.Eq(e => e.GiftId, 0L),
                Builders<CraftAttributeConfigEntry>.Filter.Exists("gift_id", false));
            all = (await craftCol.Find(craftFilter).ToListAsync()).Select(ToUpgradeConfigEntry).ToList();
        }
        else
        {
            var col = db.GetCollection<UpgradeConfigEntry>("star-gift-upgrade-config");
            var filter = Builders<UpgradeConfigEntry>.Filter.Or(
                Builders<UpgradeConfigEntry>.Filter.Eq(e => e.GiftId, gift.GiftId),
                Builders<UpgradeConfigEntry>.Filter.Eq(e => e.GiftId, 0L),
                Builders<UpgradeConfigEntry>.Filter.Exists("GiftId", false));
            all = await col.Find(filter).ToListAsync();
        }

        var models    = all.Where(e => e.Type == "model"   && e.GiftId == gift.GiftId).ToList();
        if (models.Count == 0) models = all.Where(e => e.Type == "model" && e.GiftId == 0).ToList();
        var patterns  = all.Where(e => e.Type == "pattern" && e.GiftId == gift.GiftId).ToList();
        if (patterns.Count == 0) patterns = all.Where(e => e.Type == "pattern" && e.GiftId == 0).ToList();
        var backdrops = all.Where(e => e.Type == "backdrop").ToList();

        // Fallback: use gift sticker as model/pattern if no config
        UniqueGiftAttribute Pick(List<UpgradeConfigEntry> list, string type)
        {
            if (list.Count == 0)
                return new UniqueGiftAttribute
                {
                    Type = type, Name = gift.Title ?? "Gift",
                    RarityPermille = 100,
                    Crafted = crafted,
                    DocumentId = gift.DocumentId, DocumentAccessHash = gift.DocumentAccessHash,
                    FileReference = gift.FileReference, DocumentDate = gift.DocumentDate,
                    MimeType = gift.MimeType, DocumentSize = gift.DocumentSize, DcId = gift.DcId,
                };
            var e = WeightedRandom(list);
            return new UniqueGiftAttribute
            {
                Type = type, Name = e.Name, RarityPermille = e.RarityPermille,
                Crafted = crafted,
                DocumentId = e.DocumentId, DocumentAccessHash = e.DocumentAccessHash,
                FileReference = e.FileReference, DocumentDate = e.DocumentDate,
                MimeType = e.MimeType, DocumentSize = e.DocumentSize, DcId = e.DcId,
            };
        }

        UniqueGiftAttribute PickBackdrop()
        {
            if (backdrops.Count == 0)
                return new UniqueGiftAttribute { Type = "backdrop", Name = "Default", RarityPermille = 100, BackdropId = 1, CenterColor = 0x2980B9, EdgeColor = 0x1A5276, PatternColor = 0x3498DB, TextColor = 0xFFFFFF };
            var e = WeightedRandom(backdrops);
            return new UniqueGiftAttribute
            {
                Type = "backdrop", Name = e.Name, RarityPermille = e.RarityPermille,
                BackdropId = e.BackdropId, CenterColor = e.CenterColor,
                EdgeColor = e.EdgeColor, PatternColor = e.PatternColor, TextColor = e.TextColor,
            };
        }

        return [Pick(models, "model"), Pick(patterns, "pattern"), PickBackdrop()];
    }


    private static UpgradeConfigEntry ToUpgradeConfigEntry(CraftAttributeConfigEntry e) => new()
    {
        Type = e.Type,
        GiftId = e.GiftId,
        Name = e.Name,
        RarityPermille = e.RarityPermille,
        DocumentId = e.DocumentId,
        DocumentAccessHash = e.DocumentAccessHash,
        FileReference = e.FileReference,
        DocumentDate = e.DocumentDate,
        MimeType = e.MimeType,
        DocumentSize = e.DocumentSize,
        DcId = e.DcId,
        BackdropId = e.BackdropId,
        CenterColor = e.CenterColor,
        EdgeColor = e.EdgeColor,
        PatternColor = e.PatternColor,
        TextColor = e.TextColor,
    };

    private static T WeightedRandom<T>(List<T> items) where T : UpgradeConfigEntry
    {
        var total = items.Sum(e => e.RarityPermille);
        var r = Random.Shared.Next(total);
        var acc = 0;
        foreach (var e in items) { acc += e.RarityPermille; if (r < acc) return e; }
        return items[^1];
    }

    // Sync fallback (used by preview when no DB available)
    public static UniqueGiftAttribute[] GenerateAttributes(StarGiftDocument gift)
    {
        var rng = Random.Shared;
        var backdrops = new[]
        {
            new { Name = "Crimson", BackdropId = 1, Center = 0xC0392B, Edge = 0x922B21, Pattern = 0xE74C3C, Text = 0xFFFFFF },
            new { Name = "Azure",   BackdropId = 2, Center = 0x2980B9, Edge = 0x1A5276, Pattern = 0x3498DB, Text = 0xFFFFFF },
            new { Name = "Emerald", BackdropId = 3, Center = 0x27AE60, Edge = 0x1E8449, Pattern = 0x2ECC71, Text = 0xFFFFFF },
            new { Name = "Gold",    BackdropId = 4, Center = 0xF39C12, Edge = 0xB7770D, Pattern = 0xF1C40F, Text = 0x000000 },
            new { Name = "Violet",  BackdropId = 5, Center = 0x8E44AD, Edge = 0x6C3483, Pattern = 0x9B59B6, Text = 0xFFFFFF },
        };
        var bd = backdrops[rng.Next(backdrops.Length)];

        return
        [
            new UniqueGiftAttribute
            {
                Type = "model",
                Name = gift.Title ?? "Gift",
                RarityPermille = rng.Next(10, 500),
                DocumentId = gift.DocumentId,
                DocumentAccessHash = gift.DocumentAccessHash,
                FileReference = gift.FileReference,
                DocumentDate = gift.DocumentDate,
                MimeType = gift.MimeType,
                DocumentSize = gift.DocumentSize,
                DcId = gift.DcId,
            },
            new UniqueGiftAttribute
            {
                Type = "pattern",
                Name = gift.Title ?? "Gift",
                RarityPermille = rng.Next(10, 300),
                DocumentId = gift.DocumentId,
                DocumentAccessHash = gift.DocumentAccessHash,
                FileReference = gift.FileReference,
                DocumentDate = gift.DocumentDate,
                MimeType = gift.MimeType,
                DocumentSize = gift.DocumentSize,
                DcId = gift.DcId,
            },
            new UniqueGiftAttribute
            {
                Type = "backdrop",
                Name = bd.Name,
                RarityPermille = rng.Next(10, 300),
                BackdropId = bd.BackdropId,
                CenterColor = bd.Center,
                EdgeColor = bd.Edge,
                PatternColor = bd.Pattern,
                TextColor = bd.Text,
            },
        ];
    }
}
