# Slack App Setup — Kozmo Check-ins

Required manual steps to enable the Slack check-in channel. The app cannot be created
programmatically — it requires a Slack workspace admin to complete these steps.

## 1. Create the Slack App

1. Go to https://api.slack.com/apps and click "Create New App".
2. Choose "From scratch".
3. Name it (e.g. "Kozmo Check-ins") and select your workspace.

## 2. Configure Bot Scopes

Under "OAuth & Permissions" > "Scopes" > "Bot Token Scopes", add:

| Scope               | Purpose                                               |
|---------------------|-------------------------------------------------------|
| `chat:write`        | Post messages to channels and DMs (Phase 1+2)         |
| `chat:write.public` | Post to channels the bot is not a member of           |
| `commands`          | Receive `/kozmo` slash command invocations (Phase 3)  |

## 3. Enable Interactivity

Under "Interactivity & Shortcuts":

1. Toggle Interactivity **ON**.
2. Set the Request URL to:
   ```
   https://<your-api-host>/slack/interactivity
   ```
   (Replace `<your-api-host>` with your public URL — e.g. use ngrok for local testing.)
3. Save changes.

## 3a. Register the Slash Command (Phase 3)

Under "Slash Commands", click "Create New Command":

| Field            | Value                                         |
|------------------|-----------------------------------------------|
| Command          | `/kozmo`                                      |
| Request URL      | `https://<your-api-host>/slack/command`       |
| Short Desc.      | Kozmo vendor intelligence                     |
| Usage Hint       | `pending | vendor <name> | help`              |

Save the command. The signing-secret verification on `/slack/command` is identical to `/slack/interactivity` — no extra credentials needed.

## 3b. Enable the Home Tab (Phase 3)

Under "App Home":

1. Toggle "Home Tab" **ON**.
2. Under "Show Tabs" ensure "Home Tab" is checked.

Under "Event Subscriptions":

1. Toggle Events **ON**.
2. Set the Request URL to:
   ```
   https://<your-api-host>/slack/events
   ```
   Slack will send a `url_verification` challenge — Kozmo handles this automatically (no
   signing secret required for the handshake; all subsequent events are signature-verified).
3. Under "Subscribe to bot events", add:
   - `app_home_opened`
4. Save changes.

## 4. Install the App to Your Workspace

Under "Install App", click "Install to Workspace" and approve the OAuth flow.

After installation you will receive a **Bot User OAuth Token** (starts with `xoxb-`).

## 5. Configure Environment Variables

Set the following variables before starting Kozmo.Api:

```bash
# Bot token from step 4 — used to post messages to channels/DMs
KOZMO_SLACK_BOT_TOKEN=xoxb-your-token-here

# Signing secret from "Basic Information" > "App Credentials"
# Used to verify that inbound webhooks are genuinely from Slack
KOZMO_SLACK_SIGNING_SECRET=your-signing-secret-here
```

Or via user-secrets (development):
```bash
dotnet user-secrets set "Slack:BotToken" "xoxb-your-token-here"
dotnet user-secrets set "Slack:SigningSecret" "your-signing-secret-here"
```

## 6. Set Owner Channel Preference

Insert a row into `owner_channel_prefs` for the owner who should receive Slack check-ins:

```sql
INSERT OR REPLACE INTO owner_channel_prefs (owner_id, channel, slack_destination)
VALUES ('owner@example.com', 'Slack', 'C0123456789');
-- For a DM instead of a channel: use the user's Slack user ID (starts with U)
-- VALUES ('owner@example.com', 'Slack', 'U0987654321');
```

Owners without a row in this table continue to receive check-ins by email (default).

## 7. Invite the Bot to Your Channel (if using a channel)

In Slack, open the target channel and type:
```
/invite @KozmoCheckIns
```

## Testing Locally

Use ngrok to expose the local API to Slack:
```bash
ngrok http 5000
```
Set all three Request URLs in the Slack App to the ngrok HTTPS URL:
- Interactivity: `<ngrok-url>/slack/interactivity`
- Slash command:  `<ngrok-url>/slack/command`
- Events:         `<ngrok-url>/slack/events`

## Slash Command Usage

```
/kozmo pending          — list your open check-ins
/kozmo vendor <name>    — posture card for a vendor (case-insensitive substring match)
/kozmo help             — show this usage message
```

Responses are ephemeral (visible only to you). Vendor name matching is exact substring;
if multiple vendors match you'll be prompted to be more specific.

## Home Tab

Open the Kozmo app in Slack and click the **Home** tab. Kozmo publishes your open
check-ins there whenever you open the tab (`app_home_opened` event). The view is
read-only — answering still uses the button flow in check-in digest messages.

## Notes

- All inbound Slack payloads (interactivity, slash commands, events except `url_verification`)
  are verified using HMACSHA256 + the signing secret before any processing.
  Requests older than 5 minutes are rejected (replay guard).
- Phase 3 endpoints (`/slack/command`, `/slack/events`) are read-only — they never write
  beliefs, call ProcessAnswerAsync, or trigger a recompute.
- The `/kozmo vendor` command reads the stored current index/posture; it does NOT recompute.
- Any owner may click a button — identity is captured in the belief's provenance
  (`answered-by:slack-user:{userId}`) and restriction can be added as a future additive check.
- TYPED_VALUE / STATUS_SELECT questions show an "Answer in Kozmo" link to the pending queue;
  in-Slack typed input is a future phase.
