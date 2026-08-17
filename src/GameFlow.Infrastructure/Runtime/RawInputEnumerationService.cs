using GameFlow.Infrastructure.Runtime.RawInput;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime;

/// <summary>
/// Periodically enumerates keyboards and mice via Windows Raw Input and
/// publishes them to the <see cref="InputDeviceCatalog"/> under the
/// <c>"rawinput"</c> source, so they appear in the Devices view next to
/// the SDL3 controllers without either backend clobbering the other.
///
/// <para>Enumeration is a cheap query, but doing it off the input hot
/// loop (here, on a ~2 s timer) keeps it fully decoupled from the SDL
/// pump. The catalog's change-gated publish means a stable set of
/// devices produces no event churn.</para>
///
/// <para><b>Windows, Linux, and macOS.</b> On other platforms the
/// service starts and immediately idles — no scanner exists yet.</para>
/// </summary>
public sealed class RawInputEnumerationService(
    InputDeviceCatalog catalog,
    ILogger<RawInputEnumerationService> logger) : BackgroundService
{
    private const string SourceKey = "rawinput";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // Raw Input / evdev / IOHIDManager: nothing to enumerate on this platform.
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var devices = OperatingSystem.IsWindows() ? RawInputDeviceScanner.Scan()
                    : OperatingSystem.IsLinux() ? Input.Linux.LinuxInputDeviceScanner.Scan(logger)
                    : Input.Mac.MacInputDeviceScanner.Scan();
                catalog.ReplaceDevices(SourceKey, devices);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Keyboard/mouse enumeration failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Drop our contribution so stale keyboard/mouse rows don't linger.
        try
        {
            catalog.ReplaceDevices(SourceKey, null);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to clear Raw Input devices on shutdown.");
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
