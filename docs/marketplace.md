# Köp/Sälj

The existing `/marknad` menu item opens the public classified listings. Routes:

- `/marknad`: active advertisements, 24 per page, ordered newest first. Cards show full text in a bounded scroll area, all images as rounded thumbnails, and a contact dialog opened from the always-visible footer beside the date. Clicking a thumbnail opens the full image in a dialog.
- `/marknad/{id}`: compatibility redirect to the advertisement's card on the correct listing page; expired/missing ads return 404. There is no separate product detail page.
- `/marknad/ny`: create an advertisement (Member, KeyMember or Admin).
- `/marknad/{id}/redigera` and `/marknad/{id}/radera`: owner/admin actions.
- `/marknad/mina`: an authenticated owner's advertisements, including expired ads awaiting cleanup. Administrators see all advertisements here.

## Data and authorization

`Advertisement`, `AdvertisementImage` and `AdvertisementContactMethod` are stored in SQLite through the existing EF Core context. Types are enums, contacts are individual typed rows, and images have a stable order. Migration `AddAdvertisements` adds only new tables/indexes/constraints. Existing account, profile, league, event and news data is preserved.

Every write validates the Identity security stamp and checks current database roles. The owner comes from the authenticated account when creating and cannot be changed by forms. Members whose membership later expires retain access to edit/delete their existing advertisements. Admins may edit/delete any advertisement; KeyMember does not grant access to other owners' ads. Forms use antiforgery and optimistic concurrency versions. Deletion requires confirmation. Ownership, CreatedAt and ExpiresAt are absent from the form input model.

Contacts are validated both in the form and in the service: at least one selection, required bounded values for selected methods, email/phone format validation, and no additional value for I lokalen. Deselected details are not stored. Account email, telephone and personal details are never copied into advertisements. Razor encodes all public text, including Messenger/Discord details; those details are plain text rather than arbitrary clickable URLs. Contact fields show/hide immediately when their checkboxes change, with required-field state updated in the browser. Server validation remains authoritative.

## Images

The existing `NewsImage` signature validation and bounded reader are reused: JPEG, PNG, GIF or WebP, at most 5 MB each, at most 10 images after removals and additions. A failed validation does not partially delete images or save the advertisement. Images are database blobs, served using generated database IDs and detected MIME types with `nosniff`; original filenames are never storage paths. Lists fetch image IDs without loading image bytes into the page query. Images are lazy-loaded. Replacing/removing an image deletes its blob; deleting an advertisement or account cascades to all associated blobs and contacts. SQLite can reuse freed storage; the database file need not shrink immediately.

Marketplace POSTs allow a 55 MB request body for ten 5 MB files plus form overhead. If deployed behind a proxy or hosting server with a smaller request limit, permit 55 MB for these routes as well. The rest of the application's request limits are unchanged. The file picker accumulates successive selections and displays a removable list of pending files; existing stored images are retained unless explicitly marked for removal. Browsers clear file selections after server validation failures; the form asks the user to select those files again.

## Expiration and cleanup

CreatedAt, UpdatedAt and ExpiresAt are UTC. ExpiresAt is set to `CreatedAt.AddMonths(2)` once, including calendar month-end handling. Editing changes UpdatedAt but never ownership, CreatedAt or ExpiresAt. Expired ads cannot be saved or republished through editing.

List/detail queries and the public image endpoint require `ExpiresAt > now`. The exact expiration boundary is excluded. Marketplace pages and image responses use `Cache-Control: no-store`. Expired records are therefore hidden independently of cleanup scheduling.

`AdvertisementCleanup` runs on application startup and every six hours, deleting expired advertisements in batches of 200. Database cascades remove images and contact methods without loading image bytes. Cleanup errors are logged and retried next interval; shutdown cancellation is respected. An application that was offline catches up on its next startup. No separate cron job or external service is required. The injected TimeProvider makes calendar and expiry tests deterministic.

## Deployment and verification

Development applies migrations on startup. In production, back up SQLite and run `dotnet ef database update` before starting the updated application. This also applies any preceding unapplied migrations. No other feature settings are required, except the hosting upload limit described above if applicable.

`AdvertisementTests` covers membership and ownership, forged ownership/timestamps, public rendering and privacy, conditional contact validation, image count/type/size and editing, confirmation and concurrency, expiration boundaries, cleanup cascades, and preservation of pre-existing database data. The HTTP tests submit the same multipart forms used by the site. `node --test tests/marketplace.test.mjs` verifies successive file selections, removal, cancellation and limits using the real upload handler with lightweight DOM/FileList stand-ins.
