using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;

namespace WDWAPP.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<NewsArticle> NewsArticles => Set<NewsArticle>();
    public DbSet<PlayerProfile> PlayerProfiles => Set<PlayerProfile>();
    public DbSet<Advertisement> Advertisements => Set<Advertisement>();
    public DbSet<AdvertisementImage> AdvertisementImages => Set<AdvertisementImage>();
    public DbSet<AdvertisementContactMethod> AdvertisementContacts => Set<AdvertisementContactMethod>();
    public DbSet<CalendarEvent> Events => Set<CalendarEvent>();
    public DbSet<MatchRequest> MatchRequests => Set<MatchRequest>();
    public DbSet<LeaguePlayer> LeaguePlayers => Set<LeaguePlayer>();
    public DbSet<LeagueSeason> LeagueSeasons => Set<LeagueSeason>();
    public DbSet<LeagueRegistration> LeagueRegistrations => Set<LeagueRegistration>();
    public DbSet<LeagueSession> LeagueSessions => Set<LeagueSession>();
    public DbSet<LeagueParticipation> LeagueParticipations => Set<LeagueParticipation>();
    public DbSet<LeagueMatch> LeagueMatches => Set<LeagueMatch>();
    public DbSet<LeagueRating> LeagueRatings => Set<LeagueRating>();
    public DbSet<LeagueAudit> LeagueAudits => Set<LeagueAudit>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Advertisement>().HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Advertisement>().HasIndex(a => a.ExpiresAt);
        builder.Entity<Advertisement>().HasIndex(a => new { a.CreatedAt, a.Id });
        builder.Entity<Advertisement>().Property(a => a.Version).IsConcurrencyToken();
        builder.Entity<Advertisement>().ToTable(t => {
            t.HasCheckConstraint("CK_Advertisement_Type", "Type BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_Advertisement_Expiry", "ExpiresAt > CreatedAt");
        });
        builder.Entity<AdvertisementImage>().HasOne(i => i.Advertisement).WithMany(a => a.Images)
            .HasForeignKey(i => i.AdvertisementId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<AdvertisementImage>().HasIndex(i => new { i.AdvertisementId, i.SortOrder });
        builder.Entity<AdvertisementContactMethod>().HasOne(c => c.Advertisement).WithMany(a => a.Contacts)
            .HasForeignKey(c => c.AdvertisementId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<AdvertisementContactMethod>().HasIndex(c => new { c.AdvertisementId, c.Type }).IsUnique();
        builder.Entity<AdvertisementContactMethod>().ToTable(t => t.HasCheckConstraint("CK_AdvertisementContact_Type", "Type BETWEEN 0 AND 5"));
        builder.Entity<PlayerProfile>().HasIndex(p => p.UserId).IsUnique();
        builder.Entity<PlayerProfile>().HasOne<ApplicationUser>().WithOne().HasForeignKey<PlayerProfile>(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PlayerProfile>().Property(p => p.Version).IsConcurrencyToken();
        builder.Entity<PlayerProfile>().OwnsOne(p => p.SelfReportedTitles);
        builder.Entity<CalendarEvent>().HasOne(e => e.LeagueSession).WithOne()
            .HasForeignKey<CalendarEvent>(e => e.LeagueSessionId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<CalendarEvent>().HasOne(e => e.MatchRequest).WithOne(r => r.CalendarEvent)
            .HasForeignKey<CalendarEvent>(e => e.MatchRequestId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<CalendarEvent>().ToTable(t => t.HasCheckConstraint("CK_Event_Type",
            "(Type IN (0, 1) AND LeagueSessionId IS NULL AND MatchRequestId IS NULL) OR " +
            "(Type = 2 AND LeagueSessionId IS NOT NULL AND MatchRequestId IS NULL) OR " +
            "(Type = 3 AND MatchRequestId IS NOT NULL AND LeagueSessionId IS NULL)"));
        builder.Entity<MatchRequest>().HasOne(r => r.OwnerUser).WithMany()
            .HasForeignKey(r => r.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<MatchRequest>().HasOne(r => r.AcceptedByUser).WithMany()
            .HasForeignKey(r => r.AcceptedByUserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<MatchRequest>().Property(r => r.Version).IsConcurrencyToken();
        builder.Entity<MatchRequest>().ToTable(t => t.HasCheckConstraint("CK_MatchRequest_Acceptance",
            "(AcceptedByUserId IS NULL AND AcceptedUtc IS NULL) OR " +
            "(AcceptedByUserId IS NOT NULL AND AcceptedUtc IS NOT NULL AND AcceptedByUserId <> OwnerUserId)"));
        builder.Entity<LeagueRegistration>().HasKey(r => new { r.SeasonId, r.PlayerId });
        builder.Entity<LeagueSeason>().HasIndex(s => new { s.Year, s.Term }).IsUnique();
        builder.Entity<LeagueSeason>().HasIndex(s => s.IsClosed).IsUnique().HasFilter("IsClosed = 0");
        builder.Entity<LeagueSeason>().Property(s => s.Version).IsConcurrencyToken();
        builder.Entity<LeagueSeason>().ToTable(t => t.HasCheckConstraint("CK_LeagueSeason_Term", "Term IN (1, 2) AND Year BETWEEN 1 AND 9999"));
        builder.Entity<LeaguePlayer>().HasIndex(p => p.UserId).IsUnique();
        builder.Entity<LeaguePlayer>().HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<LeagueSession>().Property(s => s.Version).IsConcurrencyToken();
        builder.Entity<LeagueMatch>().Property(m => m.Version).IsConcurrencyToken();
        builder.Entity<LeagueMatch>().Property(m => m.PlayerOneFaction).HasMaxLength(100);
        builder.Entity<LeagueMatch>().Property(m => m.PlayerTwoFaction).HasMaxLength(100);
        builder.Entity<LeagueMatch>().HasAlternateKey(m => new { m.Id, m.SessionId });
        builder.Entity<LeagueMatch>().HasOne<LeaguePlayer>().WithMany().HasForeignKey(m => m.PlayerOneId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<LeagueMatch>().HasOne<LeaguePlayer>().WithMany().HasForeignKey(m => m.PlayerTwoId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<LeagueMatch>().ToTable(t => {
            t.HasCheckConstraint("CK_LeagueMatch_Players", "PlayerOneId <> PlayerTwoId AND ReporterId IN (PlayerOneId, PlayerTwoId)");
            t.HasCheckConstraint("CK_LeagueMatch_Result", "Outcome BETWEEN 0 AND 2 AND Status BETWEEN 0 AND 2");
        });
        builder.Entity<LeagueParticipation>().HasKey(p => new { p.SessionId, p.PlayerId });
        builder.Entity<LeagueParticipation>().HasOne(p => p.Match).WithMany().HasForeignKey(p => new { p.MatchId, p.SessionId })
            .HasPrincipalKey(m => new { m.Id, m.SessionId }).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<LeagueParticipation>().ToTable(t => t.HasCheckConstraint("CK_LeagueParticipation_Bye", "NOT (IsBye = 1 AND MatchId IS NOT NULL)"));
        builder.Entity<LeagueRating>().HasKey(r => new { r.MatchId, r.PlayerId });
        builder.Entity<CalendarEvent>().Property(item => item.Version).IsConcurrencyToken();
        builder.Entity<CalendarEvent>().HasIndex(item => item.Date);
        builder.Entity<NewsArticle>().HasOne(article => article.Event).WithOne(item => item.NewsArticle)
            .HasForeignKey<NewsArticle>(article => article.EventId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<NewsArticle>().Navigation(article => article.Event).AutoInclude();
        builder.Entity<NewsArticle>().Property(article => article.Version).IsConcurrencyToken();
        builder.Entity<NewsArticle>().HasIndex(article => article.PublishedUtc);
        builder.Entity<ApplicationUser>().HasIndex(user => user.NormalizedEmail).IsUnique();
        builder.Entity<IdentityUserRole<string>>().HasIndex(role => role.UserId).IsUnique();
    }
}
