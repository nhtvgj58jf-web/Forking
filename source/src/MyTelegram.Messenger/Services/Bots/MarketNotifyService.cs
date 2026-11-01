using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Passport;
using StackExchange.Redis;

namespace MyTelegram.Messenger.Services.Bots;

public class MarketNotifyService(
    IMessageAppService messageAppService,
    IMongoDatabase mongoDatabase) : Microsoft.Extensions.Hosting.BackgroundService
{
    private long _botUserId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("[MarketNotify] started");
        var col = mongoDatabase.GetCollection<BsonDocument>("market-notify");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (true)
                {
                    var job = await col.FindOneAndUpdateAsync(
                        new BsonDocument("status", "new"),
                        new BsonDocument("$set", new BsonDocument("status", "sending")),
                        cancellationToken: stoppingToken);
                    if (job == null)
                    {
                        break;
                    }

                    var ok = false;
                    try
                    {
                        if (_botUserId == 0)
                        {
                            var bot = await mongoDatabase.GetCollection<BsonDocument>("botfather-bot-state")
                                .Find(new BsonDocument("Username", "market_bot")).FirstOrDefaultAsync(stoppingToken);
                            if (bot != null)
                            {
                                _botUserId = bot["BotUserId"].ToInt64();
                            }
                        }

                        if (_botUserId != 0)
                        {
                            await SendAsync(job["to"].ToInt64(), job["text"].AsString);
                            ok = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MarketNotify] send error: {ex}");
                    }

                    await col.UpdateOneAsync(new BsonDocument("_id", job["_id"]),
                        new BsonDocument("$set", new BsonDocument("status", ok ? "sent" : "failed")),
                        cancellationToken: stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MarketNotify] error: {ex}");
            }

            try
            {
                await Task.Delay(3000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task SendAsync(long toUserId, string text)
    {
        var botReq = RequestInfo.Empty with
        {
            UserId = _botUserId, Layer = MyTelegramConsts.Layer,
            Date = DateTime.UtcNow.ToTimestamp(), RequestId = Guid.NewGuid()
        };
        var toPeer = new Peer(PeerType.User, toUserId);
        var sendInput = new SendMessageInput(botReq, _botUserId, toPeer, text,
            Random.Shared.NextInt64(), (TVector<IMessageEntity>?)null, null, false);
        await messageAppService.SendMessageAsync([sendInput]);
    }
}
