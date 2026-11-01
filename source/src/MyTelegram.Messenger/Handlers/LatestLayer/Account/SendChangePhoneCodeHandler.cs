namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Verify a new phone number to associate to the current account
/// Possible errors
/// Code Type Description
/// 406 FRESH_CHANGE_PHONE_FORBIDDEN You can't change phone number right after logging in, please wait at least 24 hours.
/// 400 PHONE_NUMBER_BANNED The provided phone number is banned from telegram.
/// 406 PHONE_NUMBER_INVALID The phone number is invalid.
/// 400 PHONE_NUMBER_OCCUPIED The phone number is already in use.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.sendChangePhoneCode"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SendChangePhoneCodeHandler(
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IVerificationCodeGenerator verificationCodeGenerator,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    MongoDB.Driver.IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSendChangePhoneCode, MyTelegram.Schema.Auth.ISentCode>
{
    protected override async Task<MyTelegram.Schema.Auth.ISentCode> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestSendChangePhoneCode obj)
    {
        var phoneNumber = obj.PhoneNumber.ToPhoneNumber();
        if (!long.TryParse(phoneNumber, out _))
            RpcErrors.RpcErrors400.PhoneNumberInvalid.ThrowRpcError();

        var existing = await queryProcessor.ProcessAsync(new GetUserByPhoneNumberQuery(phoneNumber));
        if (existing != null)
            RpcErrors.RpcErrors400.PhoneNumberOccupied.ThrowRpcError();

        var allowed = await mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("allowed_phones")
            .CountDocumentsAsync(MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", phoneNumber));
        if (allowed == 0)
        {
            RpcErrors.RpcErrors400.PhoneNumberInvalid.ThrowRpcError();
        }

        var code = verificationCodeGenerator.Generate();
        var phoneCodeHash = Guid.NewGuid().ToString("N");
        await commandBus.PublishAsync(new CreateAppCodeCommand(
            AppCodeId.Create(phoneNumber, phoneCodeHash),
            input.ToRequestInfo(),
            input.UserId,
            phoneNumber,
            code,
            phoneCodeHash,
            DateTime.UtcNow.ToTimestamp()));

        return new TSentCode
        {
            Type = new TSentCodeTypeSms { Length = code.Length },
            PhoneCodeHash = phoneCodeHash,
            Timeout = options.CurrentValue.VerificationCodeExpirationSeconds,
        };
    }
}
