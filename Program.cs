using WDWAPP.Components;
using WDWAPP.Components.Account;
using WDWAPP.Data;
using WDWAPP.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WDWAPP.Services;

var bootstrapIndex = Array.IndexOf(args, "--bootstrap-admin");
if (bootstrapIndex >= 0 && (bootstrapIndex + 1 >= args.Length || args[bootstrapIndex + 1].StartsWith("--")))
{
    Console.Error.WriteLine("Usage: --bootstrap-admin existing-user@example.com");
    Environment.ExitCode = 1;
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.AddAuthorization(AccessLevels.Configure);
builder.Services.AddScoped<AdminUserService>();
builder.Services.AddScoped<NewsService>();
builder.Services.AddScoped<EventService>();
builder.Services.AddScoped<MatchRequestService>();
builder.Services.AddScoped<InitialAdminSetup>();
builder.Services.AddScoped<LeagueService>();
builder.Services.AddScoped<PlayerProfileService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AdvertisementService>();
builder.Services.AddHostedService<AdvertisementCleanup>();
builder.Services.AddSingleton<IPlayerRanking, UnavailablePlayerRanking>();
// Role changes and deleted accounts take effect on the next HTTP request.
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing DefaultConnection.")));
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.User.AllowedUserNameCharacters = "";
        options.Password.RequiredLength = 10;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        // Email delivery/confirmation will be implemented in a later increment.
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/access-denied";
    options.Cookie.Name = "WDWAPP.Identity";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
});

var app = builder.Build();

// Local development is ready to run; production migrations are applied explicitly.
using (var scope = app.Services.CreateScope())
{
    if (app.Environment.IsDevelopment())
    {
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
    }

    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var role in AccessLevels.Roles)
    {
        if (!await roles.RoleExistsAsync(role))
        {
            var result = await roles.CreateAsync(new IdentityRole(role));
            if (!result.Succeeded)
                throw new InvalidOperationException($"Could not create role {role}.");
        }
    }

    if (bootstrapIndex >= 0)
    {
        var result = await scope.ServiceProvider.GetRequiredService<InitialAdminSetup>()
            .PromoteAsync(args[bootstrapIndex + 1]);
        Console.WriteLine(result.Message);
        Environment.ExitCode = result.Succeeded ? 0 : 1;
        return;
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/marknad"))
    {
        var limit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = AdvertisementService.MaxRequestBytes;
    }
    await next(context);
});
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/marknad/bilder/{id:int}", async (int id, ApplicationDbContext database, TimeProvider clock, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var now = clock.GetUtcNow().UtcDateTime;
    var image = await database.AdvertisementImages.AsNoTracking().Where(i => i.Id == id && i.Advertisement.ExpiresAt > now)
        .Select(i => new { i.Data, i.ContentType }).SingleOrDefaultAsync();
    if (image is null) return Results.NotFound();
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(image.Data, image.ContentType);
});
app.MapGet("/spelare/{id:int}/bild", async (int id, ApplicationDbContext database, HttpContext context) =>
{
    var image = await database.PlayerProfiles.AsNoTracking().Where(p => p.Id == id)
        .Select(p => new { p.ImageData, p.ImageContentType }).SingleOrDefaultAsync();
    if (image?.ImageData is null || image.ImageContentType is null) return Results.NotFound();
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(image.ImageData, image.ImageContentType);
});
app.MapGet("/evenemang/{id:int}/bild", async (int id, ApplicationDbContext database, HttpContext context) =>
{
    var image = await database.Events.AsNoTracking().Where(item => item.Id == id)
        .Select(item => new { item.ImageData, item.ImageContentType }).SingleOrDefaultAsync();
    if (image?.ImageData is null || image.ImageContentType is null) return Results.NotFound();
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(image.ImageData, image.ImageContentType);
});
app.MapGet("/nyheter/{id:int}/bild", async (int id, ApplicationDbContext database, HttpContext context) =>
{
    var image = await database.NewsArticles.AsNoTracking().Where(article => article.Id == id)
        .Select(article => new { article.ImageData, article.ImageContentType }).SingleOrDefaultAsync();
    if (image?.ImageData is null || image.ImageContentType is null) return Results.NotFound();
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(image.ImageData, image.ImageContentType);
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
