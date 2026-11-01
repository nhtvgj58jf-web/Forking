namespace MyTelegram.Converters.Mappers.LatestLayer;

internal sealed class UserMapper
    : IObjectMapper<IUserReadModel, TUser>,
        ILayeredMapper,
        ITransientDependency
{
    public int Layer => Layers.LayerLatest;
    

    public TUser Map(IUserReadModel source)
    {
        return Map(source, new TUser());
    }

    public TUser Map(
        IUserReadModel source,
        TUser destination
    )
    {
        destination.Id = source.UserId;
        destination.Photo = new TUserProfilePhotoEmpty();
        destination.AccessHash = source.AccessHash;
        destination.Bot = source.Bot;

        // user.bot makes the constructor set the bot_info_version flag, and serializing it then
        // dereferences the value: a bot without one takes down every response it appears in. Only the
        // bots created through BotFather ever got the field, so it is defaulted here instead.
        destination.BotInfoVersion = source.BotInfoVersion ?? (source.Bot ? 1 : null);
        destination.Phone = source.PhoneNumber;
        destination.FirstName = source.FirstName;
        destination.LastName = source.LastName;
        destination.Fake = source.Fake && source.EmojiStatusDocumentId == null;
        destination.Scam = source.Scam && source.EmojiStatusDocumentId == null;
        destination.Verified = source.Verified;
        destination.Support = source.Support;
        destination.Premium = source.Premium;

        destination.Color = source.Color.ToPeerColor();
        destination.ProfileColor = source.ProfileColor.ToPeerColor();
        destination.ContactRequirePremium = source.GlobalPrivacySettings?.NewNoncontactPeersRequirePremium ?? false;
        if (source.GlobalPrivacySettings?.NoncontactPeersPaidStars > 0)
            destination.SendPaidMessagesStars = source.GlobalPrivacySettings.NoncontactPeersPaidStars;
        destination.BotHasMainApp = source.BotHasMainApp;
        destination.BotActiveUsers = source.BotActiveUsers;

        // Map Usernames to TVector<IUsername>
        if (source.Usernames != null && source.Usernames.Count > 0)
        {
            destination.Usernames = new TVector<IUsername>();
            foreach (var usernameInfo in source.Usernames)
            {
                destination.Usernames.Add(new TUsername
                {
                    Username = usernameInfo.Username,
                    Editable = usernameInfo.Editable,
                    Active = usernameInfo.Active
                });
            }

            // Set primary username (first active editable, or first active)
            var primary = source.Usernames.FirstOrDefault(u => u.Active && u.Editable)
                          ?? source.Usernames.FirstOrDefault(u => u.Active);
            if (primary != null)
            {
                destination.Username = primary.Username;
            }
        }
        else
        {
            // Fallback to legacy UserName field
            destination.Username = source.UserName;
        }

        // Read BotBusiness from MongoDB directly since it's not in IUserReadModel
        // This is a temporary solution until BotBusiness is added to the read model
        if (source.Bot)
        {
            // BotBusiness flag will be set by UserConverterService
            destination.BotBusiness = false; // Default value, will be overridden if needed
        }

        return destination;
    }
}
