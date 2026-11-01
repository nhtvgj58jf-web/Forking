using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Fetch <a href="https://corefork.telegram.org/api/custom-emoji">custom emoji stickers »</a>.Returns a list of <a href="https://corefork.telegram.org/constructor/document">documents</a> with the animated custom emoji in TGS format, and a <a href="https://corefork.telegram.org/constructor/documentAttributeCustomEmoji">documentAttributeCustomEmoji</a> attribute with the original emoji and info about the emoji stickerset this custom emoji belongs to.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getCustomEmojiDocuments"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetCustomEmojiDocumentsHandler(
    IMongoDatabase mongoDatabase,
    IFileReferenceHelper fileReferenceHelper,
    IAccessHashHelper2 accessHashHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetCustomEmojiDocuments, TVector<MyTelegram.Schema.IDocument>>
{
    /// <summary>
    /// How many ids one call may carry. tdlib marks the number as a server-side limit
    /// (<c>StickersManager.h</c>, <c>MAX_GET_CUSTOM_EMOJI_STICKERS = 200</c>) and splits larger
    /// requests itself (<c>get_custom_emoji_stickers_unlimited</c>), so no client sends more; the cap
    /// is here to keep the <c>$in</c> bounded rather than to reject anything real.
    /// </summary>
    private const int MaxDocumentIds = 200;

    protected override async Task<TVector<MyTelegram.Schema.IDocument>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetCustomEmojiDocuments obj)
    {
        if (obj.DocumentId == null || obj.DocumentId.Count == 0)
        {
            return [];
        }

        // Duplicates and order both belong to the client: it matches the answer against its own
        // request, so a repeated id has to come back repeated and in place. Only the query is deduped.
        var requestedIds = obj.DocumentId.Take(MaxDocumentIds).ToList();
        var uniqueIds = requestedIds.Distinct().ToList();

        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var filter = Builders<BsonDocument>.Filter.In("DocumentId", uniqueIds.Select(x => (BsonValue)new BsonInt64(x)));
        var docs = await docCol.Find(filter).ToListAsync();
        var docMap = docs.ToDictionary(d => GetInt64(d["DocumentId"]));
        var collectibleModelDocumentIds = await GetCollectibleModelDocumentIdsAsync(uniqueIds);
        var result = new List<IDocument>(requestedIds.Count);

        foreach (var documentId in requestedIds)
        {
            if (!docMap.TryGetValue(documentId, out var d))
            {
                result.Add(new TDocumentEmpty { Id = documentId });
                continue;
            }

            try
            {
                result.Add(BuildDocument(input, d, collectibleModelDocumentIds.Contains(documentId)));
            }
            catch (Exception)
            {
                // Match Telegram: requested IDs that are not custom emoji are
                // represented by documentEmpty instead of being omitted.
                result.Add(new TDocumentEmpty { Id = documentId });
            }
        }

        return new TVector<IDocument>(result);
    }

    private async Task<HashSet<long>> GetCollectibleModelDocumentIdsAsync(ICollection<long> documentIds)
    {
        var modelAttributeFilter = new BsonDocument
        {
            ["Type"] = new BsonDocument("$in", new BsonArray(new[] { "model", "pattern" })),
            ["DocumentId"] = new BsonDocument("$in", new BsonArray(documentIds))
        };
        var giftFilter = new BsonDocument("Attributes",
            new BsonDocument("$elemMatch", modelAttributeFilter));

        var gifts = await mongoDatabase.GetCollection<BsonDocument>("unique-star-gifts")
            .Find(giftFilter)
            .Project(Builders<BsonDocument>.Projection.Include("Attributes"))
            .ToListAsync();

        return gifts
            .SelectMany(GetModelDocumentIds)
            .Where(documentIds.Contains)
            .ToHashSet();
    }

    private static IEnumerable<long> GetModelDocumentIds(BsonDocument gift)
    {
        if (!gift.TryGetValue("Attributes", out var attributes) || !attributes.IsBsonArray)
        {
            yield break;
        }

        foreach (var value in attributes.AsBsonArray)
        {
            if (!value.IsBsonDocument)
            {
                continue;
            }

            var attribute = value.AsBsonDocument;
            if (!attribute.TryGetValue("Type", out var type) ||
                !type.IsString ||
                (type.AsString != "model" && type.AsString != "pattern") ||
                !attribute.TryGetValue("DocumentId", out var documentId))
            {
                continue;
            }

            yield return GetInt64(documentId);
        }
    }

    private IDocument BuildDocument(IRequestInput input, BsonDocument d, bool isCollectibleModelDocument)
    {
        var documentId = GetInt64(d["DocumentId"]);

        var attributes = GetValidCustomEmojiAttributes(input, d, isCollectibleModelDocument);
        var thumbs = ReadThumbs(d);

        return new TDocument
        {
            Id = documentId,
            AccessHash = accessHashHelper.GenerateAccessHash(input.UserId, input.AccessHashKeyId, documentId, AccessHashType.Document),
            FileReference = fileReferenceHelper.Create(AccessHashType.Document, documentId),
            Date = d.Contains("Date") ? GetInt32(d["Date"]) : 0,
            MimeType = d.Contains("MimeType") ? d["MimeType"].AsString : "application/octet-stream",
            Size = d.Contains("Size") ? GetInt64(d["Size"]) : 0,
            Thumbs = thumbs,
            VideoThumbs = new TVector<IVideoSize>(),
            // A document served with dc_id=0 points the client at a non-existent
            // datacenter; its download request then gets stuck and the client spams
            // help.getConfig trying to discover DC0 (see MessagesController/tgnet
            // updateDcSettings). Fall back to the media DC like every other builder.
            DcId = d.Contains("DcId") && GetInt32(d["DcId"]) > 0 ? GetInt32(d["DcId"]) : MyTelegramConsts.MediaDcId,
            Attributes = attributes
        };
    }

    private static TVector<IPhotoSize> ReadThumbs(BsonDocument document)
    {
        var result = new TVector<IPhotoSize>();
        if (!document.TryGetValue("Thumbs", out var thumbsValue) || !thumbsValue.IsBsonArray)
        {
            return result;
        }

        foreach (var value in thumbsValue.AsBsonArray.Where(value => value.IsBsonDocument))
        {
            var thumb = value.AsBsonDocument;
            var type = thumb.GetValue("_t", "").AsString;
            var thumbType = thumb.GetValue("Type", "").AsString;

            switch (type)
            {
                case nameof(TPhotoSize):
                    result.Add(new TPhotoSize
                    {
                        Type = thumbType,
                        W = GetInt32(thumb["W"]),
                        H = GetInt32(thumb["H"]),
                        Size = GetInt32(thumb["Size"]),
                    });
                    break;
                case nameof(TPhotoCachedSize):
                    result.Add(new TPhotoCachedSize
                    {
                        Type = thumbType,
                        W = GetInt32(thumb["W"]),
                        H = GetInt32(thumb["H"]),
                        Bytes = GetBytes(thumb["Bytes"]),
                    });
                    break;
                case nameof(TPhotoSizeProgressive):
                    result.Add(new TPhotoSizeProgressive
                    {
                        Type = thumbType,
                        W = GetInt32(thumb["W"]),
                        H = GetInt32(thumb["H"]),
                        Sizes = new TVector<int>(thumb["Sizes"].AsBsonArray.Select(GetInt32)),
                    });
                    break;
                case nameof(TPhotoStrippedSize):
                    result.Add(new TPhotoStrippedSize { Type = thumbType, Bytes = GetBytes(thumb["Bytes"]) });
                    break;
                case nameof(TPhotoPathSize):
                    result.Add(new TPhotoPathSize { Type = thumbType, Bytes = GetBytes(thumb["Bytes"]) });
                    break;
                case nameof(TPhotoSizeEmpty):
                    result.Add(new TPhotoSizeEmpty { Type = thumbType });
                    break;
            }
        }

        return result;
    }

    private static byte[] GetBytes(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Binary => value.AsBsonBinaryData.Bytes,
            BsonType.Array => value.AsBsonArray.Select(item => (byte)GetInt32(item)).ToArray(),
            _ => [],
        };
    }

    private TVector<IDocumentAttribute> GetValidCustomEmojiAttributes(IRequestInput input, BsonDocument d, bool isCollectibleModelDocument)
    {
        if (CustomEmojiAttributeHelper.TryGetCustomEmojiAttribute(d, out var customEmojiAttribute))
        {
            NormalizeCustomEmojiStickerSet(input, customEmojiAttribute);
            return GetSupportingAttributes(d, customEmojiAttribute);
        }

        if (CustomEmojiAttributeHelper.TryGetStickerAttributeAsCustomEmoji(d, out customEmojiAttribute))
        {
            NormalizeCustomEmojiStickerSet(input, customEmojiAttribute);
            return GetSupportingAttributes(d, customEmojiAttribute);
        }

        // Unique gift models are valid collectible emoji-status documents even
        // when their source document was uploaded before Attributes2 existed.
        if (isCollectibleModelDocument)
        {
            return
            [
                new TDocumentAttributeCustomEmoji
                {
                    Alt = "🎁",
                    Free = true,
                    Stickerset = new TInputStickerSetEmpty()
                },
                new TDocumentAttributeImageSize { W = 512, H = 512 },
                new TDocumentAttributeFilename { FileName = "AnimatedSticker.tgs" }
            ];
        }

        if (!d.Contains("Attributes2") || d["Attributes2"].IsBsonNull)
        {
            throw new InvalidDataException("Missing custom emoji attributes.");
        }

        throw new InvalidDataException("Document is not a custom emoji.");
    }

    private void NormalizeCustomEmojiStickerSet(IRequestInput input, TDocumentAttributeCustomEmoji attribute)
    {
        if (attribute.Stickerset is TInputStickerSetID stickerSet)
        {
            stickerSet.AccessHash = accessHashHelper.GenerateAccessHash(
                input.UserId,
                input.AccessHashKeyId,
                stickerSet.Id,
                AccessHashType.StickerSet);
            return;
        }

        attribute.Stickerset ??= new TInputStickerSetEmpty();
    }

    private static TVector<IDocumentAttribute> GetSupportingAttributes(
        BsonDocument document,
        TDocumentAttributeCustomEmoji customEmojiAttribute)
    {
        if (!document.TryGetValue("Attributes2", out var attributesValue) || !attributesValue.IsBsonArray)
        {
            return [customEmojiAttribute];
        }

        try
        {
            var attributes = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<TVector<IDocumentAttribute>>(
                attributesValue.ToJson());
            return new TVector<IDocumentAttribute>(
                attributes.Where(attribute =>
                    attribute is not TDocumentAttributeCustomEmoji &&
                    attribute is not TDocumentAttributeSticker)
                .Prepend(customEmojiAttribute));
        }
        catch
        {
            return [customEmojiAttribute];
        }
    }

    private static long GetInt64(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int64 => v.AsInt64,
            BsonType.Int32 => v.AsInt32,
            BsonType.Double => (long)v.AsDouble,
            _ => 0
        };
    }

    private static int GetInt32(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int32 => v.AsInt32,
            BsonType.Int64 => (int)v.AsInt64,
            BsonType.Double => (int)v.AsDouble,
            _ => 0
        };
    }
}
