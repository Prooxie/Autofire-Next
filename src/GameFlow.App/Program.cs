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
    /// Renderer for Windows. Software by default — deliberately.
    ///
    /// <para>
    /// This was briefly switched to prefer GPU (ANGLE) on the theory that
    /// software rasterisation was behind the app's paint cost. It was not,
    /// and the switch made interactive latency dramatically WORSE on real
    /// hardware — mouse movement and clicks became barely usable — even
    /// though the in-process repaint timer showed ANGLE marginally faster
    /// (18.8 ms vs 20.0 ms).
    /// </para>
    ///
    /// <para>
    /// That gap is the lesson: ThemeSurface's own repaint timer measures
    /// only the time spent building the draw list. It cannot see GPU
    /// present and swapchain stalls, which is where ANGLE's cost lands and
    /// what the user actually feels. A faster number there does not mean a
    /// faster application, and the original pin to software — which
    /// carried no comment explaining it — was evidently load-bearing.
    /// </para>
    ///
    /// <para>
    /// The real paint cost is layer count, not the rasteriser: a DualSense
    /// theme composites ~47 megapixel-scale alpha-blended images per frame.
    /// See docs/known-issues.md P1 for the fix that actually addresses it.
    /// </para>
    ///
    /// <para>
    /// <c>GAMEFLOW_RENDERING=gpu</c> opts into ANGLE for anyone who wants
    /// to retest it on different hardware, with the software fallback
    /// retained.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Win32RenderingMode> ResolveWindowsRenderingModes()
    {
        var requested = Environment.GetEnvironmentVariable("GAMEFLOW_RENDERING");

        if (string.Equals(requested, "gpu", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requested, "angle", StringComparison.OrdinalIgnoreCase))
        {
            Log.Information("Rendering: GPU (ANGLE) requested by GAMEFLOW_RENDERING, software fallback retained.");
            return [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software];
        }

        return [Win32RenderingMode.Software];
    }
}
