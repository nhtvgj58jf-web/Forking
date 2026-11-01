using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.PaidMedia;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Messenger.Services.Boosts;
using MyTelegram.Messenger.Services.Payments;
using MyTelegram.Messenger.Services.StarsSubscriptions;
using MyTelegram.Schema.Payments;
using MyTelegram.Services.Services;
using MyTelegram.Domain.Aggregates.ChatInvite;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class SendStarsFormHandler(
    IMongoDatabase mongoDatabase,
    IMessageAppService messageAppService,
    IPeerHelper peerHelper,
    IServiceProvider services,
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IBotInvoicePaymentService botInvoicePaymentService,
    IStarsSubscriptionService starsSubscriptionService) : RpcResultObjectHandler<RequestSendStarsForm, IPaymentResult>
{
    protected override async Task<IPaymentResult> HandleCoreAsync(IRequestInput input, RequestSendStarsForm obj)
    {
        if (obj.Invoice is TInputInvoiceMessage paidMediaInvoice && await IsPaidMediaInvoiceAsync(input, paidMediaInvoice))
            return await HandlePaidMediaPaymentAsync(input, paidMediaInvoice);

        // A Telegram Stars invoice never carries card credentials, so tdlib's send_payment_form routes
        // it here rather than to payments.sendPaymentForm — this is the path a bot invoice actually
        // takes from an official client. See https://corefork.telegram.org/api/payments#5-checkout
        if (obj.Invoice is TInputInvoiceMessage or TInputInvoiceSlug &&
            await botInvoicePaymentService.CanHandleAsync(input, obj.Invoice))
        {
            return await botInvoicePaymentService.PayAsync(input, obj.Invoice, requestedInfoId: null, shippingOptionId: null);
        }

        if (obj.Invoice is TInputInvoiceChatInviteSubscription subscriptionInvoice)
            return await HandleChatInviteSubscriptionAsync(input, subscriptionInvoice);

        // Handle Premium purchase with Stars
        if (obj.Invoice is TInputInvoicePremiumGiftStars premiumStars)
        {
            // Get Stars price based on months (matching PremiumPromoConverter)
            long starsPrice = premiumStars.Months switch
            {
                1 => 2500,   // 1 month = 2500 stars
                3 => 7000,   // 3 months = 7000 stars
                6 => 13000,  // 6 months = 13000 stars
                12 => 25000, // 12 months = 25000 stars
                _ => 0
            };

            if (starsPrice == 0)
                RpcErrors.RpcErrors400.BotInvoiceInvalid.ThrowRpcError();

            // Check Stars balance
            var premiumBalance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            if (premiumBalance < starsPrice)
                RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

            // Get target user
            var targetUserId = premiumStars.UserId switch
            {
                TInputUserSelf => input.UserId,
                TInputUser u => u.UserId,
                _ => peerHelper.GetPeer(premiumStars.UserId, input.UserId).PeerId
            };

            // Premium gifting is not allowed to bots/system users (Telegram clients also reject this UI-side).
            if (targetUserId != input.UserId)
            {
                var queryProcessor0 = services.GetRequiredService<IQueryProcessor>();
                await PeerKindHelper.EnsureNotBotOrSystemAsync(queryProcessor0, targetUserId);
            }

            // Deduct Stars from sender
            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -starsPrice);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -starsPrice,
                title: $"Premium gift ({premiumStars.Months} months)", peerUserId: targetUserId, premiumGiftMonths: premiumStars.Months);

            // Activate Premium subscription for target user
            await ActivatePremiumAsync(targetUserId, premiumStars.Months);

            // Create boost slots for sender (boosts_per_sent_gift = 3)
            // Sender gets 3 boost slots when gifting Premium to another user
            if (targetUserId != input.UserId)
            {
                var boostSlotCol = mongoDatabase.GetCollection<UserBoostSlotDocument>("user-boost-slots");

                // Find next available slot numbers for sender
                var existingSlots = await boostSlotCol.Find(x => x.UserId == input.UserId)
                    .SortByDescending(x => x.Slot)
                    .Limit(1)
                    .ToListAsync();

                int nextSlot = existingSlots.Count > 0 ? existingSlots[0].Slot + 1 : 0;

                // Create 3 new boost slots for sender
                for (int i = 0; i < 3; i++)
                {
                    await boostSlotCol.InsertOneAsync(new UserBoostSlotDocument
                    {
                        Id = $"boost-slot-{input.UserId}-{nextSlot + i}",
                        UserId = input.UserId,
                        Slot = nextSlot + i,
                        Cooldown = 0,
                        ChannelId = null
                    });
                }
            }

            // Create 1 boost slot for recipient
            {
                var boostSlotCol = mongoDatabase.GetCollection<UserBoostSlotDocument>("user-boost-slots");

                var existingSlots = await boostSlotCol.Find(x => x.UserId == targetUserId)
                    .SortByDescending(x => x.Slot)
                    .Limit(1)
                    .ToListAsync();

                int nextSlot = existingSlots.Count > 0 ? existingSlots[0].Slot + 1 : 0;

                await boostSlotCol.InsertOneAsync(new UserBoostSlotDocument
                {
                    Id = $"boost-slot-{targetUserId}-{nextSlot}",
                    UserId = targetUserId,
                    Slot = nextSlot,
                    Cooldown = 0,
                    ChannelId = null
                });
            }

            // Send gift message to recipient
            var action = new TMessageActionGiftPremium
            {
                Currency = "XTR",
                Amount = starsPrice,
                Days = premiumStars.Months * 30,
                Message = premiumStars.Message,
            };

            await messageAppService.SendMessageAsync([new SendMessageInput(
                input.ToRequestInfo() with { ReqMsgId = 0 },
                input.UserId,
                new Peer(PeerType.User, targetUserId),
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: action
            )]);

            return new TPaymentResult { Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 } };
        }

        if (obj.Invoice is TInputInvoiceStarGiftAuctionBid auctionBid)
        {
            var auctionCol = mongoDatabase.GetCollection<AuctionDocument>("star-gift-auctions");
            var auction = await auctionCol.Find(x => x.GiftId == auctionBid.GiftId && !x.Finished).FirstOrDefaultAsync();
            if (auction == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
            if (auctionBid.BidAmount < auction!.MinBidAmount) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            var auctionBalance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            var existingBids = auction.Bids.Where(b => b.UserId == input.UserId && !b.Returned).ToList();
            var existingBid = existingBids.OrderByDescending(b => b.Amount).FirstOrDefault();
            long existingTotal = existingBids.Sum(b => b.Amount);
            long extraNeeded = existingTotal > 0 ? auctionBid.BidAmount - existingTotal : auctionBid.BidAmount;
            if (auctionBalance < extraNeeded) RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

            if (existingBids.Count > 0)
            {
                foreach (var b in existingBids) b.Returned = true;
                await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -extraNeeded);
            }
            else
            {
                await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -auctionBid.BidAmount);
            }

            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -extraNeeded,
                title: $"Auction bid for gift {auctionBid.GiftId}", stargiftAuctionBid: true);

            // Compute top BEFORE adding new bid (to detect who gets pushed out)
            var previousTop = auction.Bids
                .Where(b => !b.Returned && b.UserId != input.UserId)
                .GroupBy(b => b.UserId)
                .Select(g => g.OrderByDescending(b => b.Amount).First())
                .OrderByDescending(b => b.Amount)
                .ToList();

            auction.Bids.Add(new AuctionBid
            {
                UserId = input.UserId,
                Amount = auctionBid.BidAmount,
                Date = DateTime.UtcNow.ToTimestamp(),
                NameHidden = auctionBid.HideName,
                MessageText = (auctionBid.Message as TTextWithEntities)?.Text,
            });
            auction.Version++;
            await auctionCol.ReplaceOneAsync(x => x.Id == auction.Id, auction);

            // Compute new top after adding bid
            var newTopIds = auction.Bids
                .Where(b => !b.Returned)
                .GroupBy(b => b.UserId)
                .Select(g => g.OrderByDescending(b => b.Amount).First())
                .OrderByDescending(b => b.Amount)
                .Take(auction.GiftsPerRound)
                .Select(b => b.UserId)
                .ToHashSet();

            // Notify users who were in previous top but now pushed out
            for (int i = 0; i < previousTop.Count; i++)
            {
                var bid = previousTop[i];
                if (newTopIds.Contains(bid.UserId)) continue;

                // How many spots are available (giftsPerRound)
                var n = auction.GiftsPerRound;
                var msg = $"Your bid on gift 🎁 is no longer in the top {n} highest. Increase your bid to keep competing for the gift.";
                await messageAppService.SendMessageAsync([new SendMessageInput(
                    RequestInfo.Empty with
                    {
                        UserId = MyTelegramConsts.NotificationServiceUserId,
                        Layer = MyTelegramConsts.Layer,
                        Date = DateTime.UtcNow.ToTimestamp(),
                        RequestId = Guid.NewGuid()
                    },
                    MyTelegramConsts.NotificationServiceUserId,
                    new Peer(PeerType.User, bid.UserId),
                    msg,
                    Random.Shared.NextInt64(),
                    sendMessageType: SendMessageType.Text,
                    messageType: MessageType.Text
                )]);
            }

            return new TPaymentResult { Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 } };
        }

        if (obj.Invoice is TInputInvoiceStarGiftPrepaidUpgrade prepaidInvoice)
        {
            var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
            var peer = peerHelper.GetPeer(prepaidInvoice.Peer, input.UserId)!;
            var saved = await savedCol.Find(d =>
                d.OwnerUserId == peer.PeerId &&
                d.PrepaidUpgradeHash == prepaidInvoice.Hash &&
                !d.IsUnique).FirstOrDefaultAsync();
            if (saved == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            var upgradeHandler = new UpgradeStarGiftHandler(mongoDatabase, messageAppService);
            return new TPaymentResult { Updates = await upgradeHandler.UpgradeAsync(input, saved!, overrideUserId: saved!.OwnerUserId) };
        }

        if (obj.Invoice is TInputInvoiceStarGiftUpgrade upgradeInvoice)
        {
            var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
            SavedStarGiftDocument? saved = null;

            // Resolve the exact saved gift the client referred to. Each
            // InputSavedStarGift variant carries a unique key, so we MUST
            // match on it precisely — falling back to "first non-unique gift
            // with UpgradeStars" used to upgrade an arbitrary Blue Star when
            // the user actually clicked Upgrade on a different gift.
            switch (upgradeInvoice.Stargift)
            {
                case TInputSavedStarGiftUser u:
                {
                    // Exact match on whichever key the client round-tripped: a real service-message
                    // id, the allocated local id for unanchored gifts, or RandomId for unique ones.
                    // The old "no hit → newest unanchored upgradable gift" fallback was there
                    // because msg_id arrived truncated and never matched; now that ids survive the
                    // round-trip it would only ever upgrade the wrong gift.
                    if (u.MsgId != 0)
                    {
                        saved = await savedCol.Find(
                            Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, input.UserId)
                            & Builders<SavedStarGiftDocument>.Filter.Eq(d => d.IsUnique, false)
                            & Builders<SavedStarGiftDocument>.Filter.Ne(d => d.UpgradeStars, null)
                            & SavedStarGiftMsgIdHelper.MatchMsgId(u.MsgId)).FirstOrDefaultAsync(); saved ??= await SavedStarGiftMsgIdHelper.AnchorByMessageAsync(savedCol, input.UserId, u.MsgId);
                    }
                    else
                    {
                        // msg_id == 0 → the client gave us nothing to match on; the most recently
                        // received upgradable gift is the only sane interpretation.
                        saved = await savedCol
                            .Find(d => d.OwnerUserId == input.UserId && !d.IsUnique && d.UpgradeStars.HasValue)
                            .Sort(Builders<SavedStarGiftDocument>.Sort.Descending(d => d.Date))
                            .FirstOrDefaultAsync();
                    }
                    break;
                }
                case TInputSavedStarGiftChat c:
                    var channelId = (c.Peer as TInputPeerChannel)?.ChannelId ?? 0;
                    saved = await savedCol.Find(d =>
                        d.OwnerChannelId == channelId &&
                        d.RandomId == c.SavedId &&
                        !d.IsUnique && d.UpgradeStars.HasValue).FirstOrDefaultAsync();
                    break;
                case TInputSavedStarGiftSlug s:
                    saved = await savedCol.Find(d =>
                        d.OwnerUserId == input.UserId &&
                        d.UniqueSlug == s.Slug &&
                        !d.IsUnique && d.UpgradeStars.HasValue).FirstOrDefaultAsync();
                    break;
            }

            if (saved == null || saved.IsUnique || !saved.UpgradeStars.HasValue)
                RpcErrors.RpcErrors400.StargiftUpgradeUnavailable.ThrowRpcError();

            if (saved!.OwnerChannelId > 0)
            {
                var upgChannelId = saved!.OwnerChannelId;
                var upgChCol = mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("eventflow-channelreadmodel");
                var upgIsCreator = await upgChCol.CountDocumentsAsync(new MongoDB.Bson.BsonDocument { { "ChannelId", upgChannelId }, { "CreatorId", input.UserId } }) > 0;
                var upgIsAdmin = upgIsCreator || await mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("eventflow-channelmemberreadmodel")
                    .CountDocumentsAsync(new MongoDB.Bson.BsonDocument { { "ChannelId", upgChannelId }, { "UserId", input.UserId }, { "IsAdmin", true }, { "Left", false } }) > 0;
                if (!upgIsAdmin) RpcErrors.RpcErrors400.StargiftOwnerInvalid.ThrowRpcError();
            }

            var bal = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            if (bal < saved!.UpgradeStars!.Value)
                RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
            var chargedUpgradeStars = saved.UpgradeStars.Value;
            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -saved.UpgradeStars.Value);

            var giftCol = mongoDatabase.GetCollection<StarGiftDocument>("star-gifts");
            StarGiftDocument? giftDoc = null;
            
            
            // First try to find via auction lookup
            if (saved.IsAuction && saved.GiftNum.HasValue && saved.GiftNum.Value > 0)
            {
                try
                {
                    var acquiredCollection = mongoDatabase.GetCollection<BsonDocument>("star-gift-auction-acquired");
                    var filter = Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("UserId", saved.OwnerUserId),
                        Builders<BsonDocument>.Filter.Eq("GiftNum", saved.GiftNum.Value)
                    );
                    var acquiredGift = await acquiredCollection.Find(filter).FirstOrDefaultAsync();
                    if (acquiredGift != null)
                    {
                        var actualGiftId = acquiredGift.GetValue("GiftId").AsInt64;
                        var giftFilter = Builders<StarGiftDocument>.Filter.Eq(g => g.GiftId, actualGiftId);
                        giftDoc = await giftCol.Find(giftFilter).FirstOrDefaultAsync();
                        if (giftDoc != null)
                        {
                            await mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts")
                                .UpdateOneAsync(d => d.Id == saved.Id, Builders<SavedStarGiftDocument>.Update.Set(d => d.GiftId, actualGiftId));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Auction lookup error: {ex.Message}");
                }
            }
            
            // Fallback to direct lookup
            if (giftDoc == null)
            {
                var directFilter = Builders<StarGiftDocument>.Filter.Eq(g => g.GiftId, saved.GiftId);
                giftDoc = await giftCol.Find(directFilter).FirstOrDefaultAsync();
            }
            
            if (giftDoc == null)
                RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            var upgradeHandler = new UpgradeStarGiftHandler(mongoDatabase, messageAppService);
            return new TPaymentResult { Updates = await upgradeHandler.UpgradeAsync(input, saved, giftDoc, chargedUpgradeStars, "In-App Purchase") };
        }

        if (obj.Invoice is TInputInvoiceStarGiftTransfer transferInvoice)
        {
            var transferHandler = new TransferStarGiftHandler(mongoDatabase, messageAppService, peerHelper, queryProcessor);
            const long transferCost = 25;
            var transferBalance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            if (transferBalance < transferCost) RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
            var transferUpdates = await transferHandler.TransferAsync(input, transferInvoice.Stargift, transferInvoice.ToId);
            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -transferCost);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -transferCost, title: "Gift transfer", gift: true);
            return new TPaymentResult { Updates = transferUpdates };
        }

        if (obj.Invoice is TInputInvoiceStarGiftDropOriginalDetails dropInvoice)
        {
            const long dropCost = 25;
            var savedCol2 = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
            SavedStarGiftDocument? saved2 = dropInvoice.Stargift switch
            {
                TInputSavedStarGiftUser u2 => await savedCol2.Find(
                    Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, input.UserId)
                    & SavedStarGiftMsgIdHelper.MatchMsgId(u2.MsgId)).FirstOrDefaultAsync(),
                TInputSavedStarGiftSlug s2 => await savedCol2.Find(d => d.OwnerUserId == input.UserId && d.UniqueSlug == s2.Slug).FirstOrDefaultAsync(),
                TInputSavedStarGiftChat c2 => await savedCol2.Find(d => d.OwnerUserId == input.UserId && d.RandomId == c2.SavedId).FirstOrDefaultAsync(),
                _ => null
            };
            if (saved2 == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            var bal2 = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            if (bal2 < dropCost) RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

            // Drop-original-details is modelled as a self-transfer: the gift stays
            // owned by the same user, but the original sender is replaced with self
            // and the inscription/comment are wiped. No service message is emitted,
            // matching the user-facing semantics of "removing the signature".
            await savedCol2.UpdateOneAsync(
                Builders<SavedStarGiftDocument>.Filter.Eq(d => d.Id, saved2!.Id),
                Builders<SavedStarGiftDocument>.Update
                    .Set(d => d.MessageText, null)
                    .Set(d => d.MessageEntities, null)
                    .Set(d => d.FromUserId, input.UserId)
                    .Set(d => d.NameHidden, true)
                    .Set(d => d.Date, DateTime.UtcNow.ToTimestamp()));

            // For unique gifts also update unique-star-gifts
            if (saved2!.IsUnique && saved2.UniqueSlug != null)
            {
                await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
                    .UpdateOneAsync(d => d.Slug == saved2.UniqueSlug,
                        Builders<UniqueStarGiftDocument>.Update
                            .Set(d => d.NameHidden, true)
                            .Set(d => d.OriginalDetailsDropped, true)
                            .Set(d => d.MessageText, null)
                            .Set(d => d.MessageEntities, null)
                            .Set(d => d.FromUserId, input.UserId));
            }

            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -dropCost);
            // Tag with stargiftDropOriginalDetails and slug so the wallet entry
            // shows the dedicated layer-223 transaction kind, and clients can
            // hydrate the embedded gift visual from the slug.
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -dropCost,
                title: "Remove original details", stargiftDropOriginalDetails: true,
                stargiftSlug: saved2!.UniqueSlug, gift: true);

            return new TPaymentResult { Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 } };
        }

        if (obj.Invoice is TInputInvoiceStarGiftResale resaleInvoice)
        {
            var uniqueCol = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");
            var uniqueDoc = await uniqueCol.Find(d => d.Slug == resaleInvoice.Slug).FirstOrDefaultAsync();
            if (uniqueDoc == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            // Layer 206: currency is selected by the `ton` flag on the invoice or
            // forced to TON when the gift is flagged resale_ton_only.
            bool useTon = resaleInvoice.Ton || uniqueDoc!.ResaleTonOnly;
            long price = useTon ? uniqueDoc!.ResellTon : uniqueDoc!.ResellStars;
            if (price <= 0) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            long buyerBalance = useTon
                ? await TonBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId)
                : await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            if (buyerBalance < price) RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

            var toPeer2 = peerHelper.GetPeer(resaleInvoice.ToId, input.UserId)!;
            var newOwnerUserId = toPeer2.PeerType == PeerType.User ? toPeer2.PeerId : 0L;
            // If ToId resolves to 0 (e.g. inputPeerSelf not resolved), fallback to buyer
            if (newOwnerUserId == 0 && toPeer2.PeerType != PeerType.Channel) newOwnerUserId = input.UserId;
            var newOwnerChannelId = toPeer2.PeerType == PeerType.Channel ? toPeer2.PeerId : 0L;
            var sellerUserId = uniqueDoc.OwnerUserId;

            // Prevent buying your own gift
            if (sellerUserId == input.UserId || (newOwnerUserId > 0 && newOwnerUserId == sellerUserId))
                RpcErrors.RpcErrors400.StargiftOwnerInvalid.ThrowRpcError();

            // Cannot send NFT to bots / system users.
            if (newOwnerUserId > 0 && newOwnerUserId != input.UserId)
            {
                var queryProcessorR = services.GetRequiredService<IQueryProcessor>();
                await PeerKindHelper.EnsureNotBotOrSystemAsync(queryProcessorR, newOwnerUserId);
            }

            // Debit buyer + credit seller (85 % after fee, same as Telegram).
            long sellerReceives = (long)Math.Floor(price * 0.85);
            if (useTon)
            {
                await TonBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -price);
                await TonBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -price, gift: true, title: $"Buy {uniqueDoc.Title} #{uniqueDoc.Num}");
                await TonBalanceHelper.AddBalanceAsync(mongoDatabase, sellerUserId, sellerReceives);
                await TonBalanceHelper.AddTransactionAsync(mongoDatabase, sellerUserId, sellerReceives, gift: true, title: $"Sold {uniqueDoc.Title} #{uniqueDoc.Num}");
            }
            else
            {
                // Tag both legs with stargiftResale:true so the buyer/seller see
                // the resale-specific row in their Stars wallet history.
                await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -price);
                await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -price, gift: true, stargiftResale: true, title: $"Buy {uniqueDoc.Title} #{uniqueDoc.Num}", stargiftSlug: uniqueDoc.Slug);
                await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, sellerUserId, sellerReceives);
                await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, sellerUserId, sellerReceives, gift: true, stargiftResale: true, title: $"Sold {uniqueDoc.Title} #{uniqueDoc.Num}", stargiftSlug: uniqueDoc.Slug);
            }

            // Transfer ownership. Both currency-specific listings are cleared and
            // the initial-sale / last-resale columns are refreshed for the matching
            // currency so future stats show the last paid amount.
            var ownershipUpdate = Builders<UniqueStarGiftDocument>.Update
                .Set(d => d.OwnerUserId, newOwnerUserId)
                .Set(d => d.OwnerChannelId, newOwnerChannelId)
                .Set(d => d.ResellStars, 0L)
                .Set(d => d.ResellTon, 0L);
            ownershipUpdate = useTon
                ? ownershipUpdate.Set(d => d.LastResaleTon, price)
                : ownershipUpdate.Set(d => d.LastResaleStars, price).Set(d => d.InitialSaleStars, price);
            await uniqueCol.UpdateOneAsync(d => d.UniqueId == uniqueDoc.UniqueId, ownershipUpdate);

            var savedCol3 = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
            await savedCol3.DeleteOneAsync(d => d.IsUnique && d.UniqueSlug == uniqueDoc.Slug);
            await savedCol3.InsertOneAsync(new SavedStarGiftDocument
            {
                OwnerUserId = newOwnerUserId,
                OwnerChannelId = newOwnerChannelId,
                FromUserId = sellerUserId,
                GiftId = uniqueDoc.GiftId,
                Stars = 0,
                IsUnique = true,
                UniqueSlug = uniqueDoc.Slug,
                NameHidden = uniqueDoc.NameHidden,
                MessageText = uniqueDoc.MessageText,
                MessageEntities = uniqueDoc.MessageEntities,
                RandomId = uniqueDoc.UniqueId,
                Saved = true,
                Date = DateTime.UtcNow.ToTimestamp(),
                DocumentId = uniqueDoc.DocumentId,
                DocumentAccessHash = uniqueDoc.DocumentAccessHash,
                FileReference = uniqueDoc.FileReference,
                DocumentDate = uniqueDoc.DocumentDate,
                MimeType = uniqueDoc.MimeType,
                DocumentSize = uniqueDoc.DocumentSize,
                DcId = uniqueDoc.DcId,
            });

            // Notify seller via a structured service action so the client can localise it
            // (previously this was sent as hard-coded English text).
            uniqueDoc.OwnerUserId = newOwnerUserId;
            uniqueDoc.OwnerChannelId = newOwnerChannelId;
            uniqueDoc.ResellStars = 0;
            uniqueDoc.ResellTon = 0;
            uniqueDoc.ResaleTonOnly = false;
            var sellerUniqueTl = UniqueStarGiftHelper.ToTl(uniqueDoc);
            IStarsAmount sellerReceivedAmount = useTon
                ? new TStarsTonAmount { Amount = sellerReceives }
                : new TStarsAmount { Amount = sellerReceives, Nanos = 0 };
            IPeer sellerActionPeer = newOwnerUserId > 0
                ? new TPeerUser { UserId = newOwnerUserId }
                : new TPeerChannel { ChannelId = newOwnerChannelId };
            var sellerAction = new TMessageActionStarGiftUnique
            {
                Gift = sellerUniqueTl,
                Transferred = true,
                Saved = false,
                FromId = new TPeerUser { UserId = input.UserId },
                Peer = sellerActionPeer,
                ResaleAmount = sellerReceivedAmount,
            };
            await messageAppService.SendMessageAsync([new SendMessageInput(
                RequestInfo.Empty with { UserId = MyTelegramConsts.NotificationServiceUserId, Layer = MyTelegramConsts.Layer, Date = DateTime.UtcNow.ToTimestamp(), RequestId = Guid.NewGuid() },
                MyTelegramConsts.NotificationServiceUserId,
                new Peer(PeerType.User, sellerUserId),
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: sellerAction
            )]);

            // Send gift action to buyer. Layer 206: the resale price is surfaced to
            // the client via messageActionStarGiftUnique.resale_amount so the
            // buyer/seller chat UI can show "sold for N TON" or "sold for N ⭐️".
            if (newOwnerUserId > 0)
            {
                var uniqueTl2 = UniqueStarGiftHelper.ToTl(uniqueDoc);
                IStarsAmount resaleAmount = useTon
                    ? new TStarsTonAmount { Amount = price }
                    : new TStarsAmount { Amount = price, Nanos = 0 };
                var action2 = new TMessageActionStarGiftUnique
                {
                    Gift = uniqueTl2,
                    Transferred = true,
                    Saved = true,
                    FromId = new TPeerUser { UserId = sellerUserId },
                    Peer = new TPeerUser { UserId = newOwnerUserId },
                    ResaleAmount = resaleAmount,
                };
                await messageAppService.SendMessageAsync([new SendMessageInput(
                    RequestInfo.Empty with { UserId = MyTelegramConsts.NotificationServiceUserId, Layer = MyTelegramConsts.Layer, Date = DateTime.UtcNow.ToTimestamp(), RequestId = Guid.NewGuid() },
                    MyTelegramConsts.NotificationServiceUserId,
                    new Peer(PeerType.User, newOwnerUserId),
                    string.Empty,
                    Random.Shared.NextInt64(),
                    sendMessageType: SendMessageType.MessageService,
                    messageType: MessageType.Text,
                    messageAction: action2
                )]);
            }

            // Push balance updates to both parties.
            if (useTon)
            {
                await BalancePushHelper.PushTonBalanceAsync(objectMessageSender, mongoDatabase, input.UserId);
                await BalancePushHelper.PushTonBalanceAsync(objectMessageSender, mongoDatabase, sellerUserId);
            }
            else
            {
                await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, input.UserId);
                await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, sellerUserId);
            }

            return new TPaymentResult { Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 } };
        }

        // Nothing above matched, so this is an invoice type this server cannot settle. Report it as an
        // RPC error rather than letting an internal exception reach the client.
        if (obj.Invoice is not TInputInvoiceStarGift starGiftInvoice)
        {
            RpcErrors.RpcErrors400.BotInvoiceInvalid.ThrowRpcError();
            return null!;
        }

        var collection = mongoDatabase.GetCollection<StarGiftDocument>("star-gifts");
        var gift = await collection.Find(d => d.GiftId == starGiftInvoice.GiftId).FirstOrDefaultAsync();
        if (gift == null)
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        // Check and deduct balance
        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
        if (balance < gift!.Stars)
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

        if (gift!.Limited)
        {
            var updated = await collection.FindOneAndUpdateAsync(
                Builders<StarGiftDocument>.Filter.And(
                    Builders<StarGiftDocument>.Filter.Eq(d => d.GiftId, starGiftInvoice.GiftId),
                    Builders<StarGiftDocument>.Filter.Gt(d => d.AvailabilityRemains, 0)
                ),
                Builders<StarGiftDocument>.Update.Inc(d => d.AvailabilityRemains, -1),
                new FindOneAndUpdateOptions<StarGiftDocument> { ReturnDocument = ReturnDocument.After }
            );
            if (updated == null)
                RpcErrors.RpcErrors400.StargiftUsageLimited.ThrowRpcError();
            gift = updated;
            var now = DateTime.UtcNow.ToTimestamp();
            // Set FirstSaleDate only if not yet set, always update LastSaleDate
            await collection.UpdateOneAsync(
                Builders<StarGiftDocument>.Filter.And(
                    Builders<StarGiftDocument>.Filter.Eq(d => d.GiftId, gift!.GiftId),
                    Builders<StarGiftDocument>.Filter.Eq(d => d.FirstSaleDate, null)
                ),
                Builders<StarGiftDocument>.Update.Set(d => d.FirstSaleDate, now)
            );
            var soldOut = gift!.AvailabilityRemains == 0;
            await collection.UpdateOneAsync(
                Builders<StarGiftDocument>.Filter.Eq(d => d.GiftId, gift.GiftId),
                Builders<StarGiftDocument>.Update.Set(d => d.LastSaleDate, now).Set(d => d.SoldOut, soldOut)
            );
        }

        var toPeer = peerHelper.GetPeer(starGiftInvoice.Peer, input.UserId)!;
        var isChannel = toPeer.PeerType == PeerType.Channel;

        // Only allow gifts to users and broadcast channels, not groups
        if (isChannel)
        {
            var channelReadModel = await mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel")
                .Find(new BsonDocument("ChannelId", toPeer.PeerId))
                .Project(new BsonDocument { { "Broadcast", 1 } })
                .FirstOrDefaultAsync();
            if (channelReadModel == null || !channelReadModel.GetValue("Broadcast", false).AsBoolean)
                RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
        }
        else if (toPeer.PeerId != input.UserId)
        {
            // Cannot gift to bots / system users.
            var queryProcessorG = services.GetRequiredService<IQueryProcessor>();
            await PeerKindHelper.EnsureNotBotOrSystemAsync(queryProcessorG, toPeer.PeerId);

            // Item 15: forbid attaching a comment to a star gift if the recipient charges
            // for incoming messages (NoncontactPeersPaidStars > 0). Without this guard a
            // sender could bypass the paid-message price by tucking text into the gift.
            var hasComment = !starGiftInvoice.HideName && !string.IsNullOrEmpty((starGiftInvoice.Message as TTextWithEntities)?.Text);
            if (hasComment)
            {
                var privacyAppService = services.GetRequiredService<IPrivacyAppService>();
                var targetGps = await privacyAppService.GetGlobalPrivacySettingsAsync(toPeer.PeerId);
                if ((targetGps?.NoncontactPeersPaidStars ?? 0) > 0)
                {
                    throw new RpcException(new RpcError(400, "STARGIFT_MESSAGE_NOT_ALLOWED"));
                }
            }
        }

        var randomId = Random.Shared.NextInt64();
        var prepaidUpgradeHash = starGiftInvoice.IncludeUpgrade && gift.UpgradeStars.HasValue
            ? Guid.NewGuid().ToString("N")
            : null;

        var starGift = new TStarGift
        {
            Id = gift!.GiftId,
            Stars = gift.Stars,
            ConvertStars = gift.ConvertStars,
            UpgradeStars = gift.UpgradeStars,
            Sticker = new TDocument
            {
                Id = gift.DocumentId,
                AccessHash = gift.DocumentAccessHash,
                FileReference = gift.FileReference,
                Date = gift.DocumentDate,
                MimeType = gift.MimeType,
                Size = gift.DocumentSize,
                DcId = gift.DcId,
                Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }, new TDocumentAttributeImageSize { W = 512, H = 512 }, new TDocumentAttributeFilename { FileName = "AnimatedSticker.tgs" }],
            },
        };

        var messageAction = new TMessageActionStarGift
        {
            Gift = starGift,
            NameHidden = starGiftInvoice.HideName,
            CanUpgrade = gift.UpgradeStars.HasValue && prepaidUpgradeHash == null,
            ConvertStars = gift.ConvertStars,
            // Android treats upgrade_stars > 0 as "the upgrade is already paid":
            // it adds the amount to the displayed gift value and shows the
            // "free upgrade" copy. Regular gifts must therefore expose zero here.
            UpgradeStars = prepaidUpgradeHash != null ? gift.UpgradeStars : null,
            Message = starGiftInvoice.HideName ? null : starGiftInvoice.Message,
            Peer = isChannel ? new TPeerChannel { ChannelId = toPeer.PeerId } : null,
            SavedId = isChannel ? 0L : null,
            FromId = starGiftInvoice.HideName ? null : new TPeerUser { UserId = input.UserId },
            PrepaidUpgrade = prepaidUpgradeHash != null,
            PrepaidUpgradeHash = prepaidUpgradeHash,
        };

        if (isChannel)
        {
            // Notify each channel admin via private message from 777000
            var channelAdmins = await mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel")
                .Find(new BsonDocument("ChannelId", toPeer.PeerId))
                .Project(new BsonDocument("AdminList", 1))
                .FirstOrDefaultAsync();

            var adminUserIds = channelAdmins?["AdminList"]?.AsBsonArray
                .Select(a => a["UserId"].AsInt64)
                .Distinct()
                .ToList() ?? [];

            var notifyTasks = adminUserIds.Select(adminId => messageAppService.SendMessageAsync([new SendMessageInput(
                RequestInfo.Empty with
                {
                    UserId = MyTelegramConsts.NotificationServiceUserId,
                    Layer = MyTelegramConsts.Layer,
                    Date = DateTime.UtcNow.ToTimestamp(),
                    RequestId = Guid.NewGuid()
                },
                MyTelegramConsts.NotificationServiceUserId,
                new Peer(PeerType.User, adminId),
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: messageAction
            )]));
            await Task.WhenAll(notifyTasks);
        }
        else
        {
            var sendMessageInput = new SendMessageInput(
                input.ToRequestInfo() with { ReqMsgId = 0, PermAuthKeyId = 0 },
                input.UserId,
                toPeer,
                string.Empty,
                randomId,
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: messageAction
            );
            await messageAppService.SendMessageAsync([sendMessageInput]);
        }

        var savedGifts = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        await savedGifts.InsertOneAsync(new SavedStarGiftDocument
        {
            Id = ObjectId.GenerateNewId(),
            OwnerUserId = isChannel ? 0 : toPeer.PeerId,
            OwnerChannelId = isChannel ? toPeer.PeerId : 0,
            FromUserId = input.UserId,
            GiftId = gift.GiftId,
            Stars = gift.Stars,
            ConvertStars = gift.ConvertStars,
            UpgradeStars = gift.UpgradeStars,
            NameHidden = starGiftInvoice.HideName,
            Saved = true,
            Date = DateTime.UtcNow.ToTimestamp(),
            MessageText = starGiftInvoice.HideName ? null : starGiftInvoice.Message?.Text,
            // Item 14: persist entities so animated/custom emoji in the gift comment survive a round-trip.
            MessageEntities = starGiftInvoice.HideName ? null : (starGiftInvoice.Message as TTextWithEntities)?.Entities,
            RandomId = randomId,
            DocumentId = gift.DocumentId,
            DocumentAccessHash = gift.DocumentAccessHash,
            FileReference = gift.FileReference,
            DocumentDate = gift.DocumentDate,
            MimeType = gift.MimeType,
            DocumentSize = gift.DocumentSize,
            DcId = gift.DcId,
        });

        // Deduct stars from sender, add transaction
        var totalCost = gift!.Stars + (starGiftInvoice.IncludeUpgrade && gift.UpgradeStars.HasValue ? gift.UpgradeStars.Value : 0);
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -totalCost);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -totalCost, gift: true,
            peerUserId: isChannel ? null : toPeer.PeerId,
            peerChannelId: isChannel ? toPeer.PeerId : null);

        if (prepaidUpgradeHash != null)
        {
            await mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts")
                .UpdateOneAsync(d => d.RandomId == randomId,
                    Builders<SavedStarGiftDocument>.Update
                        .Set(d => d.PrepaidUpgrade, true)
                        .Set(d => d.PrepaidUpgradeHash, prepaidUpgradeHash));
        }

        return new TPaymentResult { Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 } };
    }


    private async Task<bool> IsPaidMediaInvoiceAsync(IRequestInput input, TInputInvoiceMessage invoiceMessage)
    {
        return await PaidMediaHelper.ResolveInvoiceAsync(mongoDatabase, peerHelper, input, invoiceMessage) != null;
    }

    private async Task<IPaymentResult> HandlePaidMediaPaymentAsync(IRequestInput input, TInputInvoiceMessage invoiceMessage)
    {
        var context = await PaidMediaHelper.ResolveInvoiceAsync(mongoDatabase, peerHelper, input, invoiceMessage);
        if (context == null)
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();

        var doc = context.Document;
        var revealed = PaidMediaHelper.ToRevealedExtendedMedia(doc.ExtendedMedia);
        if (!PaidMediaHelper.IsPurchasedBy(doc, input.UserId))
        {
            var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            if (balance < doc.StarsAmount)
                RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -doc.StarsAmount);
            await PaidMediaHelper.MarkPurchasedAsync(mongoDatabase, doc, input.UserId);

            var queryProcessor = services.GetRequiredService<IQueryProcessor>();
            var txMedia = PaidMediaHelper.ToTransactionExtendedMedia(doc.ExtendedMedia);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -doc.StarsAmount,
                peerChannelId: doc.ChannelId, title: "Paid media", msgId: doc.MsgId, extendedMedia: txMedia);

            var sender = await queryProcessor.ProcessAsync(new GetUserByIdQuery(doc.SenderUserId));
            if (sender?.Bot == true)
            {
                await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, doc.SenderUserId, doc.StarsAmount);
                await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, doc.SenderUserId, doc.StarsAmount,
                    peerChannelId: doc.ChannelId, title: "Paid media", msgId: doc.MsgId, extendedMedia: txMedia);
                await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, doc.SenderUserId);
            }
            else
            {
                await ChannelRevenueHelper.CreditAsync(mongoDatabase, doc.ChannelId, ChannelRevenueHelper.StarsCurrency,
                    doc.StarsAmount, sourceUserId: input.UserId, title: "Paid media", msgId: doc.MsgId, extendedMedia: txMedia);

                var channel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(doc.ChannelId));
                if (channel != null)
                {
                    await BalancePushHelper.PushChannelRevenueStatusAsync(objectMessageSender, mongoDatabase,
                        doc.ChannelId, PeerType.Channel, channel.CreatorId, ChannelRevenueHelper.StarsCurrency);
                }
            }
            await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, input.UserId);

            var update = new TUpdateMessageExtendedMedia
            {
                Peer = context.DisplayPeer.ToPeer(),
                MsgId = context.DisplayMsgId,
                ExtendedMedia = revealed
            };
            await objectMessageSender.PushMessageToPeerAsync(input.UserId.ToUserPeer(), new TUpdates
            {
                Updates = new TVector<IUpdate> { update },
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = DateTime.UtcNow.ToTimestamp(),
                Seq = 0
            }, pts: 0);

            if (!string.IsNullOrEmpty(doc.Payload) && sender?.Bot == true)
            {
                var qts = await PaidMediaHelper.NextQtsAsync(mongoDatabase, doc.SenderUserId);
                await objectMessageSender.PushMessageToPeerAsync(doc.SenderUserId.ToUserPeer(), new TUpdates
                {
                    Updates = new TVector<IUpdate>
                    {
                        new TUpdateBotPurchasedPaidMedia { UserId = input.UserId, Payload = doc.Payload!, Qts = qts }
                    },
                    Users = new TVector<IUser>(),
                    Chats = new TVector<IChat>(),
                    Date = DateTime.UtcNow.ToTimestamp(),
                    Seq = 0
                }, pts: 0);
            }
        }

        return new TPaymentResult
        {
            Updates = new TUpdates
            {
                Updates = new TVector<IUpdate>
                {
                    new TUpdateMessageExtendedMedia
                    {
                        Peer = context.DisplayPeer.ToPeer(),
                        MsgId = context.DisplayMsgId,
                        ExtendedMedia = revealed
                    }
                },
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = DateTime.UtcNow.ToTimestamp(),
                Seq = 0
            }
        };
    }

    /// <summary>
    /// Buys one period of a channel's Telegram Star subscription through a paid invite link, then
    /// joins the buyer to the channel.
    /// See https://corefork.telegram.org/api/invites#paid-invite-links
    /// </summary>
    private async Task<IPaymentResult> HandleChatInviteSubscriptionAsync(IRequestInput input, TInputInvoiceChatInviteSubscription invoice)
    {
        if (string.IsNullOrEmpty(invoice.Hash))
        {
            RpcErrors.RpcErrors400.InviteHashEmpty.ThrowRpcError();
        }

        var chatInviteReadModel = await queryProcessor.ProcessAsync(new GetChatInviteByLinkQuery(invoice.Hash));
        if (chatInviteReadModel == null || chatInviteReadModel.Revoked)
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        if (chatInviteReadModel!.SubscriptionPricingAmount is not > 0 || chatInviteReadModel.SubscriptionPricingPeriod is not > 0)
        {
            RpcErrors.RpcErrors400.PricingChatInvalid.ThrowRpcError();
        }

        var channelReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(chatInviteReadModel.PeerId));
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.ChannelIdInvalid.ThrowRpcError();
        }

        var channelMember = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(chatInviteReadModel.PeerId, input.UserId));
        if (channelMember is { Left: false, Kicked: false })
        {
            RpcErrors.RpcErrors400.UserAlreadyParticipant.ThrowRpcError();
        }

        if (channelMember is { Kicked: true })
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        // An unexpired subscription may be re-fulfilled without paying again.
        var subscription = await starsSubscriptionService.GetActiveSubscriptionAsync(input.UserId, chatInviteReadModel.PeerId);
        subscription ??= await starsSubscriptionService.ChargeAsync(input.UserId,
            chatInviteReadModel.PeerId,
            chatInviteReadModel.Link,
            chatInviteReadModel.SubscriptionPricingPeriod!.Value,
            chatInviteReadModel.SubscriptionPricingAmount!.Value,
            channelReadModel!.Title);

        if (subscription == null)
        {
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
        }

        // The join itself runs through the regular event-sourced import path, so membership,
        // dialogs and update pushes all behave exactly as for a free invite link.
        var command = new ImportChatInviteCommand(
            ChatInviteId.Create(chatInviteReadModel.PeerId, chatInviteReadModel.InviteId),
            input.ToRequestInfo(),
            ChatInviteRequestState.NoApprovalRequired,
            DateTime.UtcNow.ToTimestamp(),
            subscription!.UntilDate);
        await commandBus.PublishAsync(command);

        return new TPaymentResult
        {
            Updates = new TUpdates
            {
                Updates = new TVector<IUpdate>(),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = DateTime.UtcNow.ToTimestamp(),
                Seq = 0
            }
        };
    }

    private async Task ActivatePremiumAsync(long userId, int months)
    {
        var userCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");

        // Check if user already has Premium
        var userFilter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var user = await userCol.Find(userFilter).FirstOrDefaultAsync();

        if (user == null)
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();

        bool alreadyHasPremium = user.Contains("Premium") && user["Premium"].AsBoolean;

        // Set Premium flag
        var update = Builders<BsonDocument>.Update.Set("Premium", true);
        await userCol.UpdateOneAsync(userFilter, update);

        // Create/update premium subscription
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiresAt = now + (months * 30 * 24 * 3600);

        var subCol = mongoDatabase.GetCollection<BsonDocument>("premium_subscriptions");
        await subCol.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("UserId", userId),
            new BsonDocument
            {
                ["UserId"] = userId,
                ["Months"] = months,
                ["ActivatedAt"] = now,
                ["ExpiresAt"] = expiresAt,
                ["Currency"] = "XTR"
            },
            new ReplaceOptions { IsUpsert = true });

        // Grant 4 boosts ONLY if user didn't have Premium before
        if (!alreadyHasPremium)
        {
            var boostCol = mongoDatabase.GetCollection<BsonDocument>("channel_boosts");
            var expires = now + (86400 * 365); // 1 year

            // Find next available slot
            var existingBoosts = await boostCol.Find(Builders<BsonDocument>.Filter.Eq("UserId", userId))
                .ToListAsync();

            int nextSlot = 1;
            if (existingBoosts.Count > 0)
            {
                var usedSlots = existingBoosts.Select(b => b["Slot"].AsInt32).ToHashSet();
                while (usedSlots.Contains(nextSlot))
                    nextSlot++;
            }

            // Add 4 boosts
            for (int i = 0; i < 4; i++)
            {
                await boostCol.InsertOneAsync(new BsonDocument
                {
                    ["UserId"] = userId,
                    ["Slot"] = nextSlot + i,
                    ["ChannelId"] = 0L,
                    ["Date"] = now,
                    ["Expires"] = expires
                });
            }
        }
    }
}
