namespace MyTelegram.Messenger.Services.Impl;
public partial class AppConfigHelper : IAppConfigHelper, ISingletonDependency
{
    public AppConfigHelper()
    {
        GetAppConfig();
    }

    public int GetAppConfigHash()
    {
        return _hash;
    }

    private void AddCustomConfig()
    {
        SetConfig("stars_paid_messages_available", new TJsonBool { Value = true });
        SetConfig("stars_paid_reactions_available", new TJsonBool { Value = true });
        SetConfig("stars_paid_message_amount_max", new TJsonNumber { Value = 10000 });
        SetConfig("hidden_members_group_size_min", new TJsonNumber { Value = 10 });
        SetConfig("stargifts_blocked", new TJsonBool { Value = false });
        SetConfig("stargifts_craft_attribute_permilles", new TJsonArray { Value = [new TJsonArray { Value = [new TJsonNumber { Value = 100 }] }, new TJsonArray { Value = [new TJsonNumber { Value = 80 }, new TJsonNumber { Value = 400 }] }, new TJsonArray { Value = [new TJsonNumber { Value = 70 }, new TJsonNumber { Value = 350 }, new TJsonNumber { Value = 700 }] }, new TJsonArray { Value = [new TJsonNumber { Value = 60 }, new TJsonNumber { Value = 300 }, new TJsonNumber { Value = 600 }, new TJsonNumber { Value = 1000 }] }] });
        SetConfig("stars_gifts_enabled", new TJsonBool { Value = true });
        SetConfig("premium_gift_attach_menu_icon", new TJsonBool { Value = true });
        SetConfig("premium_gift_text_field_icon", new TJsonBool { Value = true });
        SetConfig("premium_purchase_blocked", new TJsonBool { Value = false });
        SetConfig("giveaway_gifts_purchase_available", new TJsonBool { Value = true });
        SetConfig("settings_display_passkeys", new TJsonBool { Value = true });
        SetConfig("passkeys_account_passkeys_max", new TJsonNumber { Value = 20 });
        SetConfig("factcheck_length_limit", new TJsonNumber { Value = 1024 });

        // GIF search runs through the built-in @gif inline bot. Only the legacy config#cc1a241e field
        // carried this before, and the generated appConfig mentions the key in a comment without ever
        // emitting it, so clients that read it from appConfig found nothing.
        // See https://corefork.telegram.org/api/gifs#searching-gifs
        SetConfig("gif_search_username", new TJsonString { Value = "gif" });

        // Affiliate Programs configuration
        SetConfig("starref_program_allowed", new TJsonBool { Value = true });
        SetConfig("starref_connect_allowed", new TJsonBool { Value = true });
        SetConfig("starref_min_commission_permille", new TJsonNumber { Value = 10 });
        SetConfig("starref_max_commission_permille", new TJsonNumber { Value = 500 });
    }

    private void SetConfig(string key, IJSONValue value)
    {
        var oldValue = _jsonValue?.Value.FirstOrDefault(p => p.Key == key);
        if (oldValue != null)
        {
            _jsonValue?.Value.Remove(oldValue);
        }
        _jsonValue?.Value.Add(new TJsonObjectValue
        {
            Key = key,
            Value = value
        });
    }
}
