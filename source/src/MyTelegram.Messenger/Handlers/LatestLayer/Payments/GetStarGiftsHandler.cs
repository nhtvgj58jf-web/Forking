using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarGiftsHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<RequestGetStarGifts, IStarGifts>
{
    protected override async Task<IStarGifts> HandleCoreAsync(IRequestInput input, RequestGetStarGifts obj)
    {
        var collection = mongoDatabase.GetCollection<StarGiftDocument>("star-gifts");
        var docs = await collection.Find(d => d.GiftId > 4).ToListAsync();

        // Count upgrade variants — all variants are shared across gifts
        var upgradeCol = mongoDatabase.GetCollection<BsonDocument>("star-gift-upgrade-config");
        var totalVariantCount = (int)await upgradeCol.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

        // Compute resale stats per gift_id from unique-star-gifts
        var uniqueCol = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");
        var resaleDocs = await uniqueCol.Find(d => d.ResellStars > 0).ToListAsync();
        var resaleByGiftId = resaleDocs
            .GroupBy(d => d.GiftId)
            .ToDictionary(g => g.Key, g => (Count: (long)g.Count(), MinPrice: g.Min(d => d.ResellStars)));

        var gifts = new TVector<IStarGift>();
        foreach (var doc in docs)
        {
            IPeer? releasedBy = doc.ReleasedByPeerType switch
            {
                0 => new TPeerUser { UserId = doc.ReleasedByPeerId!.Value },
                1 => new TPeerChannel { ChannelId = doc.ReleasedByPeerId!.Value },
                _ => null
            };

            gifts.Add(new TStarGift
            {
                Id = doc.GiftId,
                Stars = doc.Stars,
                ConvertStars = doc.ConvertStars,
                UpgradeStars = doc.UpgradeStars,
                Limited = doc.Limited || resaleByGiftId.ContainsKey(doc.GiftId),
                SoldOut = doc.SoldOut,
                Birthday = doc.Birthday,
                RequirePremium = doc.RequirePremium,
                LimitedPerUser = doc.LimitedPerUser,
                AvailabilityTotal = doc.AvailabilityTotal ?? doc.AvailabilityRemains ?? (resaleByGiftId.ContainsKey(doc.GiftId) ? (int?)1000000 : null),
                AvailabilityRemains = doc.AvailabilityRemains ?? (resaleByGiftId.ContainsKey(doc.GiftId) ? (int?)1000000 : null),
                AvailabilityResale = resaleByGiftId.TryGetValue(doc.GiftId, out var rs) ? rs.Count : null,
                FirstSaleDate = doc.FirstSaleDate,
                LastSaleDate = doc.LastSaleDate,
                ResellMinStars = resaleByGiftId.TryGetValue(doc.GiftId, out var rs2) ? rs2.MinPrice : null,
                Title = doc.Title,
                ReleasedBy = releasedBy,
                PerUserTotal = doc.PerUserTotal,
                PerUserRemains = doc.PerUserRemains,
                LockedUntilDate = doc.LockedUntilDate,
                Auction = doc.IsAuction,
                AuctionSlug = doc.IsAuction && !string.IsNullOrEmpty(doc.AuctionSlug) ? doc.AuctionSlug : null,
                GiftsPerRound = doc.IsAuction && doc.GiftsPerRound > 0 ? doc.GiftsPerRound : null,
                AuctionStartDate = doc.IsAuction && doc.AuctionStartDate > 0 ? doc.AuctionStartDate : null,
                UpgradeVariants = totalVariantCount > 0 ? totalVariantCount : null,
                Sticker = new TDocument
                {
                    Id = doc.DocumentId,
                    AccessHash = doc.DocumentAccessHash,
                    FileReference = doc.FileReference ?? [],
                    Date = doc.DocumentDate,
                    MimeType = doc.MimeType ?? "application/x-tgsticker",
                    Size = doc.DocumentSize,
                    DcId = doc.DcId,
                    Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }, new TDocumentAttributeImageSize { W = (doc.MimeType == "image/webp" ? 128 : 512), H = (doc.MimeType == "image/webp" ? 128 : 512) }, new TDocumentAttributeFilename { FileName = (doc.MimeType == "image/webp" ? "sticker.webp" : "AnimatedSticker.tgs") }],
                },
            });
        }

        return new TStarGifts { Gifts = gifts, Hash = docs.Count, Chats = new TVector<IChat>(), Users = new TVector<IUser>() };
    }
}