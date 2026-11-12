using System;

namespace SpeechToText;

/// <summary>
/// Rules for the <c>User/STT.Trigger</c> switch (SPEC §6.2). Kept free of engine types so it can be unit-tested.
/// </summary>
public static class TriggerPolicy
{
    /// <summary>
    /// Whether a change to the trigger variable counts.
    /// </summary>
    /// <param name="modifiedByLocalUser">
    /// True when the last writer was the local user, or when nobody has written the value yet (a <c>null</c>
    /// <c>LastModifyingUser</c> is our own initial value).
    /// </param>
    public static bool Accept(bool modifiedByLocalUser, TriggerSource source) =>
        modifiedByLocalUser || source == TriggerSource.Anyone;
}

/// <summary>
/// Optional safety net: asks for the trigger to be switched off once it has been open longer than a limit. Pure state
/// machine over an injectable clock.
/// </summary>
public sealed class TriggerAutoOff
{
    private readonly Func<long> _clockMs;
    private long? _openedAt;

    public TriggerAutoOff(Func<long>? clockMs = null) => _clockMs = clockMs ?? (() => Environment.TickCount64);

    /// <summary>How long the trigger has been open without interruption, or zero when it is closed.</summary>
    public TimeSpan OpenFor => _openedAt is long t ? TimeSpan.FromMilliseconds(_clockMs() - t) : TimeSpan.Zero;

    /// <summary>
    /// Call regularly with the current trigger state. Returns true while the trigger is open and past
    /// <paramref name="limitSeconds"/> (so a failed write is retried on the next call); false when closed, under the
    /// limit, or when <paramref name="limitSeconds"/> is 0 or less (disabled). Closing the trigger restarts the clock.
    /// </summary>
    public bool ShouldSwitchOff(bool open, int limitSeconds)
    {
        if (!open) { _openedAt = null; return false; }
        _openedAt ??= _clockMs();
        return limitSeconds > 0 && _clockMs() - _openedAt.Value >= limitSeconds * 1000L;
    }
}
