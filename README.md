# WDWAPP

.NET 10 Blazor Web App with Interactive Server rendering. Identity account pages use
static server rendering so login and logout can issue authentication cookies.

## Run locally

Use the HTTPS launch profile in Visual Studio, or:

```powershell
dotnet run --project WDWAPP.csproj --launch-profile https
```

Open https://localhost:7049. Authentication cookies require HTTPS.
Register at `/register` with an email, a unique public nickname (2–32 characters),
and a password, plus name, address, Swedish postal code, and city. Postal codes accept
`12345` or `123 45` and are stored as `123 45`. These contact details are required for
new registrations; existing accounts retain null values until a future profile-edit
feature is added. The public display name remains the nickname.
Sign in at `/login` with the email, not the nickname. Clicking the
nickname in the header opens `/account`, where the user can log out.

Development startup applies EF migrations to `Data/wdwapp.db` and creates the three
privileged roles. The SQLite database is ignored by Git. Override
`ConnectionStrings:DefaultConnection` to use another SQLite location.

## Authorization foundation

Registration assigns **no roles**. An authenticated account satisfies `LoggedIn`.
Each account can have **zero or one role**. Changing access replaces the previous
role; choosing Inloggad removes it. A unique database index enforces this rule,
including for code that writes directly to Identity's user-role table.
The single-role migration keeps the highest role on any legacy multi-role account
(Admin > KeyMember > Member), and invalidates that account's old login sessions.
Accounts with zero or one role are unchanged.
The named policies in `Security/AccessLevels.cs` are cumulative:

| Policy | Allowed users |
| --- | --- |
| LoggedIn | Any authenticated user |
| Member | Member, KeyMember, Admin |
| KeyMember | KeyMember, Admin |
| Admin | Admin |

Protect future endpoints on the server, for example:

```csharp
app.MapGet("/api/member-content", Handler).RequireAuthorization(AccessLevels.Member);
```

For Razor pages/components use `@attribute [Authorize(Policy = AccessLevels.Member)]`.
Hiding a link is not authorization. Future cookie-authenticated state-changing API
endpoints also need antiforgery protection.

## First admin and user management

1. Register your own account normally.
2. Stop the running application in Visual Studio.
3. In the project directory, run (replace the example with your registered email):

```powershell
dotnet run --project WDWAPP.csproj --launch-profile https -- --bootstrap-admin "you@example.com"
```

The command promotes that existing account and exits without starting the web server.
It refuses to run if an admin already exists or the email is not registered. It never
creates an account or sets a password, and no public setup route is exposed.
For production, run the published application with the same argument and the intended
database configuration, after applying migrations.

Restart the app and sign in again. **Admin** appears in the hamburger menu only for
admins. `/admin` offers a searchable, paginated user list. Each user has an access-level
editor and a separate deletion confirmation page. Accounts are permanently deleted;
future features that reference users must explicitly handle account deletion.

An admin cannot change their own access or delete their own account. Another admin
must do that. The service also protects the last admin and rejects stale edits.
Mutations run in a transaction and recheck the acting admin against the database.

Role changes invalidate the affected user's existing login sessions. Identity checks
the security stamp on every HTTP request; open interactive circuits revalidate every
minute. Admin pages use static server rendering, so every action passes HTTP
authorization and antiforgery validation. Future sensitive interactive features must
also check authorization at their server-side operation boundary.

## News

The homepage shows a compact preview of the newest article, linking directly to it
in `/nyheter`. The public archive contains every published article, newest first.
Dates are assigned automatically in UTC and displayed in Swedish using Stockholm
time. Editing does not change the original publication date or archive order.

Admins and keymembers use **Admin / Keymember** (`/hantera`) to publish news.
They can edit and delete articles from the archive. User management remains
admin-only and is hidden from keymembers in the shared management page.

Articles support a title, plain text with line breaks, an optional picture uploaded
from the editor's device with an accessible description, and optional HTTP(S) links
(one per line). JPEG, PNG, GIF, and WebP uploads up to 5 MB are supported; the server
checks file signatures rather than trusting filenames or supplied MIME types.
Uploaded pictures are stored in SQLite with the article, so database backups include
them. Editors can replace or remove a picture; text edits preserve it. Deleting an
article also deletes its picture. Existing externally linked pictures still display.
Publishing is immediate. Deletion requires confirmation, and stale edits are rejected.
All management forms require authorization and antiforgery validation, with a
fresh database permission check for each mutation.

The `AddNewsArticles` and `AddNewsImageUploads` migrations run automatically in development. Apply them before
deploying to production using the database update command below.

## Calendar and events

`/kalender` (also `/evenemang`) shows a compact Monday-first month calendar with
event markers. Selecting a date lists that day's events below the grid; each event
has a public detail page. Month/date links work without JavaScript. Dates and optional
times refer to Swedish local time; creation and publication timestamps use UTC.

Keymembers and admins manage events through `/hantera/evenemang` in the shared
management area. Title, description and date are required. Time, an uploaded image
and an HTTP(S) external link are optional. Upload limits and validation are shared
with news. All event mutations recheck the user's current role and security stamp,
require antiforgery protection at the form boundary, and reject stale versions.

Events are stored in `Events`. Selecting **Publicera som nyhet** creates a news row
with a unique nullable `EventId` foreign key and a publication timestamp. Its displayed
content comes directly from the event, including the image; no event content is copied
into the news row. Standalone news rows have no `EventId` and behave as before.
Event-backed news must be edited through the event editor. Editing preserves the news
publication timestamp; disabling publication removes only the linked news row.
Re-enabling publication creates a new publication timestamp, making it eligible as
latest news again. Deleting an event cascades to its news row and removes its image.

The `AddCalendarEvents` migration preserves existing news and runs automatically
in development. Apply it explicitly before a production deployment. No calendar
library or other new runtime dependency is required.

## 40K league

`/ligan` is the public, sortable league table. Official position is ordered by total
points then ELO (player ID gives stable ordering when both are equal). Display sorting
does not change that position. The table scrolls horizontally on small screens.
The homepage card and hamburger menu link to it. The table appears first, headed
by the current season, e.g. `40K-liga Höstterminen 2026`. Participation links and
access information appear below it. Signed-in users without Member access are told
that a term pass or key membership is required; only signed-out visitors are asked
to log in.

Members, keymembers and admins register separately for each season directly from `/ligan`
at any time, then opt into individual open rounds. Only that season's registered
players appear in its table. `LeagueRegistration` stores the season/player pair and
UTC registration timestamp; the global player record retains Identity and ELO history.
New players start with 500 ELO and zero statistics, with no automatic
entries for prior rounds. A round's Swedish calendar date must be on or after the
player's registration date for that season. Joining never resets another player's results or ratings.

`Mina matcher` appears under the standings with both names, factions, match points
for/against and W/L/D. Players report both factions and nonnegative match scores;
the server derives the outcome. Their opponent confirms or disputes with a single
form submission in the match card (with antiforgery, ownership and version checks).
Confirmed matches show only their results, with no confirmation status text.
The old `/ligan/spela` route redirects to `/ligan`. There is no public season selector;
historical data remains available for a future statistics page.

Pending and
disputed matches reserve both players' round slots but do not score. Either player
can resubmit an unconfirmed result, becoming its reporter; the opponent must confirm
again. Only admins can correct confirmed results. Even admins cannot use opponent
confirmation for their own report; administrative correction is a separate action.

Admins use `/hantera/ligan` to create and close spring/autumn leagues, create rounds with manual dates, correct/delete
matches, assign/remove WO, finalize, reopen or cancel rounds. WO requires an odd
number of participants, with at most one WO per round. Remove WO before adding more
participants. WO gives exactly three points and affects no played-match statistic or
rating. Confirmed matches score 3/2/1 immediately; PPG is their league points divided
by their match count (three wins = 9 / 3 = 3.00). WO never enters that average.
PTSF/PTSA are the actual match scores for/against from confirmed, non-cancelled games.
Legacy games without scores show an unknown total (–), not an invented score.
Finalization rejects unresolved matches and locks normal player actions.
There is at most one active league and one league per year/term. All rounds must be
finalized or cancelled before closing a league. The next league starts with zero
points, matches, W/D/L and PPM; players retain only their accumulated ELO. Existing
league players must register again for the new season before appearing in its table
or joining rounds. Historical standings calculations retain the statistics and ELO
through that season, without including later results.
Closed leagues block player actions and new rounds; admins can still correct
historical results, replaying ELO through subsequent seasons.

Admins can also choose **Radera liga** on the management page and explicitly confirm
permanent deletion. This removes that season's rounds, matches, participation/WO and
rating entries in one transaction, then recalculates ELO from the remaining seasons.
Player accounts, their global league identity and the administrative audit log remain;
registrations for the deleted season are removed. Deleting
all seasons returns players to 500 ELO when a new league is created, making test data
easy to reset. Both active and closed leagues can be deleted.

Cancelled rounds contribute neither points nor ratings; reopening removes the round's
ELO until it is finalized again while retaining confirmed-match points.

Match history is authoritative. `LeagueRatings` is a derived per-match ledger with
before/change/after values, rebuilt transactionally from finalized confirmed matches
ordered by season year/term, round date, then round ID. Historical result/date/status changes replay
all later ratings using the custom decisive and draw tables. `LeagueAudits` preserves
the actor, UTC timestamp, submitted command and before/after snapshots. No W/D/L,
point or PPM totals are stored. Creating a new season changes the scope of statistics
without deleting any history. There is no automatic scheduling.

All mutations revalidate the acting user's security stamp and current database role
inside a serializable SQLite transaction. Static forms provide antiforgery protection;
version tokens reject stale match/round edits. A unique participation key and SQLite
triggers prevent cross-slot duplicate matches and match/WO overlap. Identity accounts
with league records cannot be deleted; their access can be revoked while preserving
history. Future presentation/profile pages are deliberately deferred.

Apply `AddLeague`, `AddLeagueJoinDate`, `AddLeagueSeasons`, `AddLeagueRegistrations` and `AddLeagueMatchDetails` before production deployment. Development
applies them at startup. League tests cover the role hierarchy, HTTP forms and CSRF,
result lifecycle, byes, PPM, sorting, every rating bracket boundary, historical replay,
database invariants, joining an ongoing league, season transitions, archived rankings,
cross-season ELO corrections and migration of existing data. The season migration
assigns existing rounds to spring/autumn by their date and keeps the latest league
open. The registration migration preserves players only in seasons where they already
have round participation; other players must register explicitly. Admins create leagues
manually on an empty installation.

## Database changes and deployment

The initial migration and model snapshot are in `Data/Migrations`. Restore the pinned
EF tool before creating or applying migrations:

```powershell
dotnet tool restore
dotnet ef migrations add MigrationName --project WDWAPP.csproj --output-dir Data/Migrations
dotnet ef database update --project WDWAPP.csproj
```

Production does not automatically migrate the database. Apply migrations before
starting the app. Provide a writable, persistent SQLite location and persist ASP.NET
Core Data Protection keys so authentication cookies survive deployments.

## Tests

```powershell
dotnet test tests/WDWAPP.Tests/WDWAPP.Tests.csproj -c Release
```

Integration tests use a temporary SQLite database and cover registration, nickname
display, login/logout, persistent cookies, antiforgery, duplicate emails, lockout,
return URL safety, the access hierarchy, admin authorization, role changes, deletion,
stale sessions/edits, self-protection, and one-time initial admin setup. News tests
cover publishing/editing/deleting as either editor role, automatic dates, latest
article selection, archive ordering, encoded content, unsafe links, antiforgery,
permission restrictions, stale edits, and revoked editor access.
Event tests also cover both editor roles, event/news publication toggles, live content
updates, cascade deletion, optional fields, image upload/removal, invalid dates/links,
stale edits, revoked permissions, month boundaries and leap years.

## Deliberately deferred

Avatars, email confirmation, password recovery, membership payments/expiry, and the business pages/API endpoints are not
implemented yet. Email addresses are currently not verified. Existing design-page
links and content remain placeholders.
