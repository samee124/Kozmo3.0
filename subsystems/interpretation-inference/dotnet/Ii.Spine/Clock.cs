namespace Ii.Spine;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class WallClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class FixedClock : IClock
{
    private readonly DateTimeOffset _at;
    public FixedClock(DateTimeOffset at) => _at = at;
    public DateTimeOffset UtcNow => _at;
}

/// <summary>
/// Canonical demo as-of. All seeding, reset, and replay paths inject DemoClock.Fixed
/// into IiFacade so the pipeline never reads the wall clock in the demo path.
/// </summary>
public static class DemoClock
{
    public static readonly DateTimeOffset AsOf  = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);
    public static readonly IClock         Fixed = new FixedClock(AsOf);
}
