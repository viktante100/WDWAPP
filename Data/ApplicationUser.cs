using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace WDWAPP.Data;

// UserName is the public nickname. Email is used only for signing in.
public sealed class ApplicationUser : IdentityUser
{
    [MaxLength(100)]
    public string? FullName { get; set; }
    [MaxLength(200)]
    public string? Address { get; set; }
    [MaxLength(6)]
    public string? PostalCode { get; set; }
    [MaxLength(100)]
    public string? City { get; set; }
}
