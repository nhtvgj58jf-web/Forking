using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarGiftActiveAuctionsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<RequestGetStarGiftActiveAuctions, IStarGiftActiveAuctions>, IObjectHandler
{
    protected override async Task<IStarGiftActiveAuctions> HandleCoreAsync(IRequestInput input, RequestGetStarGiftActiveAuctions obj)
    {
        var now = DateTime.UtcNow.ToTimestamp();
        
        var auctionCol = mongoDatabase.GetCollection<AuctionDocument>("star-gift-auctions");
        var activeAuctions = await auctionCol.Find(x => !x.Finished && x.StartDate <= now && x.EndDate > now).ToListAsync();
        
        if (activeAuctions.Count == 0)
        {
            return new TStarGiftActiveAuctionsNotModified();
        }

        var giftCol = mongoDatabase.GetCollection<StarGiftDocument>("star-gifts");
        var acquiredCol = mongoDatabase.GetCollection<AuctionAcquiredGiftDocument>("star-gift-auction-acquired");
        
        var auctionStates = new List<IStarGiftActiveAuctionState>();
        var userIds = new HashSet<long>();
        
        foreach (var auction in activeAuctions)
        {
            var giftDoc = await giftCol.Find(x => x.GiftId == auction.GiftId).FirstOrDefaultAsync();
            
            IStarGift? giftTl;
            if (giftDoc != null)
            {
                IPeer? releasedBy = giftDoc.ReleasedByPeerType switch
                {
                    0 => new TPeerUser { UserId = giftDoc.ReleasedByPeerId!.Value },
                    1 => new TPeerChannel { ChannelId = giftDoc.ReleasedByPeerId!.Value },
                    _ => null
                };

                giftTl = new TStarGift
                {
                    Id = giftDoc.GiftId,
                    Stars = giftDoc.Stars,
                    ConvertStars = giftDoc.ConvertStars,
                    UpgradeStars = giftDoc.UpgradeStars,
                    Limited = giftDoc.Limited,
                    SoldOut = giftDoc.SoldOut,
                    Birthday = giftDoc.Birthday,
                    RequirePremium = giftDoc.RequirePremium,
                    LimitedPerUser = giftDoc.LimitedPerUser,
                    AvailabilityTotal = giftDoc.AvailabilityTotal ?? giftDoc.AvailabilityRemains,
                    AvailabilityRemains = giftDoc.AvailabilityRemains,
                    FirstSaleDate = giftDoc.FirstSaleDate,
                    LastSaleDate = giftDoc.LastSaleDate,
                    Title = giftDoc.Title,
                    ReleasedBy = releasedBy,
                    PerUserTotal = giftDoc.PerUserTotal,
                    PerUserRemains = giftDoc.PerUserRemains,
                    LockedUntilDate = giftDoc.LockedUntilDate,
                    Auction = giftDoc.IsAuction,
                    AuctionSlug = giftDoc.IsAuction && !string.IsNullOrEmpty(giftDoc.AuctionSlug) ? giftDoc.AuctionSlug : null,
                    GiftsPerRound = giftDoc.IsAuction && giftDoc.GiftsPerRound > 0 ? giftDoc.GiftsPerRound : null,
                    AuctionStartDate = giftDoc.IsAuction && giftDoc.AuctionStartDate > 0 ? giftDoc.AuctionStartDate : null,
                    Sticker = new TDocument
                    {
                        Id = giftDoc.DocumentId,
                        AccessHash = giftDoc.DocumentAccessHash,
                        FileReference = giftDoc.FileReference ?? [],
                        Date = giftDoc.DocumentDate,
                        MimeType = giftDoc.MimeType ?? "application/x-tgsticker",
                        Size = giftDoc.DocumentSize,
                        DcId = giftDoc.DcId,
                        Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }, new TDocumentAttributeImageSize { W = 512, H = 512 }, new TDocumentAttributeFilename { FileName = "AnimatedSticker.tgs" }],
                    },
                };
            }
            else
            {
                giftTl = new TStarGift { Id = auction.GiftId, Stars = auction.MinBidAmount, ConvertStars = 0, Sticker = new TDocumentEmpty() };
            }

            var state = AuctionHelper.ToState(auction);
            
            int acquiredCount = 0;
            if (input.UserId > 0)
            {
                acquiredCount = (int)await acquiredCol.CountDocumentsAsync(x => x.GiftId == auction.GiftId && x.UserId == input.UserId);
            }
            
            var userState = AuctionHelper.ToUserState(auction, input.UserId, acquiredCount);
            
            foreach (var bid in auction.Bids.Where(b => !b.Returned))
            {
                userIds.Add(bid.UserId);
            }
            
            auctionStates.Add(new TStarGiftActiveAuctionState
            {
                Gift = giftTl,
                State = state,
                UserState = userState,
            });
        }

        return new TStarGiftActiveAuctions
        {
            Auctions = new TVector<IStarGiftActiveAuctionState>(auctionStates),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
        };
    }
}