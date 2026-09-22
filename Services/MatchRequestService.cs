using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WDWAPP.Data;
using WDWAPP.Security;

namespace WDWAPP.Services;

public sealed class MatchRequestInput
{
    [Required(ErrorMessage = "Ange ett datum.")] public DateOnly? Date { get; set; }
    [Required(ErrorMessage = "Ange en starttid.")] public string Time { get; set; } = "";
    [Required(ErrorMessage = "Välj ett spel.")] public string Game { get; set; } = "";
    public string Version { get; set; } = "";
}

// Returned only after commit. A future application-level notification handler can consume this
// without putting mail delivery in the transaction or in the domain service.
public sealed record MatchAccepted(int RequestId, string OwnerUserId, string AcceptedByUserId, DateTime AcceptedUtc);
public sealed record MatchRequestResult(bool Succeeded, string Message, int? Id = null, MatchAccepted? Acceptance = null);
public sealed record MatchRequestAccess(bool Eligible, bool Admin, string? UserId, string? Nickname, bool KeyMember = false)
{
    public bool CanPlay => Eligible;
}

public sealed class MatchRequestService(ApplicationDbContext database, UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn, TimeProvider clock)
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
    public DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, Stockholm);
    public bool IsFuture(CalendarEvent item) => item.Time.HasValue && item.Date.ToDateTime(item.Time.Value) > LocalNow;

    public async Task<MatchRequestAccess> AccessAsync(ClaimsPrincipal actor)
    {
        var user = actor.Identity?.IsAuthenticated == true ? await signIn.ValidateSecurityStampAsync(actor) : null;
        if (user is null) return new(false, false, null, null);
        var roles = await users.GetRolesAsync(user);
        return new(roles.Any(AccessLevels.Roles.Contains), roles.Contains(AccessLevels.Admin), user.Id, user.UserName,
            roles.Contains(AccessLevels.KeyMember) || roles.Contains(AccessLevels.Admin));
    }

    private IQueryable<string> KeyMemberIds => from membership in database.UserRoles
        join role in database.Roles on membership.RoleId equals role.Id
        where role.Name == AccessLevels.KeyMember || role.Name == AccessLevels.Admin
        select membership.UserId;

    public IQueryable<MatchRequest> VisibleRequests(MatchRequestAccess access)
    {
        var today = DateOnly.FromDateTime(LocalNow);
        return Requests().Where(r => r.CalendarEvent.Date >= today &&
            (r.AcceptedByUserId == null && (access.KeyMember || KeyMemberIds.Contains(r.OwnerUserId))
            || access.Admin || access.UserId != null &&
            (r.OwnerUserId == access.UserId || r.AcceptedByUserId == access.UserId)));
    }

    public IQueryable<CalendarEvent> VisibleEvents(IQueryable<CalendarEvent> query, MatchRequestAccess access, bool management = true)
    {
        var visibleIds = VisibleRequests(management ? access : access with { Admin = false }).Select(r => r.Id);
        return query.Where(e => e.MatchRequestId == null || visibleIds.Contains(e.MatchRequestId.Value));
    }

    private IQueryable<MatchRequest> Requests() => database.MatchRequests.AsNoTracking().Select(r => new MatchRequest
    {
        Id = r.Id, OwnerUserId = r.OwnerUserId, AcceptedByUserId = r.AcceptedByUserId,
        Game = r.Game, CreatedUtc = r.CreatedUtc, AcceptedUtc = r.AcceptedUtc, Version = r.Version,
        OwnerUser = new ApplicationUser { UserName = r.OwnerUser.UserName },
        AcceptedByUser = r.AcceptedByUser == null ? null
            : new ApplicationUser { UserName = r.AcceptedByUser.UserName },
        CalendarEvent = new CalendarEvent { Id = r.CalendarEvent.Id, Date = r.CalendarEvent.Date, Time = r.CalendarEvent.Time, Type = r.CalendarEvent.Type }
    });

    public async Task<MatchRequestResult> SaveAsync(ClaimsPrincipal actor, int? id, MatchRequestInput input)
    {
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(input, new ValidationContext(input), errors, true)
            || !MatchGames.All.Contains(input.Game)
            || !TimeOnly.TryParseExact(input.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return new(false, "Välj datum, starttid (HH:mm) och ett spel i listan.");
        var local = input.Date!.Value.ToDateTime(time);
        if (local <= LocalNow || Stockholm.IsInvalidTime(local) || Stockholm.IsAmbiguousTime(local))
            return new(false, "Välj en framtida tid i svensk tid. Tiden får inte vara ogiltig eller tvetydig vid byte mellan sommar- och vintertid.");
        await using var transaction = await database.Database.BeginTransactionAsync();
        database.ChangeTracker.Clear();
        var access = await AccessAsync(actor);
        if (!access.Eligible) return new(false, "Terminspass eller nyckelmedlemskap krävs. Logga in igen.");
        var request = id.HasValue ? await database.MatchRequests.Include(r => r.CalendarEvent).SingleOrDefaultAsync(r => r.Id == id)
            : new MatchRequest { OwnerUserId = access.UserId!, CreatedUtc = clock.GetUtcNow().UtcDateTime,
                CalendarEvent = new CalendarEvent { Type = CalendarEventType.MatchRequest, CreatedUtc = clock.GetUtcNow().UtcDateTime } };
        if (request is null || request.OwnerUserId != access.UserId) return new(false, "Du kan bara ändra din egen förfrågan.");
        if (request.AcceptedByUserId != null) return new(false, "En bokad match kan inte ändras.");
        if (id.HasValue && request.Version != input.Version) return new(false, "Förfrågan har ändrats. Ladda om sidan.");
        request.Game = input.Game;
        request.CalendarEvent.Date = input.Date.Value;
        request.CalendarEvent.Time = time;
        request.CalendarEvent.Version = Guid.NewGuid().ToString("N");
        request.Version = Guid.NewGuid().ToString("N");
        if (!id.HasValue) database.MatchRequests.Add(request);
        try { await database.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return new(false, "Förfrågan har ändrats. Ladda om sidan."); }
        await transaction.CommitAsync();
        return new(true, "Matchförfrågan har sparats.", request.Id);
    }

    public async Task<MatchRequestResult> AcceptAsync(ClaimsPrincipal actor, int id, string version)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync();
            database.ChangeTracker.Clear();
            var access = await AccessAsync(actor);
            if (!access.CanPlay) return new(false, "Terminspass eller högre behörighet krävs för att acceptera en match.");
            var request = await Requests().SingleOrDefaultAsync(r => r.Id == id);
            if (request is null) return new(false, "Förfrågan finns inte längre.");
            if (request.OwnerUserId == access.UserId) return new(false, "Du kan inte acceptera din egen match.");
            if (!access.KeyMember && !await KeyMemberIds.ContainsAsync(request.OwnerUserId))
                return new(false, "Minst en av spelarna måste vara nyckelmedlem. Endast nyckelmedlemmar kan besvara den här förfrågan.");
            if (!IsFuture(request.CalendarEvent)) return new(false, "Matchens starttid har passerat.");
            var acceptedUtc = clock.GetUtcNow().UtcDateTime;
            // Atomic compare-and-set is the arbiter, even if callers loaded the same version.
            var changed = await database.MatchRequests.Where(r => r.Id == id && r.Version == version && r.AcceptedByUserId == null
                && r.OwnerUserId != access.UserId).ExecuteUpdateAsync(set => set
                    .SetProperty(r => r.AcceptedByUserId, access.UserId)
                    .SetProperty(r => r.AcceptedUtc, acceptedUtc)
                    .SetProperty(r => r.Version, Guid.NewGuid().ToString("N")));
            if (changed != 1) return new(false, "Matchen har redan accepterats eller ändrats. Ladda om sidan.");
            await transaction.CommitAsync();
            return new(true, "Matchen är bokad!", id, new(id, request.OwnerUserId, access.UserId!, acceptedUtc));
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        { return new(false, "Matchen uppdateras just nu. Ladda om sidan och försök igen."); }
    }

    public async Task<MatchRequestResult> DeleteAsync(ClaimsPrincipal actor, int id, string version, bool confirmed)
    {
        if (!confirmed) return new(false, "Bekräfta att matchförfrågan ska tas bort.");
        await using var transaction = await database.Database.BeginTransactionAsync();
        database.ChangeTracker.Clear();
        var access = await AccessAsync(actor);
        if (!access.Eligible) return new(false, "Du saknar behörighet att ta bort förfrågan.");
        var request = await database.MatchRequests.SingleOrDefaultAsync(r => r.Id == id);
        if (request is null) return new(false, "Förfrågan finns inte längre.");
        if (!access.Admin && (request.OwnerUserId != access.UserId || request.AcceptedByUserId != null))
            return new(false, "Du kan bara ta bort din egen oaccepterade förfrågan. Kontakta admin för en bokad match.");
        if (request.Version != version) return new(false, "Förfrågan har ändrats. Ladda om sidan.");
        database.MatchRequests.Remove(request);
        try { await database.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return new(false, "Förfrågan har ändrats. Ladda om sidan."); }
        await transaction.CommitAsync();
        return new(true, "Matchförfrågan och kalenderposten har tagits bort.");
    }
}
