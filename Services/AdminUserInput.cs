using System.ComponentModel.DataAnnotations;

namespace WDWAPP.Services;

public sealed class AdminUserInput
{
    [Required] public string Version { get; set; } = "";
    [Required, StringLength(32, MinimumLength = 2)]
    [RegularExpression(@"[\p{L}\p{N}_. -]+")]
    public string Nickname { get; set; } = "";
    [Required, EmailAddress] public string Email { get; set; } = "";
    [StringLength(100)] public string? FullName { get; set; }
    [StringLength(200)] public string? Address { get; set; }
    [RegularExpression(@"[0-9]{3} ?[0-9]{2}", ErrorMessage = "Ange postnummer med fem siffror, till exempel 123 45.")]
    public string? PostalCode { get; set; }
    [StringLength(100)] public string? City { get; set; }
    [Phone, StringLength(50)] public string? PhoneNumber { get; set; }
}
