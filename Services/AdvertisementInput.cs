using System.ComponentModel.DataAnnotations;
using WDWAPP.Data;

namespace WDWAPP.Services;

public static class AdvertisementLabels
{
    public static string Type(AdvertisementType type) => type switch {
        AdvertisementType.Sell => "Säljes", AdvertisementType.Buy => "Köpes",
        AdvertisementType.Trade => "Bytes", AdvertisementType.GiveAway => "Skänkes", _ => "Okänd"
    };
    public static string Contact(AdvertisementContactType type) => type switch {
        AdvertisementContactType.Email => "E-post", AdvertisementContactType.Phone => "Telefon",
        AdvertisementContactType.Sms => "Telefon – endast SMS", AdvertisementContactType.Messenger => "Facebook Messenger",
        AdvertisementContactType.Discord => "Discord", AdvertisementContactType.AtVenue => "I lokalen", _ => "Okänd"
    };
}

// Explicit form fields: ownership and publication/expiration dates cannot be bound from a request.
public sealed class AdvertisementInput : IValidatableObject
{
    [Required(ErrorMessage = "Ange en rubrik."), StringLength(160)] public string Title { get; set; } = "";
    [Required(ErrorMessage = "Ange en beskrivning."), StringLength(10000)] public string Description { get; set; } = "";
    [EnumDataType(typeof(AdvertisementType))] public AdvertisementType Type { get; set; }
    public bool EmailSelected { get; set; }
    public string? Email { get; set; }
    public bool PhoneSelected { get; set; }
    public string? Phone { get; set; }
    public bool SmsSelected { get; set; }
    public string? Sms { get; set; }
    public bool MessengerSelected { get; set; }
    public string? Messenger { get; set; }
    public bool DiscordSelected { get; set; }
    public string? Discord { get; set; }
    public bool AtVenueSelected { get; set; }
    public List<int> RemoveImageIds { get; set; } = [];
    public string Version { get; set; } = "";

    public List<AdvertisementContactMethod> Contacts()
    {
        var contacts = new List<AdvertisementContactMethod>();
        if (EmailSelected) contacts.Add(new() { Type = AdvertisementContactType.Email, Value = Email?.Trim() });
        if (PhoneSelected) contacts.Add(new() { Type = AdvertisementContactType.Phone, Value = Phone?.Trim() });
        if (SmsSelected) contacts.Add(new() { Type = AdvertisementContactType.Sms, Value = Sms?.Trim() });
        if (MessengerSelected) contacts.Add(new() { Type = AdvertisementContactType.Messenger, Value = Messenger?.Trim() });
        if (DiscordSelected) contacts.Add(new() { Type = AdvertisementContactType.Discord, Value = Discord?.Trim() });
        if (AtVenueSelected) contacts.Add(new() { Type = AdvertisementContactType.AtVenue });
        return contacts;
    }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var contacts = Contacts();
        if (contacts.Count == 0) yield return new("Välj minst ett kontaktsätt.");
        foreach (var contact in contacts.Where(c => c.Type != AdvertisementContactType.AtVenue))
        {
            var value = contact.Value;
            var label = AdvertisementLabels.Contact(contact.Type);
            if (string.IsNullOrWhiteSpace(value) || value.Length > 254 || value.Any(char.IsControl))
            { yield return new($"Ange uppgifter för {label} (högst 254 tecken)."); continue; }
            if (contact.Type == AdvertisementContactType.Email && !new EmailAddressAttribute().IsValid(value))
                yield return new("Ange en giltig e-postadress.");
            if (contact.Type is AdvertisementContactType.Phone or AdvertisementContactType.Sms
                && (value.Length > 50 || value.Count(char.IsDigit) < 3 || !new PhoneAttribute().IsValid(value)))
                yield return new($"Ange ett giltigt telefonnummer för {label}.");
        }
        if (RemoveImageIds is null || RemoveImageIds.Count > AdvertisementService.MaxImages || RemoveImageIds.Any(id => id <= 0))
            yield return new("Ogiltigt bildval.");
    }
    public static AdvertisementInput From(Advertisement ad)
    {
        var input = new AdvertisementInput { Title = ad.Title, Description = ad.Description, Type = ad.Type, Version = ad.Version };
        foreach (var contact in ad.Contacts)
            switch (contact.Type)
            {
                case AdvertisementContactType.Email: input.EmailSelected = true; input.Email = contact.Value; break;
                case AdvertisementContactType.Phone: input.PhoneSelected = true; input.Phone = contact.Value; break;
                case AdvertisementContactType.Sms: input.SmsSelected = true; input.Sms = contact.Value; break;
                case AdvertisementContactType.Messenger: input.MessengerSelected = true; input.Messenger = contact.Value; break;
                case AdvertisementContactType.Discord: input.DiscordSelected = true; input.Discord = contact.Value; break;
                case AdvertisementContactType.AtVenue: input.AtVenueSelected = true; break;
            }
        return input;
    }
}
