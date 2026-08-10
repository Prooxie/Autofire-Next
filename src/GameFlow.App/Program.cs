using Avalonia;
using Serilog;

namespace GameFlow.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            _ = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Application terminated unexpectedly.");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = ResolveWindowsRenderingModes()
            });
        }

        return builder;
    }

    /// <summary>
    /// Renderer preference for Windows, GPU first.
    ///
    /// <para>
    /// This used to be pinned to <see cref="Win32RenderingMode.Software"/>
    /// with no explanation, and that single line was the app's dominant
    /// performance problem. Controller themes are large layered bitmaps —
    /// a 1467x816 canvas with 23 image layers is typical — drawn scaled
    /// with high-quality interpolation on every frame. On the CPU that
    /// measured 15.3 ms per surface repaint, which does not fit in a
    /// frame budget at any usable refresh rate, and with several surfaces
    /// on screen the dispatcher saturated and the window stopped
    /// responding.
    /// </para>
    ///
    /// <para>
    /// The list is a preference order, not a demand: Avalonia walks it and
    /// takes the first mode that initialises, so a machine where ANGLE
    /// cannot start still gets a working — if slower — window rather than
    /// a crash. Software is kept last for exactly that reason.
    /// </para>
    ///
    /// <para>
    /// <c>GAMEFLOW_RENDERING=software</c> forces the old behaviour without
    /// a rebuild, for a driver that renders incorrectly rather than
    /// failing outright — the one case the automatic fallback cannot
    /// detect.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Win32RenderingMode> ResolveWindowsRenderingModes()
    {
        var requested = Environment.GetEnvironmentVariable("GAMEFLOW_RENDERING");

        if (string.Equals(requested, "software", StringComparison.OrdinalIgnoreCase))
        {
            Log.Information("Rendering: software forced by GAMEFLOW_RENDERING.");
            return [Win32RenderingMode.Software];
        }

        return [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software];
    }
}
