namespace Wc.Contracts;

public enum CheckInChannel { Email, Slack }

/// <summary>
/// Per-owner delivery channel preference.
/// When no row exists for an owner, Email is the default.
/// SlackDestination holds a Slack channel id (e.g. "C0123456" / "#kozmo-checkins")
/// or a Slack user id for DM; null when Channel == Email.
/// </summary>
public sealed record OwnerChannelPreference(
    string         OwnerId,
    CheckInChannel Channel,
    string?        SlackDestination
);
