using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetSavedStarGiftHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestGetSavedStarGift, ISavedStarGifts>
{
    protected override async Task<ISavedStarGifts> HandleCoreAsync(IRequestInput input, RequestGetSavedStarGift obj)
    {
        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var giftCol = mongoDatabase.GetCollection<StarGiftDocument>("star-gifts");
        var uniqueCol = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");

        var gifts = new TVector<ISavedStarGift>();

        foreach (var stargift in obj.Stargift)
        {
            SavedStarGiftDocument? doc = null;
            if (stargift is TInputSavedStarGiftUser u)
                doc = await savedCol.Find(
                    Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, input.UserId)
                    & SavedStarGiftMsgIdHelper.MatchMsgId(u.MsgId)).FirstOrDefaultAsync();
            else if (stargift is TInputSavedStarGiftChat c)
            {
                var channelId = (c.Peer as TInputPeerChannel)?.ChannelId ?? 0;
                var savedId = c.SavedId;
                doc = await savedCol.Find(d => d.OwnerChannelId == channelId && d.RandomId == savedId).FirstOrDefaultAsync();
            }
            else if (stargift is TInputSavedStarGiftSlug s)
                doc = await savedCol.Find(d => d.OwnerUserId == input.UserId && d.UniqueSlug == s.Slug).FirstOrDefaultAsync();
            if (doc == null) continue;

            IStarGift giftTl;
            if (doc.IsUnique && doc.UniqueSlug != null)
            {
                var uniqueDoc = await uniqueCol.Find(d => d.Slug == doc.UniqueSlug).FirstOrDefaultAsync();
                giftTl = uniqueDoc != null ? UniqueStarGiftHelper.ToTl(uniqueDoc, documentId => CollectibleEmojiStatusHelper.DocumentExists(mongoDatabase, documentId)) : new TStarGift { Id = doc.GiftId, Stars = doc.Stars, ConvertStars = doc.ConvertStars };
            }
            else
            {
                var meta = await giftCol.Find(d => d.GiftId == doc.GiftId).FirstOrDefaultAsync();
                giftTl = StarGiftTlBuilder.Build(
                    doc.GiftId,
                    doc.Stars,
                    doc.ConvertStars,
                    doc.UpgradeStars,
                    new TDocument
                    {
                        Id = doc.DocumentId,
                        AccessHash = doc.DocumentAccessHash,
                        FileReference = doc.FileReference,
                        Date = doc.DocumentDate,
                        MimeType = doc.MimeType,
                        Size = doc.DocumentSize,
                        DcId = doc.DcId,
                        Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }, new TDocumentAttributeImageSize { W = 512, H = 512 }, new TDocumentAttributeFilename { FileName = "AnimatedSticker.tgs" }],
                    },
                    meta);
            }

            IPeer? fromId = (!doc.NameHidden && doc.FromUserId != 0) ? new TPeerUser { UserId = doc.FromUserId } : null;
            // Item 14: restore preserved entities (custom emoji, bold, etc.) instead of stripping.
            ITextWithEntities? message = !string.IsNullOrEmpty(doc.MessageText)
                ? new TTextWithEntities { Text = doc.MessageText, Entities = doc.MessageEntities ?? new TVector<IMessageEntity>() } : null;
            int? uniqueTransferLockedUntil = null;
            if (doc.IsUnique && doc.UniqueSlug != null)
                uniqueTransferLockedUntil = (await uniqueCol.Find(d => d.Slug == doc.UniqueSlug).FirstOrDefaultAsync())?.TransferLockedUntil;
            var cooldownAt = LaterTimestamp(doc.CanTransferAt, uniqueTransferLockedUntil);
            var nowForCooldown = DateTime.UtcNow.ToTimestamp();
            if (cooldownAt.HasValue && cooldownAt.Value <= nowForCooldown) cooldownAt = null;
            var canResellAt = LaterTimestamp(doc.CanResellAt, cooldownAt);
            if (canResellAt.HasValue && canResellAt.Value <= nowForCooldown) canResellAt = null;
            var canCraftAt = LaterTimestamp(doc.CanCraftAt, cooldownAt);
            if (canCraftAt.HasValue && canCraftAt.Value <= nowForCooldown) canCraftAt = null;

            gifts.Add(new TSavedStarGift
            {
                Gift = giftTl,
                FromId = fromId,
                Date = doc.Date,
                Message = message,
                MsgId = doc.OwnerUserId > 0
                    ? await SavedStarGiftMsgIdHelper.ResolveAsync(mongoDatabase, doc)
                    : null,
                SavedId = doc.OwnerChannelId > 0 ? doc.RandomId : null,
                ConvertStars = doc.IsUnique || doc.IsAuction ? null : (doc.ConvertStars > 0 ? doc.ConvertStars : null),
                UpgradeStars = !doc.IsUnique && doc.PrepaidUpgrade ? doc.UpgradeStars : null,
                CanUpgrade = !doc.IsUnique && doc.UpgradeStars.HasValue,
                Unsaved = !doc.Saved,
                NameHidden = doc.NameHidden,
                GiftNum = doc.GiftNum,
                CanTransferAt = cooldownAt,
                CanResellAt = canResellAt,
                CanCraftAt = canCraftAt,
            });
        }

        return new TSavedStarGifts { Count = gifts.Count, Gifts = gifts, NextOffset = null, Chats = new TVector<IChat>(), Users = new TVector<IUser>() };
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
