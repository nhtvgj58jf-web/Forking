using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Bots;

public interface IWebAppInitDataBuilder
{
    Task<string> AppendInitDataAsync(string url, long botId, long userId, long queryId);
}

public class WebAppInitDataBuilder(IMongoDatabase mongoDatabase) : IWebAppInitDataBuilder, ISingletonDependency
{
    public async Task<string> AppendInitDataAsync(string url, long botId, long userId, long queryId)
    {
        try
        {
            var botState = await mongoDatabase.GetCollection<BsonDocument>("botfather-bot-state")
                .Find(new BsonDocument("BotUserId", botId)).FirstOrDefaultAsync();
            if (botState == null || !botState.TryGetValue("Token", out var tokenValue) || !tokenValue.IsString)
            {
                return url;
            }

            var token = tokenValue.AsString;
            var userDoc = await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel")
                .Find(new BsonDocument("UserId", userId)).FirstOrDefaultAsync();

            var user = new Dictionary<string, object>
            {
                ["id"] = userId,
                ["first_name"] = Str(userDoc, "FirstName")
            };
            var username = Str(userDoc, "UserName");
            if (username.Length > 0)
            {
                user["username"] = username;
            }

            var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["auth_date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ["query_id"] = queryId.ToString(),
                ["user"] = System.Text.Json.JsonSerializer.Serialize(user)
            };

            var check = new List<string>();
            var pairs = new List<string>();
            foreach (var kv in fields)
            {
                check.Add($"{kv.Key}={kv.Value}");
                pairs.Add($"{kv.Key}={Uri.EscapeDataString(kv.Value)}");
            }

            var secret = System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("WebAppData"), System.Text.Encoding.UTF8.GetBytes(token));
            var hash = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
                secret, System.Text.Encoding.UTF8.GetBytes(string.Join("\n", check)))).ToLowerInvariant();
            var initData = string.Join("&", pairs) + "&hash=" + hash;

            var hashIndex = url.IndexOf('#');
            var baseUrl = hashIndex >= 0 ? url.Substring(0, hashIndex) : url;
            return baseUrl + "#tgWebAppData=" + Uri.EscapeDataString(initData) + "&tgWebAppVersion=8.0";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebAppInitData] error: {ex}");
            return url;
        }
    }

    private static string Str(BsonDocument? d, string key)
        => d != null && d.TryGetValue(key, out var v) && v.IsString ? v.AsString : "";
}
