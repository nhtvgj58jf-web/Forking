using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;

namespace MyTelegram.Messenger.Services.Bots;

public interface IModerationBotService
{
    Task HandleAsync(IRequestInput input, Peer toPeer, string message, object? replyTo);
}

// Бот модерации и развлечений (в стиле Iris) для супергрупп. Работает только там, где бот добавлен участником.
public class ModerationBotService(
    IMessageAppService messageAppService,
    IMongoDatabase mongoDatabase,
    ICommandBus commandBus) : IModerationBotService, ISingletonDependency
{
    private const string BotUsername = "iris_bot";
    private const int WarnLimit = 3;
    private const int DefaultMuteMinutes = 60;

    private static readonly Dictionary<string, string> Aliases = new()
    {
        { "/стата", "/stats" }, { "/стат", "/stats" }, { "/я", "/me" }, { "/ктоя", "/me" }, { "/кто", "/me" },
        { "/брак", "/brak" }, { "/да", "/da" }, { "/нет", "/net" }, { "/развод", "/razvod" }, { "/браки", "/braki" },
        { "/монетка", "/coin" }, { "/кубик", "/dice" }
    };

    private static readonly HashSet<string> Commands = new()
    {
        "/ping", "/ban", "/unban", "/mute", "/unmute", "/warn", "/unwarn", "/warns",
        "/stats", "/me", "/brak", "/da", "/net", "/razvod", "/braki", "/coin", "/dice"
    };

    private static readonly int BanFlags = 1;
    private static readonly int MuteFlags = BuildMask(1, 2, 3, 4, 5, 6, 7, 8, 19, 20, 21, 22, 23, 24, 25);

    private static int BuildMask(params int[] bits)
    {
        var m = 0;
        foreach (var b in bits)
        {
            m |= 1 << b;
        }

        return m;
    }

    public async Task HandleAsync(IRequestInput input, Peer toPeer, string message, object? replyTo)
    {
        try
        {
            var parts = message.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return;
            }

            var cmd = parts[0].ToLowerInvariant();
            var at = cmd.IndexOf('@');
            if (at > 0)
            {
                var name = cmd.Substring(at + 1);
                cmd = cmd.Substring(0, at);
                if (name != BotUsername)
                {
                    return;
                }
            }

            if (Aliases.TryGetValue(cmd, out var canonical))
            {
                cmd = canonical;
            }

            if (!Commands.Contains(cmd))
            {
                return;
            }

            var chatId = toPeer.PeerId;
            var ch = await mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel")
                .Find(new BsonDocument("ChannelId", chatId)).FirstOrDefaultAsync();
            if (ch == null || Flag(ch, "Broadcast"))
            {
                return;
            }

            var creator = ch.Contains("CreatorId") && ch["CreatorId"].IsNumeric ? ch["CreatorId"].ToInt64() : 0L;
            var botId = await GetBotIdAsync();
            if (creator == 0 || botId == 0)
            {
                return;
            }

            var members = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelmemberreadmodel");
            var botMember = await members.Find(new BsonDocument { { "ChannelId", chatId }, { "UserId", botId } })
                .FirstOrDefaultAsync();
            if (botMember == null || Flag(botMember, "Left") || Flag(botMember, "Kicked"))
            {
                return;
            }

            if (cmd == "/ping")
            {
                await ReplyAsync(input, botId, chatId, "pong");
                return;
            }

            var sender = input.UserId;

            long target = 0;
            string? dur = null;
            foreach (var a in parts.Skip(1))
            {
                if (target == 0 && Regex.IsMatch(a, @"^\d{6,}$"))
                {
                    target = long.Parse(a);
                }
                else if (dur == null && Regex.IsMatch(a, @"^\d+[mhd]$", RegexOptions.IgnoreCase))
                {
                    dur = a.ToLowerInvariant();
                }
            }

            if (target == 0)
            {
                var replyId = GetReplyId(replyTo);
                if (replyId > 0)
                {
                    target = await SenderOfAsync(chatId, replyId);
                }
            }

            // ---- команды для всех участников ----
            switch (cmd)
            {
                case "/stats":
                    await StatsAsync(input, botId, chatId);
                    return;
                case "/me":
                    await MeAsync(input, botId, chatId, target == 0 ? sender : target, creator, members);
                    return;
                case "/braki":
                    await MarriagesListAsync(input, botId, chatId);
                    return;
                case "/coin":
                    await ReplyAsync(input, botId, chatId,
                        "Монетка: " + await NameOfAsync(sender) + " — " + (Random.Shared.Next(2) == 0 ? "орёл" : "решка"));
                    return;
                case "/dice":
                    await ReplyAsync(input, botId, chatId,
                        "Кубик: " + await NameOfAsync(sender) + " выбросил " + Random.Shared.Next(1, 7));
                    return;
                case "/brak":
                    await ProposeAsync(input, botId, chatId, sender, target);
                    return;
                case "/da":
                    await AcceptAsync(input, botId, chatId, sender);
                    return;
                case "/net":
                    await DeclineAsync(input, botId, chatId, sender);
                    return;
                case "/razvod":
                    await DivorceAsync(input, botId, chatId, sender);
                    return;
            }

            // ---- модерация ----
            var senderIsAdmin = sender == creator || await IsAdminAsync(members, chatId, sender);
            if (!senderIsAdmin && cmd != "/warns")
            {
                await ReplyAsync(input, botId, chatId, "Эта команда только для админов.");
                return;
            }

            if (target == 0)
            {
                if (cmd == "/warns")
                {
                    target = sender;
                }
                else
                {
                    await ReplyAsync(input, botId, chatId,
                        "Ответьте командой на сообщение человека или укажите его ID, например: " + cmd + " 2010005");
                    return;
                }
            }

            if (cmd != "/warns" && cmd != "/unban" && cmd != "/unmute" && cmd != "/unwarn")
            {
                if (target == sender || target == creator || target >= 600000000001L ||
                    await IsAdminAsync(members, chatId, target))
                {
                    await ReplyAsync(input, botId, chatId, "Этого пользователя трогать нельзя (админ, владелец или бот).");
                    return;
                }
            }

            switch (cmd)
            {
                case "/ban":
                    await EditAsync(input, chatId, creator, target, BanFlags, 0);
                    await ReplyAsync(input, botId, chatId, "Пользователь " + target + " забанен.");
                    break;
                case "/unban":
                    await EditAsync(input, chatId, creator, target, 0, 0);
                    await ReplyAsync(input, botId, chatId, "Пользователь " + target + " разбанен.");
                    break;
                case "/mute":
                {
                    var minutes = ParseMinutes(dur);
                    var until = (int)DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds();
                    await EditAsync(input, chatId, creator, target, MuteFlags, until);
                    await ReplyAsync(input, botId, chatId,
                        "Пользователь " + target + " в муте на " + minutes + " мин.");
                    break;
                }
                case "/unmute":
                    await EditAsync(input, chatId, creator, target, 0, 0);
                    await ReplyAsync(input, botId, chatId, "Пользователь " + target + " размучен.");
                    break;
                case "/warn":
                {
                    var count = await ChangeWarnsAsync(chatId, target, 1);
                    if (count >= WarnLimit)
                    {
                        await EditAsync(input, chatId, creator, target, BanFlags, 0);
                        await ChangeWarnsAsync(chatId, target, -count);
                        await ReplyAsync(input, botId, chatId,
                            "Пользователь " + target + ": " + WarnLimit + "/" + WarnLimit + " предупреждений. Забанен.");
                    }
                    else
                    {
                        await ReplyAsync(input, botId, chatId,
                            "Предупреждение пользователю " + target + ": " + count + "/" + WarnLimit);
                    }

                    break;
                }
                case "/unwarn":
                {
                    var count = await ChangeWarnsAsync(chatId, target, -1);
                    await ReplyAsync(input, botId, chatId,
                        "Предупреждение снято. У " + target + ": " + count + "/" + WarnLimit);
                    break;
                }
                case "/warns":
                {
                    var count = await ChangeWarnsAsync(chatId, target, 0);
                    await ReplyAsync(input, botId, chatId,
                        "У пользователя " + target + ": " + count + "/" + WarnLimit + " предупреждений.");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ModerationBot] error: " + ex);
        }
    }

    // ---------------- статистика и профиль ----------------

    private async Task StatsAsync(IRequestInput input, long botId, long chatId)
    {
        var col = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var match = new BsonDocument("OwnerPeerId", chatId);
        var total = await col.CountDocumentsAsync(match);
        var top = await col.Aggregate().Match(match)
            .Group(new BsonDocument { { "_id", "$SenderUserId" }, { "c", new BsonDocument("$sum", 1) } })
            .Sort(new BsonDocument("c", -1)).Limit(10).ToListAsync();
        var text = "Статистика чата. Всего сообщений: " + total + "\nТоп участников:";
        var n = 0;
        foreach (var d in top)
        {
            if (!d.Contains("_id") || !d["_id"].IsNumeric)
            {
                continue;
            }

            n++;
            text += "\n" + n + ". " + await NameOfAsync(d["_id"].ToInt64()) + " — " + d["c"].ToInt64();
        }

        await ReplyAsync(input, botId, chatId, text);
    }

    private async Task MeAsync(IRequestInput input, long botId, long chatId, long target, long creator,
        IMongoCollection<BsonDocument> members)
    {
        var col = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var cnt = await col.CountDocumentsAsync(new BsonDocument { { "OwnerPeerId", chatId }, { "SenderUserId", target } });
        var warns = await ChangeWarnsAsync(chatId, target, 0);
        var partner = await PartnerOfAsync(chatId, target);
        var role = target == creator ? "владелец" : await IsAdminAsync(members, chatId, target) ? "админ" : "участник";
        var text = "Профиль: " + await NameOfAsync(target) + "\nID: " + target + "\nРоль: " + role +
                   "\nСообщений в чате: " + cnt + "\nПредупреждений: " + warns + "/" + WarnLimit +
                   "\nБрак: " + (partner == 0 ? "нет" : await NameOfAsync(partner));
        await ReplyAsync(input, botId, chatId, text);
    }

    // ---------------- браки ----------------

    private async Task<long> PartnerOfAsync(long chatId, long userId)
    {
        var f = Builders<BsonDocument>.Filter;
        var m = await mongoDatabase.GetCollection<BsonDocument>("iris-marriages")
            .Find(f.And(f.Eq("ChatId", chatId), f.Or(f.Eq("A", userId), f.Eq("B", userId))))
            .FirstOrDefaultAsync();
        if (m == null)
        {
            return 0;
        }

        return m["A"].ToInt64() == userId ? m["B"].ToInt64() : m["A"].ToInt64();
    }

    private async Task ProposeAsync(IRequestInput input, long botId, long chatId, long sender, long target)
    {
        if (target == 0)
        {
            await ReplyAsync(input, botId, chatId, "Ответьте командой /брак на сообщение того, с кем хотите пожениться.");
            return;
        }

        if (target == sender || target >= 600000000001L)
        {
            await ReplyAsync(input, botId, chatId, "С этим пользователем пожениться нельзя.");
            return;
        }

        if (await PartnerOfAsync(chatId, sender) != 0 || await PartnerOfAsync(chatId, target) != 0)
        {
            await ReplyAsync(input, botId, chatId, "Кто-то из вас уже в браке.");
            return;
        }

        var col = mongoDatabase.GetCollection<BsonDocument>("iris-proposals");
        var id = chatId + ":" + target;
        await col.ReplaceOneAsync(new BsonDocument("_id", id),
            new BsonDocument { { "_id", id }, { "ChatId", chatId }, { "From", sender }, { "To", target } },
            new ReplaceOptions { IsUpsert = true });
        await ReplyAsync(input, botId, chatId,
            await NameOfAsync(sender) + " предлагает брак " + await NameOfAsync(target) +
            ". Ответ: /да или /нет (отправляет " + await NameOfAsync(target) + ").");
    }

    private async Task AcceptAsync(IRequestInput input, long botId, long chatId, long sender)
    {
        var col = mongoDatabase.GetCollection<BsonDocument>("iris-proposals");
        var id = chatId + ":" + sender;
        var p = await col.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync();
        if (p == null)
        {
            await ReplyAsync(input, botId, chatId, "Вам никто не предлагал брак.");
            return;
        }

        var from = p["From"].ToInt64();
        await col.DeleteOneAsync(new BsonDocument("_id", id));
        if (await PartnerOfAsync(chatId, sender) != 0 || await PartnerOfAsync(chatId, from) != 0)
        {
            await ReplyAsync(input, botId, chatId, "Кто-то из вас уже в браке.");
            return;
        }

        var a = Math.Min(from, sender);
        var b = Math.Max(from, sender);
        var mid = chatId + ":" + a + ":" + b;
        await mongoDatabase.GetCollection<BsonDocument>("iris-marriages").ReplaceOneAsync(
            new BsonDocument("_id", mid),
            new BsonDocument
            {
                { "_id", mid }, { "ChatId", chatId }, { "A", a }, { "B", b },
                { "Date", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
            },
            new ReplaceOptions { IsUpsert = true });
        await ReplyAsync(input, botId, chatId,
            "Поздравляем! " + await NameOfAsync(from) + " и " + await NameOfAsync(sender) + " теперь в браке.");
    }

    private async Task DeclineAsync(IRequestInput input, long botId, long chatId, long sender)
    {
        var col = mongoDatabase.GetCollection<BsonDocument>("iris-proposals");
        var r = await col.DeleteOneAsync(new BsonDocument("_id", chatId + ":" + sender));
        await ReplyAsync(input, botId, chatId,
            r.DeletedCount > 0 ? "Предложение отклонено." : "Вам никто не предлагал брак.");
    }

    private async Task DivorceAsync(IRequestInput input, long botId, long chatId, long sender)
    {
        var f = Builders<BsonDocument>.Filter;
        var r = await mongoDatabase.GetCollection<BsonDocument>("iris-marriages")
            .DeleteOneAsync(f.And(f.Eq("ChatId", chatId), f.Or(f.Eq("A", sender), f.Eq("B", sender))));
        await ReplyAsync(input, botId, chatId, r.DeletedCount > 0 ? "Брак расторгнут." : "Вы не состоите в браке.");
    }

    private async Task MarriagesListAsync(IRequestInput input, long botId, long chatId)
    {
        var list = await mongoDatabase.GetCollection<BsonDocument>("iris-marriages")
            .Find(new BsonDocument("ChatId", chatId)).Limit(15).ToListAsync();
        if (list.Count == 0)
        {
            await ReplyAsync(input, botId, chatId, "В этом чате пока нет браков.");
            return;
        }

        var text = "Браки чата:";
        foreach (var m in list)
        {
            text += "\n" + await NameOfAsync(m["A"].ToInt64()) + " + " + await NameOfAsync(m["B"].ToInt64());
        }

        await ReplyAsync(input, botId, chatId, text);
    }

    // ---------------- вспомогательное ----------------

    private async Task<string> NameOfAsync(long userId)
    {
        var u = await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(new BsonDocument("UserId", userId)).FirstOrDefaultAsync();
        if (u != null)
        {
            var fn = StrOf(u, "FirstName");
            var un = StrOf(u, "UserName");
            if (fn.Length > 0)
            {
                return fn;
            }

            if (un.Length > 0)
            {
                return "@" + un;
            }
        }

        return userId.ToString();
    }

    private static string StrOf(BsonDocument d, string name)
    {
        return d.Contains(name) && d[name].IsString ? d[name].AsString : string.Empty;
    }

    private static bool Flag(BsonDocument d, string name)
    {
        return d.Contains(name) && d[name].IsBoolean && d[name].AsBoolean;
    }

    private static int ParseMinutes(string? dur)
    {
        if (string.IsNullOrEmpty(dur))
        {
            return DefaultMuteMinutes;
        }

        var n = long.Parse(dur.Substring(0, dur.Length - 1));
        var unit = dur[dur.Length - 1];
        var minutes = unit == 'd' ? n * 1440 : unit == 'h' ? n * 60 : n;
        return (int)Math.Clamp(minutes, 1, 366L * 1440);
    }

    private static int GetReplyId(object? replyTo)
    {
        if (replyTo == null)
        {
            return 0;
        }

        var v = replyTo.GetType().GetProperty("ReplyToMsgId")?.GetValue(replyTo);
        return v is int i ? i : v is long l ? (int)l : 0;
    }

    private async Task<long> GetBotIdAsync()
    {
        var b = await mongoDatabase.GetCollection<BsonDocument>("botfather-bot-state")
            .Find(new BsonDocument("Username", BotUsername)).FirstOrDefaultAsync();
        return b != null && b.Contains("BotUserId") && b["BotUserId"].IsNumeric ? b["BotUserId"].ToInt64() : 0L;
    }

    private static async Task<bool> IsAdminAsync(IMongoCollection<BsonDocument> members, long chatId, long userId)
    {
        var m = await members.Find(new BsonDocument { { "ChannelId", chatId }, { "UserId", userId } })
            .FirstOrDefaultAsync();
        return m != null && Flag(m, "IsAdmin") && !Flag(m, "Left") && !Flag(m, "Kicked");
    }

    private async Task<long> SenderOfAsync(long chatId, int messageId)
    {
        var m = await mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel")
            .Find(new BsonDocument { { "OwnerPeerId", chatId }, { "MessageId", messageId } })
            .FirstOrDefaultAsync();
        return m != null && m.Contains("SenderUserId") && m["SenderUserId"].IsNumeric
            ? m["SenderUserId"].ToInt64()
            : 0L;
    }

    private async Task<int> ChangeWarnsAsync(long chatId, long userId, int delta)
    {
        var col = mongoDatabase.GetCollection<BsonDocument>("iris-warns");
        var id = chatId + ":" + userId;
        var doc = await col.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync();
        var cur = doc != null && doc.Contains("Count") && doc["Count"].IsNumeric ? doc["Count"].ToInt32() : 0;
        var now = Math.Max(0, cur + delta);
        if (delta != 0)
        {
            await col.ReplaceOneAsync(new BsonDocument("_id", id),
                new BsonDocument { { "_id", id }, { "ChatId", chatId }, { "UserId", userId }, { "Count", now } },
                new ReplaceOptions { IsUpsert = true });
        }

        return now;
    }

    private async Task EditAsync(IRequestInput input, long chatId, long creator, long target, int flags, int until)
    {
        var req = RequestInfo.Empty with
        {
            UserId = creator, Layer = input.Layer,
            Date = DateTime.UtcNow.ToTimestamp(), RequestId = Guid.NewGuid()
        };
        var rights = ChatBannedRights.FromValue(flags, until);
        var command = new EditBannedCommand(ChannelMemberId.Create(chatId, target), req, creator, chatId, target, rights);
        await commandBus.PublishAsync(command);
    }

    private async Task ReplyAsync(IRequestInput input, long botId, long chatId, string text)
    {
        var botReq = RequestInfo.Empty with
        {
            UserId = botId, Layer = input.Layer,
            Date = DateTime.UtcNow.ToTimestamp(), RequestId = Guid.NewGuid()
        };
        var toPeer = new Peer(PeerType.Channel, chatId);
        var sendInput = new SendMessageInput(botReq, botId, toPeer, text, Random.Shared.NextInt64(), null, null, false);
        await messageAppService.SendMessageAsync([sendInput]);
    }
}
