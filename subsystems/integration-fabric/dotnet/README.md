# integration-fabric/dotnet

Integration adapter layer for external calendar and mail sources.

## Projects

### If.Contracts
Integration-neutral interfaces and DTOs. No Microsoft SDK references — all adapters implement these contracts so the scoring pipeline never takes a hard dependency on any specific provider.

### If.MicrosoftGraph
Microsoft Graph adapter implementing `ICalendarSource`, `IMailSource`, and `IIntegrationCheckpointStore`. Phase 1 scaffold only — all methods throw `NotImplementedException`. Phase 2 will add Microsoft Graph authentication and real API calls.

### If.Tests
Smoke tests verifying instantiation and interface conformance, plus an architecture test enforcing that `If.Contracts` has no dependency on `Microsoft.Graph`.

## Phase 2
Phase 2 will add Microsoft Graph authentication (OAuth 2.0 client-credentials or delegated flow via `MicrosoftGraphTokenProvider`) and implement the Graph API calls in the adapter classes.
