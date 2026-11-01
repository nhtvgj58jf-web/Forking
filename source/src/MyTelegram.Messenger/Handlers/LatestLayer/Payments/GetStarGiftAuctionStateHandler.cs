using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarGiftAuctionStateHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarGiftAuctionState, MyTelegram.Schema.Payments.IStarGiftAuctionState>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.Payments.IStarGiftAuctionState> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarGiftAuctionState obj)
    {
        var auctionDoc = await AuctionHelper.FindAsync(mongoDatabase, obj.Auction);
        if (auctionDoc == null)
        {
            return new MyTelegram.Schema.Payments.TStarGiftAuctionState
            {
                Gift = new TStarGift { Id = 0, Stars = 0, ConvertStars = 0, Sticker = new TDocumentEmpty() },
                State = new TStarGiftAuctionStateNotModified(),
                UserState = new TStarGiftAuctionUserState { AcquiredCount = 0 },
                Timeout = 0,
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
            };
        }

        var giftCol = mongoDatabase.GetCollection<StarGiftDocument>("star-gifts");
        var giftDoc = await giftCol.Find(x => x.GiftId == auctionDoc.GiftId).FirstOrDefaultAsync();
        
        IStarGift? giftTl = null;
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
            giftTl = new TStarGift { Id = auctionDoc.GiftId, Stars = auctionDoc.MinBidAmount, ConvertStars = 0, Sticker = new TDocumentEmpty() };
        }

        var state = AuctionHelper.ToState(auctionDoc);
        
        var acquiredCount = 0;
        if (input.UserId > 0)
        {
            var acquiredCol = mongoDatabase.GetCollection<AuctionAcquiredGiftDocument>("star-gift-auction-acquired");
            acquiredCount = (int)await acquiredCol.CountDocumentsAsync(x => x.GiftId == auctionDoc.GiftId && x.UserId == input.UserId);
        }
        
        var userState = AuctionHelper.ToUserState(auctionDoc, input.UserId, acquiredCount);

        int timeout = 0;
        if (!auctionDoc.Finished && auctionDoc.EndDate > 0)
        {
            var now = DateTime.UtcNow.ToTimestamp();
            timeout = Math.Max(0, auctionDoc.EndDate - now);
        }

        return new MyTelegram.Schema.Payments.TStarGiftAuctionState
        {
            Gift = giftTl,
            State = state,
            UserState = userState,
            Timeout = timeout,
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
        };
    }
}