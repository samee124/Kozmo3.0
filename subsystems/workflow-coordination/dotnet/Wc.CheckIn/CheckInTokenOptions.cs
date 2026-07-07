namespace Wc.CheckIn;

/// <summary>
/// Options for generating and validating signed check-in quick-answer capability tokens.
/// Registered in DI by Program.cs; injected into BrevoCheckInTransport and CheckInConfirmModel.
/// </summary>
public sealed record CheckInTokenOptions(
    string Secret,
    int    TtlDays,
    string UiBaseUrl,
    string ApiBaseUrl);
