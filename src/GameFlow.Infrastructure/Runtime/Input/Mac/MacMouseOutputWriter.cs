using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime.Input.Mac;

/// <summary>
/// macOS counterpart to Win32MouseOutputWriter / LinuxMouseOutputWriter
/// — moves the cursor via CGEventPost.
///
/// <para>
/// <b>Architecturally different from the other two platforms.</b>
/// SendInput (Windows) and uinput (Linux) both inject genuine RELATIVE
/// motion. CGEventCreateMouseEvent does not — it takes an ABSOLUTE
/// screen point. This writer queries the real cursor position once
/// (via a throwaway CGEventCreate/CGEventGetLocation probe) and then
/// tracks its own running position internally, adding each requested
/// delta before posting an absolute-position mouse-moved event.
/// </para>
///
/// <para>
/// <b>Known limitation:</b> this does NOT set kCGMouseEventDeltaX/Y on
/// the synthesized event — only the absolute position. Most consumers
/// (anything tracking cursor position frame-to-frame, which is most
/// UI and many games) work fine with that. A game reading raw HID
/// deltas directly (bypassing cursor tracking entirely, common for
/// "mouse-look" in some titles) may not respond to this. Setting the
/// delta would mean reintroducing the CGEventField enum, whose exact
/// ordering was recalled rather than read from a header and which left
/// this file when the read path moved to IOHIDManager — a wrong index
/// silently writes some OTHER field on the event, which is worse than
/// the honest limitation of only serving absolute-tracking consumers.
/// </para>
///
/// <para>Not verified against a live device — no macOS SDK or
/// toolchain exists anywhere in this build environment.</para>
/// </summary>
public sealed class MacMouseOutputWriter : IMouseOutputWriter
{
    private readonly ILogger<MacMouseOutputWriter> logger;
    private readonly Lock gate = new();
    private bool hasPosition;
    private double currentX;
    private double currentY;
    private bool loggedProbeFailure;

    public MacMouseOutputWriter(ILogger<MacMouseOutputWriter> logger)
    {
        this.logger = logger;
    }

    public void MoveRelative(float dx, float dy)
    {
        if (dx == 0f && dy == 0f)
        {
            return;
        }

        lock (gate)
        {
            if (!hasPosition && !TryEstablishStartingPosition())
            {
                return; // couldn't determine where the real cursor is — skip rather than jump to (0,0)
            }

            currentX += dx;
            currentY += dy;

            var point = new MacEventInterop.CGPoint(currentX, currentY);
            var moveEvent = MacEventInterop.CGEventCreateMouseEvent(
                IntPtr.Zero, MacEventInterop.kCGEventMouseMoved, point, mouseButton: 0);

            if (moveEvent == IntPtr.Zero)
            {
                return;
            }

            MacEventInterop.CGEventPost(MacEventInterop.kCGHIDEventTap, moveEvent);
            MacEventInterop.CFRelease(moveEvent);
        }
    }

    /// <summary>Caller already holds gate.</summary>
    private bool TryEstablishStartingPosition()
    {
        var probe = MacEventInterop.CGEventCreate(IntPtr.Zero);
        if (probe == IntPtr.Zero)
        {
            if (!loggedProbeFailure)
            {
                loggedProbeFailure = true;
                logger.LogWarning("macOS: could not query the current cursor position — touchpad mouse mapping will have no effect.");
            }
            return false;
        }

        var location = MacEventInterop.CGEventGetLocation(probe);
        MacEventInterop.CFRelease(probe);

        currentX = location.X;
        currentY = location.Y;
        hasPosition = true;
        return true;
    }
}
