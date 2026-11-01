using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.StarGifts;

public static class UpgradeAttributeHelper
{
    public static async Task<TVector<IStarGiftAttribute>> GetAllAsync(IMongoDatabase db, StarGiftDocument gift)
    {
        var col = db.GetCollection<UpgradeConfigEntry>("star-gift-upgrade-config");
        // Pull both the gift-specific pool (GiftId == this gift) and the
        // global default pool (GiftId absent or 0). The "GiftId missing"
        // branch is needed because the original seed for the global pattern /
        // backdrop / model variants didn't write the field at all.
        var filter = Builders<UpgradeConfigEntry>.Filter.Or(
            Builders<UpgradeConfigEntry>.Filter.Eq(e => e.GiftId, gift.GiftId),
            Builders<UpgradeConfigEntry>.Filter.Eq(e => e.GiftId, 0L),
            Builders<UpgradeConfigEntry>.Filter.Exists("GiftId", false));
        var all = await col.Find(filter).ToListAsync();

        var models = ScopedRegularAttributes(all, "model", gift.GiftId);
        var patterns = ScopedRegularAttributes(all, "pattern", gift.GiftId);
        var backdrops = ScopedRegularAttributes(all, "backdrop", gift.GiftId);

        var attrs = new TVector<IStarGiftAttribute>();

        foreach (var e in models)
            attrs.Add(new TStarGiftAttributeModel { Name = e.Name, Document = MakeDoc(e), RarityPermille = e.RarityPermille });

        // Do not return the whole craft-global model pool here: the method is
        // used by clients to show variants that can be obtained from this gift
        // type.  Include only craft models explicitly scoped to the same gift_id.
        var craftModels = await LoadGiftSpecificCraftModelsAsync(db, gift.GiftId);
        foreach (var e in craftModels)
            attrs.Add(new TStarGiftAttributeModel { Crafted = true, Name = e.Name, Document = MakeDoc(e), RarityPermille = e.RarityPermille });

        if (models.Count == 0 && craftModels.Count == 0)
            attrs.Add(new TStarGiftAttributeModel { Name = gift.Title ?? "Gift", Document = MakeGiftDoc(gift), RarityPermille = 100 });

        foreach (var e in patterns)
            attrs.Add(new TStarGiftAttributePattern { Name = e.Name, Document = MakeDoc(e), RarityPermille = e.RarityPermille });
        if (patterns.Count == 0)
            attrs.Add(new TStarGiftAttributePattern { Name = gift.Title ?? "Gift", Document = MakeGiftDoc(gift), RarityPermille = 100 });

        foreach (var e in backdrops)
            attrs.Add(new TStarGiftAttributeBackdrop
            {
                Name = e.Name, BackdropId = e.BackdropId ?? 0,
                CenterColor = e.CenterColor ?? 0, EdgeColor = e.EdgeColor ?? 0,
                PatternColor = e.PatternColor ?? 0, TextColor = e.TextColor ?? 0,
                RarityPermille = e.RarityPermille,
            });
        if (backdrops.Count == 0)
            attrs.Add(new TStarGiftAttributeBackdrop
            {
                Name = "Default", BackdropId = 1,
                CenterColor = 0x2980B9, EdgeColor = 0x1A5276,
                PatternColor = 0x3498DB, TextColor = 0xFFFFFF,
                RarityPermille = 100,
            });

        return attrs;
    }

    public static async Task<TVector<IStarGiftAttribute>> GetSampleAsync(IMongoDatabase db, StarGiftDocument gift)
    {
        var attrs = await GetAllAsync(db, gift);
        var result = new TVector<IStarGiftAttribute>();

        var models = attrs.OfType<TStarGiftAttributeModel>().Where(a => !a.Crafted).ToList();
        var patterns = attrs.OfType<TStarGiftAttributePattern>().ToList();
        var backdrops = attrs.OfType<TStarGiftAttributeBackdrop>().ToList();

        // The client spins the upgrade "roulette" through the sample, so it needs several variants of each kind.
        foreach (var m in PickDistinct(models, 8)) result.Add(m);
        foreach (var pt in PickDistinct(patterns, 8)) result.Add(pt);
        foreach (var b in PickDistinct(backdrops, 8)) result.Add(b);

        return result;
    }

    private static List<T> PickDistinct<T>(List<T> items, int count) where T : class
    {
        var pool = new List<T>(items);
        var picked = new List<T>();
        while (picked.Count < count && pool.Count > 0)
        {
            var x = WeightedRandom(pool);
            picked.Add(x);
            pool.Remove(x);
        }
        return picked;
    }

    private static T WeightedRandom<T>(List<T> items) where T : class
    {
        var total = items.Sum(GetRarityPermille);
        if (total <= 0) return items[^1];
        var r = Random.Shared.Next(total);
        var acc = 0;
        foreach (var e in items) { acc += GetRarityPermille(e); if (r < acc) return e; }
        return items[^1];
    }

    private static int GetRarityPermille(object item) => item switch
    {
        UpgradeConfigEntry e => e.RarityPermille,
        CraftAttributeConfigEntry e => e.RarityPermille,
        TStarGiftAttributeModel e => e.RarityPermille,
        TStarGiftAttributePattern e => e.RarityPermille,
        TStarGiftAttributeBackdrop e => e.RarityPermille,
        _ => 1
    };

    private static List<UpgradeConfigEntry> ScopedRegularAttributes(List<UpgradeConfigEntry> all, string type, long giftId)
    {
        var specific = all.Where(e => e.Type == type && e.GiftId == giftId).ToList();
        return specific.Count > 0
            ? specific
            : all.Where(e => e.Type == type && e.GiftId == 0).ToList();
    }

    private static async Task<List<CraftAttributeConfigEntry>> LoadGiftSpecificCraftModelsAsync(IMongoDatabase db, long giftId)
    {
        var col = db.GetCollection<CraftAttributeConfigEntry>("star-gift-craft-config");
        var filter = Builders<CraftAttributeConfigEntry>.Filter.And(
            Builders<CraftAttributeConfigEntry>.Filter.Eq(e => e.Type, "model"),
            Builders<CraftAttributeConfigEntry>.Filter.Eq(e => e.GiftId, giftId));
        return await col.Find(filter).ToListAsync();
    }

    private static IDocument MakeDoc(UpgradeConfigEntry e) => new TDocument
    {
        Id = e.DocumentId ?? 0, AccessHash = e.DocumentAccessHash ?? 0,
        FileReference = e.FileReference ?? [], Date = e.DocumentDate ?? 0,
        MimeType = e.MimeType ?? "application/x-tgsticker",
        Size = e.DocumentSize ?? 0, DcId = e.DcId ?? 2,
        Attributes = [e.Type == "pattern" ? (IDocumentAttribute)new TDocumentAttributeCustomEmoji { Alt = "🎁", Free = true, TextColor = true, Stickerset = new TInputStickerSetEmpty() } : new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }, new TDocumentAttributeImageSize { W = 512, H = 512 }, new TDocumentAttributeFilename { FileName = "AnimatedSticker.tgs" }],
    };

    private static IDocument MakeDoc(CraftAttributeConfigEntry e) => new TDocument
    {
        Id = e.DocumentId ?? 0, AccessHash = e.DocumentAccessHash ?? 0,
        FileReference = e.FileReference ?? [], Date = e.DocumentDate ?? 0,
        MimeType = e.MimeType ?? "application/x-tgsticker",
        Size = e.DocumentSize ?? 0, DcId = e.DcId ?? 2,
        Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }, new TDocumentAttributeImageSize { W = 512, H = 512 }, new TDocumentAttributeFilename { FileName = "AnimatedSticker.tgs" }],
    };

    private static IDocument MakeGiftDoc(StarGiftDocument gift) => new TDocument
    {
        Id = gift.DocumentId, AccessHash = gift.DocumentAccessHash,
        FileReference = gift.FileReference ?? [], Date = gift.DocumentDate,
        MimeType = gift.MimeType ?? "application/x-tgsticker",
        Size = gift.DocumentSize, DcId = gift.DcId,
        Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }, new TDocumentAttributeImageSize { W = 512, H = 512 }, new TDocumentAttributeFilename { FileName = "AnimatedSticker.tgs" }],
    };
}
