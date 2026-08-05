using GameFlow.Core.Models;

namespace GameFlow.Core.Pipeline;

/// <summary>
/// Applies a device's <see cref="DeviceSettings"/> to its raw physical
/// snapshot. Runs BEFORE the mapping pipeline and before multi-device
/// merging, so every rule downstream — and every other device sharing
/// the slot — sees an already-conditioned signal.
///
/// <para>
/// Radial, not per-axis. Deadzone and saturation are applied to the
/// stick's MAGNITUDE with its direction preserved, which is what keeps
/// a circular deadzone circular. Treating each axis independently (the
/// naive approach) produces a square dead region, so a diagonal push
/// escapes the deadzone at a different physical distance than a
/// straight one — the classic "diagonals feel wrong" bug.
/// </para>
/// </summary>
public static class DeviceSettingsProcessor
{
    /// <summary>Returns <paramref name="snapshot"/> conditioned by <paramref name="settings"/>. Pure — no state, no side effects.</summary>
    public static ControllerSnapshot Apply(ControllerSnapshot snapshot, DeviceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return snapshot with
        {
            LeftStick = ApplyStick(snapshot.LeftStick, settings.LeftStick),
            RightStick = ApplyStick(snapshot.RightStick, settings.RightStick),
            LeftTrigger = ApplyTrigger(snapshot.LeftTrigger, settings.LeftTrigger),
            RightTrigger = ApplyTrigger(snapshot.RightTrigger, settings.RightTrigger)
        };
    }

    /// <summary>Radial deadzone + saturation + curve + sensitivity + per-axis invert.</summary>
    public static StickVector ApplyStick(StickVector stick, StickSettings settings)
    {
        var x = settings.InvertX ? -stick.X : stick.X;
        var y = settings.InvertY ? -stick.Y : stick.Y;

        var magnitude = MathF.Sqrt((x * x) + (y * y));
        if (magnitude <= 0.0001f)
        {
            return StickVector.Zero;
        }

        // Unit direction, preserved through everything below.
        var dirX = x / magnitude;
        var dirY = y / magnitude;

        var deadzone = Math.Clamp(settings.Deadzone, 0f, 0.99f);
        if (magnitude <= deadzone)
        {
            return StickVector.Zero;
        }

        // Rescale [deadzone, fullAt] onto [0, 1] so the usable travel
        // spans the full output range rather than starting partway up.
        var fullAt = Math.Clamp(settings.FullAt, deadzone + 0.01f, 1f);
        var normalized = Math.Clamp((magnitude - deadzone) / (fullAt - deadzone), 0f, 1f);

        normalized = settings.Curve switch
        {
            StickCurve.Precision => normalized * normalized,
            StickCurve.Aggressive => MathF.Sqrt(normalized),
            _ => normalized
        };

        // Anti-deadzone lifts the floor: any real movement now lands at
        // or above the level a game's own internal deadzone would ignore.
        var antiDeadzone = Math.Clamp(settings.AntiDeadzone, 0f, 0.95f);
        if (antiDeadzone > 0f && normalized > 0f)
        {
            normalized = antiDeadzone + (normalized * (1f - antiDeadzone));
        }

        normalized = Math.Clamp(normalized * settings.Sensitivity, 0f, 1f);

        return new StickVector(dirX * normalized, dirY * normalized);
    }

    public static float ApplyTrigger(float value, TriggerSettings settings)
    {
        var raw = Math.Clamp(settings.Invert ? 1f - value : value, 0f, 1f);

        var deadzone = Math.Clamp(settings.Deadzone, 0f, 0.99f);
        if (raw <= deadzone)
        {
            return 0f;
        }

        var fullAt = Math.Clamp(settings.FullAt, deadzone + 0.01f, 1f);
        var normalized = Math.Clamp((raw - deadzone) / (fullAt - deadzone), 0f, 1f);

        return Math.Clamp(normalized * settings.Sensitivity, 0f, 1f);
    }

    /// <summary>
    /// True when these settings would leave the signal untouched. Lets a
    /// caller skip the work entirely for an untuned device — which is
    /// most of them, most of the time, on a path that runs per device
    /// per tick.
    /// </summary>
    public static bool IsIdentity(DeviceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return IsStickIdentity(settings.LeftStick)
            && IsStickIdentity(settings.RightStick)
            && IsTriggerIdentity(settings.LeftTrigger)
            && IsTriggerIdentity(settings.RightTrigger);
    }

    private static bool IsStickIdentity(StickSettings s) =>
        s.Deadzone <= 0f && s.AntiDeadzone <= 0f && s.FullAt >= 1f
        && Math.Abs(s.Sensitivity - 1f) < 0.0001f
        && s.Curve == StickCurve.Linear && !s.InvertX && !s.InvertY;

    private static bool IsTriggerIdentity(TriggerSettings t) =>
        t.Deadzone <= 0f && t.FullAt >= 1f
        && Math.Abs(t.Sensitivity - 1f) < 0.0001f && !t.Invert;
}
