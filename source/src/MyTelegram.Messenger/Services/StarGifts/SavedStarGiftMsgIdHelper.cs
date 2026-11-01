using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.StarGifts;

/// <summary>
/// Resolves the 32-bit <c>msg_id</c> that addresses a user-owned saved star gift.
/// </summary>
/// <remarks>
/// <para>
/// Layer 229 addresses user-owned gifts through
/// <c>inputSavedStarGiftUser#69279795 msg_id:int</c> — a 32-bit field. Gifts that were never
/// anchored to a service message have <c>MessageId == 0</c>, and the handler used to fall back to
/// casting the 64-bit <c>RandomId</c> into that field. The cast silently truncates:
/// <c>6987717616945285832</c> arrives at the client as <c>1824737992</c>, which matches no gift on
/// the way back, so convert/upgrade/transfer all fail.
/// </para>
/// <para>
/// Instead every unanchored gift gets a stable 32-bit local id from the <c>counters</c> collection,
/// persisted on the document so the value survives restarts and round-trips intact.
/// </para>
/// </remarks>
public static class SavedStarGiftMsgIdHelper
{
    private const string CounterId = "saved-star-gift-msg-id";

    /// <summary>
    /// Local ids start well above realistic service-message ids so a local id can never collide
    /// with a real <see cref="SavedStarGiftDocument.MessageId"/>.
    /// </summary>
    private const int LocalMsgIdBase = 1_000_000_000;

    /// <summary>
    /// Returns the <c>msg_id</c> to advertise for <paramref name="doc"/>, allocating and persisting
    /// a local id when the gift is not anchored to a service message.
    /// </summary>
    public static async Task<int> ResolveAsync(IMongoDatabase database, SavedStarGiftDocument doc)
    {
        if (doc.MessageId > 0) return doc.MessageId;
        var anchoredMsgId = await TryAnchorToServiceMessageAsync(database, doc);
        if (anchoredMsgId > 0) return anchoredMsgId;
        if (doc.LocalMsgId > 0) return doc.LocalMsgId;

        var localMsgId = await NextLocalMsgIdAsync(database);

        await database.GetCollection<SavedStarGiftDocument>("saved-star-gifts")
            .UpdateOneAsync(
                Builders<SavedStarGiftDocument>.Filter.Eq(d => d.Id, doc.Id),
                Builders<SavedStarGiftDocument>.Update.Set(d => d.LocalMsgId, localMsgId));

        doc.LocalMsgId = localMsgId;
        return localMsgId;
    }

    /// <summary>
    /// Filter matching the gift a client addressed by <paramref name="msgId"/>.
    /// </summary>
    /// <remarks>
    /// <c>RandomId</c> is still matched because upgraded (unique) gifts store a small sequential
    /// unique id there, which fits in 32 bits and is what older clients round-trip.
    /// </remarks>
    public static FilterDefinition<SavedStarGiftDocument> MatchMsgId(int msgId)
    {
        var f = Builders<SavedStarGiftDocument>.Filter;
        return f.Or(
            f.And(f.Gt(d => d.MessageId, 0), f.Eq(d => d.MessageId, msgId)),
            f.And(f.Gt(d => d.LocalMsgId, 0), f.Eq(d => d.LocalMsgId, msgId)),
            f.Eq(d => d.RandomId, msgId));
    }

    /// <summary>
    /// A gift bought through the store is saved before its service message has an id, so MessageId stays 0.
    /// When the client addresses it by the real message id, anchor the closest unanchored gift in time.
    /// </summary>
    public static async Task<SavedStarGiftDocument?> AnchorByMessageAsync(
        IMongoCollection<SavedStarGiftDocument> col, long ownerUserId, int msgId)
    {
        if (msgId <= 0 || msgId >= LocalMsgIdBase) return null;
        var msgDoc = await col.Database.GetCollection<BsonDocument>("eventflow-messagereadmodel")
            .Find(new BsonDocument { { "OwnerPeerId", ownerUserId }, { "MessageId", msgId }, { "SendMessageType", 2 } })
            .Project(new BsonDocument { { "Date", 1 }, { "MessageAction", 1 } })
            .FirstOrDefaultAsync();
        if (msgDoc == null || !msgDoc.Contains("Date") || !msgDoc.Contains("MessageAction") || msgDoc["MessageAction"].IsBsonNull)
            return null;
        var msgDate = msgDoc["Date"].ToInt32();
        var candidates = await col.Find(d => d.OwnerUserId == ownerUserId && d.MessageId == 0 && !d.IsUnique
            && d.UpgradeStars.HasValue && d.Date >= msgDate - 10 && d.Date <= msgDate + 10).ToListAsync();
        var best = candidates.OrderBy(d => Math.Abs(d.Date - msgDate)).FirstOrDefault();
        if (best == null) return null;
        var res = await col.UpdateOneAsync(d => d.Id == best.Id && d.MessageId == 0,
            Builders<SavedStarGiftDocument>.Update.Set(d => d.MessageId, msgId));
        if (res.ModifiedCount == 0) return null;
        best.MessageId = msgId;
        return best;
    }

    /// <summary>
    /// Anchors an unanchored gift to its real service message (single unused candidate within +-3 s).
    /// Returns 0 when there is no unambiguous match.
    /// </summary>
    private static async Task<int> TryAnchorToServiceMessageAsync(IMongoDatabase database, SavedStarGiftDocument doc)
    {
        if (doc.OwnerUserId <= 0 || doc.Date <= 0 || doc.IsUnique) return 0;

        var msgCol = database.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var candidates = await msgCol.Find(new BsonDocument
            {
                { "OwnerPeerId", doc.OwnerUserId },
                { "SendMessageType", 2 },
                { "Date", new BsonDocument { { "$gte", doc.Date - 3 }, { "$lte", doc.Date + 3 } } },
                { "MessageAction", new BsonDocument("$ne", BsonNull.Value) },
            })
            .Project(new BsonDocument { { "MessageId", 1 }, { "Date", 1 } })
            .ToListAsync();
        if (candidates.Count == 0) return 0;

        var savedCol = database.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var free = new List<int>();
        foreach (var c in candidates)
        {
            if (!c.Contains("MessageId")) continue;
            var mid = c["MessageId"].ToInt32();
            if (mid <= 0 || mid >= LocalMsgIdBase) continue;
            var taken = await savedCol.CountDocumentsAsync(d => d.OwnerUserId == doc.OwnerUserId && d.MessageId == mid) > 0;
            if (!taken) free.Add(mid);
        }

        if (free.Count != 1) return 0;

        var res = await savedCol.UpdateOneAsync(d => d.Id == doc.Id && d.MessageId == 0,
            Builders<SavedStarGiftDocument>.Update.Set(d => d.MessageId, free[0]));
        if (res.ModifiedCount == 0) return 0;

        doc.MessageId = free[0];
        return free[0];
    }

    private static async Task<int> NextLocalMsgIdAsync(IMongoDatabase database)
    {
        var counter = await database.GetCollection<BsonDocument>("counters")
            .FindOneAndUpdateAsync(
                Builders<BsonDocument>.Filter.Eq("_id", CounterId),
                Builders<BsonDocument>.Update.Inc("seq", 1),
                new FindOneAndUpdateOptions<BsonDocument>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.After
                });

        return LocalMsgIdBase + counter["seq"].AsInt32;
    }
}
