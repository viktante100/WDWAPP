# Player profiles and league calendar

Profiles are optional and have a unique, cascading foreign key to the existing Identity account. Creation requires Member, KeyMember or Admin. Ownership is obtained from the security-stamp-validated authenticated account, never form data. Owners (including those whose membership has expired) may edit/delete their profile; administrators may edit/delete any profile. All writes revalidate access in the service, use antiforgery-protected forms and concurrency versions. Public pages expose only the profile's selected public fields, not account contact details.

SelfReportedTitles is an owned value object, separate from league match results and any future verified achievements. Zero categories are omitted. Statistics join LeaguePlayer through its unique UserId, using confirmed matches in non-cancelled rounds; no name matching is used.

Images use the existing database blob architecture and NewsImage validation (5 MB, signature-based JPEG/PNG/GIF/WebP allowlist). Client filenames are never used for storage. Replacement, profile deletion and account deletion remove the old blob transactionally. Image responses use their detected MIME type and nosniff. Generic avatars are vendored Bootstrap Icons v1.13.1 under MIT; attribution/license is in wwwroot/Images/avatars.

Calendar events have a unique nullable LeagueSessionId foreign key with cascade deletion. New rounds create events at 17:00 Stockholm wall-clock time, using the existing round ID as the displayed number. Date changes update the same event. Cancelling a round removes the event; reopening restores it. Deleting a round or its season cascades to its event. League calendar events cannot be independently edited/deleted/published as news through EventService. No news is created by league operations. Existing rounds are not backfilled by the migration; editing their date creates the linked event.

## Veizla investigation (2026-09-19)

Inspected https://rank.veizla.gg/ and its advertised WordPress REST index https://rank.veizla.gg/wp-json/. Rankings are rendered as HTML; player links use numeric IDs at /spelare/{id}. The REST index exposes oembed/1.0, wp/v2, wp-site-health/v1, wp-block-editor/v1 and wp-abilities/v1, with no ranking/player-specific feed found. Public searches did not identify a documented supported ranking API. This does not prove no private or unpublished API exists.

No HTML scraper or guessed endpoint integration is implemented. IPlayerRanking currently returns N/A without networking, so unavailable Veizla cannot break or slow public pages. VeizlaIdentity is stored for future integration but is not treated as a verified identity. Before enabling live rankings, obtain a supported feed and identity contract from Veizla, explicitly verify the association, and implement a background refresh into a local cache with timestamps, expiry and failure fallback. Do not fuzzy-match names.

## Deployment

Back up the production SQLite database, then apply `dotnet ef database update` before deploying the updated application. Development applies migrations automatically. There is no additional configuration or external service required. No existing member automatically receives a public profile.
