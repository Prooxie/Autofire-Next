using GameFlow.Core.Models;

namespace GameFlow.Infrastructure.Runtime;

/// <summary>
/// Combines the snapshots of a slot's multiple input devices into one
/// controller snapshot. Each stick takes whichever device's vector has the
/// larger magnitude (so a keyboard driving the left stick and a mouse
/// driving the right stick merge without fighting), triggers take the max,
/// and buttons are OR'd (any device pressing it counts). This is what lets
/// e.g. WASD + mouse-aim act as a single virtual pad.
/// </summary>
public static class ControllerSnapshotMerger
{
    public static ControllerSnapshot Merge(string deviceName, IReadOnlyList<ControllerSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return ControllerSnapshot.Empty(deviceName);
        }
        if (snapshots.Count == 1)
        {
            return snapshots[0];
        }

        StickVector left = new(0, 0);
        StickVector right = new(0, 0);
        float leftTrigger = 0, rightTrigger = 0;
        var buttons = ButtonMask.Empty;

        foreach (var snapshot in snapshots)
        {
            if (MagnitudeSquared(snapshot.LeftStick) > MagnitudeSquared(left))
            {
                left = snapshot.LeftStick;
            }
            if (MagnitudeSquared(snapshot.RightStick) > MagnitudeSquared(right))
            {
                right = snapshot.RightStick;
            }
            if (snapshot.LeftTrigger > leftTrigger) leftTrigger = snapshot.LeftTrigger;
            if (snapshot.RightTrigger > rightTrigger) rightTrigger = snapshot.RightTrigger;

            // A button is down on the merged pad if it is down on any
            // contributing one — which is exactly a bitwise OR now, rather
            // than a walk over one dictionary per device per frame.
            buttons |= snapshot.Buttons;
        }

        return new ControllerSnapshot
        {
            DeviceName   = deviceName,
            LeftStick    = left,
            RightStick   = right,
            LeftTrigger  = leftTrigger,
            RightTrigger = rightTrigger,
            Buttons      = buttons,
        };
    }

    private static float MagnitudeSquared(StickVector v) => (v.X * v.X) + (v.Y * v.Y);
}
