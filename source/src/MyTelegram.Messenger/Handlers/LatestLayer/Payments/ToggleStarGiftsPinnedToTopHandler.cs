using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class ToggleStarGiftsPinnedToTopHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestToggleStarGiftsPinnedToTop, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestToggleStarGiftsPinnedToTop obj)
    {
        var col = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var isChannel = obj.Peer is TInputPeerChannel;
        var ownerUserId = isChannel ? 0 : input.UserId;
        var ownerChannelId = obj.Peer is TInputPeerChannel ch ? ch.ChannelId : 0;

        var msgIds = obj.Stargift.OfType<TInputSavedStarGiftUser>().Select(g => g.MsgId).ToHashSet();
        var savedIds = obj.Stargift.OfType<TInputSavedStarGiftChat>().Select(g => g.SavedId).ToHashSet();
        var slugs = obj.Stargift.OfType<TInputSavedStarGiftSlug>().Select(g => g.Slug).ToHashSet();

        await col.UpdateManyAsync(
            d => isChannel ? d.OwnerChannelId == ownerChannelId : d.OwnerUserId == ownerUserId,
            Builders<SavedStarGiftDocument>.Update.Set(d => d.PinnedToTop, false));

        if (msgIds.Count > 0 || savedIds.Count > 0 || slugs.Count > 0)
        {
            var ownerFilter = isChannel
                ? Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerChannelId, ownerChannelId)
                : Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, ownerUserId);
            var selectedFilter = Builders<SavedStarGiftDocument>.Filter.Where(_ => false);
            foreach (var m in msgIds) selectedFilter |= SavedStarGiftMsgIdHelper.MatchMsgId(m);
            if (savedIds.Count > 0) selectedFilter |= Builders<SavedStarGiftDocument>.Filter.In(d => d.RandomId, savedIds);
            if (slugs.Count > 0) selectedFilter |= Builders<SavedStarGiftDocument>.Filter.In(d => d.UniqueSlug, slugs);

            await col.UpdateManyAsync(
                ownerFilter & selectedFilter,
                Builders<SavedStarGiftDocument>.Update.Set(d => d.PinnedToTop, true));
        }

        var pinOrder = 0;
        foreach (var g in obj.Stargift)
        {
            FilterDefinition<SavedStarGiftDocument>? one = g switch
            {
                TInputSavedStarGiftUser uu => SavedStarGiftMsgIdHelper.MatchMsgId(uu.MsgId),
                TInputSavedStarGiftChat cc => Builders<SavedStarGiftDocument>.Filter.Eq(d => d.RandomId, cc.SavedId),
                TInputSavedStarGiftSlug ss => Builders<SavedStarGiftDocument>.Filter.Eq(d => d.UniqueSlug, ss.Slug),
                _ => null
            };
            if (one == null) continue;
            var ownerOne = isChannel
                ? Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerChannelId, ownerChannelId)
                : Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, ownerUserId);
            await col.UpdateManyAsync(ownerOne & one, Builders<SavedStarGiftDocument>.Update.Set(d => d.PinOrder, pinOrder));
            pinOrder++;
        }

        return new TBoolTrue();
    }
}
