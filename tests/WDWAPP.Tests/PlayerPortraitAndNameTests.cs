using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WDWAPP.Data;
using WDWAPP.Security;
using WDWAPP.Services;
using Xunit;

namespace WDWAPP.Tests;

public sealed partial class PlayerProfileTests
{
    private async Task SetAccountName(Actor actor, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.Users.SingleAsync(u => u.Id == actor.Id)).FullName = name;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task PortraitAndName_owner_name_is_used_even_when_admin_edits_and_public_name_tracks_account()
    {
        var owner = await User(); var admin = await User(AccessLevels.Admin);
        await SetAccountName(owner, "Anna-Lisa Andersson");
        await SetAccountName(admin, "Admin Administratör");
        var id = (await Save(owner, input: new() { DisplayName = "Forged name", FirstNameOnly = true })).Id!.Value;
        Assert.Equal("Anna-Lisa", (await Profile(id)).DisplayName);
        using var visitor = Client();
        var html = await visitor.GetStringAsync("/spelare");
        Assert.Contains("Anna-Lisa", html);
        Assert.DoesNotContain("Andersson", html);
        var profile = await Profile(id);
        Assert.True((await Save(admin, id, new() { Version = profile.Version, FirstNameOnly = false })).Succeeded);
        Assert.Equal("Anna-Lisa Andersson", (await Profile(id)).DisplayName);
        await SetAccountName(owner, "Anna-Lisa Svensson");
        html = await visitor.GetStringAsync("/spelare");
        Assert.Contains("Anna-Lisa Svensson", html);
        Assert.DoesNotContain("Andersson", html);
        Assert.DoesNotContain("Administratör", html);
    }

    [Fact]
    public async Task PortraitAndName_form_saves_crop_and_privacy_and_uses_same_preview_as_roster()
    {
        var owner = await User();
        await SetAccountName(owner, "Anna Andersson");
        using var client = await Login(owner);
        var html = await client.GetStringAsync("/spelare/ny");
        Assert.DoesNotContain("name=\"Input.DisplayName\"", html);
        Assert.Contains("Anna Andersson", html);
        Assert.Contains("name=\"Input.FirstNameOnly\"", html);
        var response = await Post(client, "/spelare/ny", "player-edit", new()
        {
            ["Input.DisplayName"] = "Forged", ["Input.Avatar"] = "person-circle",
            ["Input.FirstNameOnly"] = "true", ["Input.ImageZoom"] = "2.5",
            ["Input.ImageX"] = "30", ["Input.ImageY"] = "70"
        });
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var id = int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
        var profile = await Profile(id);
        Assert.True(profile.FirstNameOnly);
        Assert.Equal("Anna", profile.DisplayName);
        Assert.Equal(2.5, profile.ImageZoom);
        Assert.Equal(30, profile.ImageX);
        Assert.Equal(70, profile.ImageY);
        html = await client.GetStringAsync($"/spelare/{id}/redigera");
        Assert.Contains("data-player-image-editor", html);
        Assert.Contains("--portrait-zoom:2.5", html);
        using var visitor = Client();
        var roster = await visitor.GetStringAsync("/spelare");
        Assert.Contains("--portrait-zoom:2.5", roster);
        Assert.Contains("--portrait-tx:30%", roster);
        Assert.True(roster.IndexOf("data-player-portrait", StringComparison.Ordinal) < roster.IndexOf("player-club-logo", StringComparison.Ordinal));
        Assert.DoesNotContain("Andersson", roster);
    }

    [Theory]
    [InlineData(0, 50, 50)]
    [InlineData(4.1, 50, 50)]
    [InlineData(1, -1, 50)]
    [InlineData(1, 50, 101)]
    public async Task PortraitAndName_invalid_crop_is_rejected(double zoom, double x, double y)
    {
        var owner = await User();
        Assert.False((await Save(owner, input: new() { ImageZoom = zoom, ImageX = x, ImageY = y })).Succeeded);
    }
}
