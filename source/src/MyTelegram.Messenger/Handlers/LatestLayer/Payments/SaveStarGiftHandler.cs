using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class SaveStarGiftHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestSaveStarGift, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestSaveStarGift obj)
    {
        var collection = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");

        if (obj.Stargift is TInputSavedStarGiftUser userGift)
        {
            var filter = Builders<SavedStarGiftDocument>.Filter.And(
                Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, input.UserId),
                SavedStarGiftMsgIdHelper.MatchMsgId(userGift.MsgId)
            );
            var update = Builders<SavedStarGiftDocument>.Update.Set(d => d.Saved, !obj.Unsave);
            await collection.UpdateOneAsync(filter, update);
        }
        else if (obj.Stargift is TInputSavedStarGiftChat chatGift)
        {
            var channelId = chatGift.Peer is TInputPeerChannel ch ? ch.ChannelId : 0;
            if (channelId != 0)
            {
                var filter = Builders<SavedStarGiftDocument>.Filter.And(
                    Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerChannelId, channelId),
                    Builders<SavedStarGiftDocument>.Filter.Eq(d => d.RandomId, chatGift.SavedId)
                );
                var update = Builders<SavedStarGiftDocument>.Update.Set(d => d.Saved, !obj.Unsave);
                await collection.UpdateOneAsync(filter, update);
            }
        }

        return new TBoolTrue();
    }
}
