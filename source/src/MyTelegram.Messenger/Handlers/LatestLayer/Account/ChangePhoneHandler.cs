using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Change the phone number of the current account
/// Possible errors
/// Code Type Description
/// 400 PHONE_CODE_EMPTY phone_code is missing.
/// 400 PHONE_CODE_EXPIRED The phone code you provided has expired.
/// 400 PHONE_CODE_INVALID The provided phone code is invalid.
/// 406 PHONE_NUMBER_INVALID The phone number is invalid.
/// 400 PHONE_NUMBER_OCCUPIED The phone number is already in use.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.changePhone"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ChangePhoneHandler(
    IQueryProcessor queryProcessor,
    IMongoDatabase mongoDatabase,
    IUserAppService userAppService,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestChangePhone, MyTelegram.Schema.IUser>
{
    protected override async Task<MyTelegram.Schema.IUser> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestChangePhone obj)
    {
        var phoneNumber = obj.PhoneNumber.ToPhoneNumber();
        if (!long.TryParse(phoneNumber, out _))
            RpcErrors.RpcErrors400.PhoneNumberInvalid.ThrowRpcError();
        if (string.IsNullOrWhiteSpace(obj.PhoneCode))
            RpcErrors.RpcErrors400.PhoneCodeEmpty.ThrowRpcError();

        var existing = await queryProcessor.ProcessAsync(new GetUserByPhoneNumberQuery(phoneNumber));
        if (existing != null && existing.UserId != input.UserId)
            RpcErrors.RpcErrors400.PhoneNumberOccupied.ThrowRpcError();

        var appCode = await queryProcessor.ProcessAsync(new GetLatestAppCodeQuery(phoneNumber, obj.PhoneCodeHash));
        if (appCode == null || appCode.Expire < DateTime.UtcNow.ToTimestamp())
            RpcErrors.RpcErrors400.PhoneCodeExpired.ThrowRpcError();
        if (!string.Equals(appCode.Code, obj.PhoneCode, StringComparison.OrdinalIgnoreCase))
            RpcErrors.RpcErrors400.PhoneCodeInvalid.ThrowRpcError();

        await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                Builders<BsonDocument>.Update.Set("PhoneNumber", phoneNumber));
        if (phoneNumber.StartsWith("888"))
        {
            try
            {
                await mongoDatabase.GetCollection<BsonDocument>("fragment_collectibles").InsertOneAsync(new BsonDocument
                {
                    ["_id"] = $"fragment-phone-{phoneNumber}",
                    ["type"] = "phone",
                    ["phone"] = phoneNumber,
                    ["purchase_date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["currency"] = "USD",
                    ["amount"] = 29900,
                    ["crypto_currency"] = "TON",
                    ["crypto_amount"] = 100000000000L,
                    ["url"] = $"https://fragment.com/number/{phoneNumber}"
                });
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        }


        var user = await userAppService.GetAsync(input.UserId);
        return userConverterService.ToUser(input, user!, layer: input.Layer);
    }
}
