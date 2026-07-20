# GraphAuthHarness

Manual test harness for Microsoft Graph **delegated** authentication.

## What it does

This harness proves the delegated OAuth2 auth-code + PKCE flow works end-to-end:

1. Reads `TenantId`, `ClientId`, `RedirectUri`, and `Scopes` from `appsettings.json`
2. Reads `ClientSecret` from .NET user secrets (never stored in source)
3. Opens the system browser — user signs in with a real Microsoft account
4. Once signed in, acquires a delegated access token (acting AS the user, not as the app)
5. Calls `/me` via the Graph API to prove the token works
6. Tests silent token refresh using the cached refresh token
7. Prints: signed-in UPN, partial object ID, delegated scopes, token expiry, and refresh result

The access token itself is never printed.

## Auth model: DELEGATED (not application)

This harness uses **delegated permissions** — the app acts on behalf of a signed-in user.
The scopes granted are:
- `Calendars.Read` — read the user's calendar
- `Mail.Read` — read the user's mail
- `User.Read` — read the user's profile (`/me`)
- `offline_access` — get a refresh token for silent renewal

This is different from application permissions (app-only), which act as the app itself
with no signed-in user. Phase 2 rework explicitly uses delegated auth.

## Prerequisites

- .NET 8 SDK
- An Entra app registration with:
  - Platform: **Mobile and desktop applications** (not "Web")
  - Redirect URI: `http://localhost:5050/auth/callback`
  - Delegated permissions granted and admin-consented:
    `Calendars.Read`, `Mail.Read`, `User.Read`, `offline_access`

## Setup

### 1. Set the client secret via user secrets

Run from the repo root:

```bash
dotnet user-secrets set "MicrosoftGraph:ClientSecret" "<your-client-secret>" \
  --project tools/GraphAuthHarness
```

### 2. Run the harness

```bash
dotnet run --project tools/GraphAuthHarness
```

A browser window will open. Sign in with the Microsoft account you want to test.
After sign-in the browser will redirect back and the harness continues.

## Expected output

```
Opening browser for sign-in (delegated auth-code + PKCE)...
Scopes requested: Calendars.Read, Mail.Read, User.Read, offline_access

Signed-in user      : user@yourtenant.onmicrosoft.com
Object ID           : 12345678...
Delegated scopes    : Calendars.Read, Mail.Read, User.Read, offline_access, profile, openid
Token expiry (UTC)  : 2026-07-14 14:00:00Z

Calling /me to verify token end-to-end...
Display name        : Your Name

Testing silent token refresh (uses cached refresh token)...
Silent refresh test : OK (expiry 2026-07-14 14:00:00Z)
```

## Troubleshooting

| Error | Cause | Fix |
|---|---|---|
| `ClientSecret is not set` | User secrets not configured | Run the `dotnet user-secrets set` command above |
| Browser doesn't open | No system browser | Install a browser or run with `--display` on Linux |
| `redirect_uri_mismatch` | Redirect URI not registered in Entra | Add `http://localhost:5050/auth/callback` under Mobile/Desktop platform |
| `401 Unauthorized` on /me | Missing `User.Read` delegated permission | Grant and admin-consent `User.Read` in Azure Portal |

## Phase 3 — Calendar read

After sign-in and silent refresh, the harness now reads the signed-in user's calendar:

1. Prompts: `Enter number of days to read (default 14):`
2. Calls `MicrosoftGraphCalendarSource.GetEventsAsync` via `/me/calendarView`
   with `Prefer: outlook.timezone="UTC"` so all times arrive as UTC
3. Handles pagination automatically (multiple pages if > ~10 events)
4. Maps each Graph event to `CalendarArtifact` via `GraphCalendarMapper`
5. Prints each event as a compact block with subject, organizer, attendee count,
   externalId, and bodyPreview (truncated to 80 chars)
6. Prints total event count and number of fields that required defaults

Recurring events appear as expanded individual occurrences within the window.
Attendee email addresses are not printed — only the count.

### Expected Phase 3 output (after sign-in lines)

```
Enter number of days to read (default 14): 14
Reading calendar    : 2026-07-14 12:00:00Z → 2026-07-28 12:00:00Z

────────────────────────────────────────────────────────────────
[2026-07-16 09:00 - 09:30 UTC]  Meeting with Contoso
  organizer   : rishi@econtracts.onmicrosoft.com
  attendees   : [count: 3]
  externalId  : msgraph:event:AAMkAG...
  bodyPreview : Discuss renewal terms and pricing...
────────────────────────────────────────────────────────────────
Total events        : 7
```

## Phase 4

Phase 4 will implement `MicrosoftGraphMailSource.FindRelevantMessagesAsync`.
