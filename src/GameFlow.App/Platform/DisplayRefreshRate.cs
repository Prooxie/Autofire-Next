using System.Runtime.InteropServices;
using Serilog;

namespace GameFlow.App.Platform;

/// <summary>
/// The primary display's refresh rate, so the dashboard can tick at the
/// rate the screen can actually show rather than at a number picked in
/// advance.
///
/// <para>
/// Avalonia's <c>Screen</c> exposes bounds, scaling and orientation but
/// not refresh rate, so this asks the platform directly. Windows only;
/// everywhere else the caller falls back to its own default, which is why
/// this reports failure rather than guessing.
/// </para>
///
/// <para>
/// Deliberately the PRIMARY display rather than the one the window
/// happens to be on. Following the window would mean re-negotiating the
/// tick every time it was dragged between monitors of different rates,
/// and every change restarts the timer — which is itself a source of
/// stutter, for a difference nobody asked for.
/// </para>
/// </summary>
internal static class DisplayRefreshRate
{
    /// <summary><c>VREFRESH</c> from wingdi.h.</summary>
    private const int VerticalRefresh = 116;

    /// <summary>
    /// GetDeviceCaps reports 0 or 1 for "hardware default", meaning it
    /// does not know. Neither is a rate, and both would be catastrophic
    /// if treated as one.
    /// </summary>
    private const int LowestMeaningfulHz = 2;

    // DllImport rather than LibraryImport: the source generator requires
    // AllowUnsafeBlocks on the whole project, and three blittable
    // signatures with no marshalling are not worth widening that for.
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);

    /// <summary>
    /// Returns the primary display's refresh rate in Hz, or
    /// <see langword="null"/> when the platform cannot say.
    /// </summary>
    public static int? TryGetPrimaryHz()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var screenDc = IntPtr.Zero;
        try
        {
            // A null window handle asks for the whole primary screen.
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return null;
            }

            var hz = GetDeviceCaps(screenDc, VerticalRefresh);
            return hz >= LowestMeaningfulHz ? hz : null;
        }
        catch (Exception exception)
        {
            // Never fatal: not knowing the refresh rate costs a default,
            // and this runs during window startup.
            Log.Debug(exception, "Could not read the display refresh rate.");
            return null;
        }
        finally
        {
            if (screenDc != IntPtr.Zero)
            {
                _ = ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
