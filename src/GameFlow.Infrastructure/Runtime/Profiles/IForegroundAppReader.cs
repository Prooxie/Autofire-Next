using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameFlow.Infrastructure.Runtime.Profiles;

/// <summary>
/// Reports which application the user is currently looking at, as a
/// process name without its extension — <c>eldenring</c>, <c>chrome</c>.
///
/// <para>
/// The process name rather than the window title on purpose. A title is
/// user-visible text that changes with the loaded save, the open
/// document, or the page, and it is localised; a rule written against
/// one would stop matching the moment the user opened a different level.
/// The executable name is the stable identity of "which game is this".
/// </para>
/// </summary>
public interface IForegroundAppReader
{
    /// <summary>
    /// The foreground application's process name, lower-cased and
    /// without <c>.exe</c>, or <see langword="null"/> when there is no
    /// foreground window or this platform cannot tell.
    /// </summary>
    string? GetForegroundProcessName();
}

/// <summary>
/// Default for platforms with no implementation. Reporting "I don't
/// know" rather than guessing is what keeps
/// <see cref="ProfileAutoSwitchService"/> inert instead of switching
/// profiles at random on a machine it cannot read.
/// </summary>
public sealed class NullForegroundAppReader : IForegroundAppReader
{
    public string? GetForegroundProcessName() => null;
}

/// <summary>
/// Windows implementation: <c>GetForegroundWindow</c> →
/// <c>GetWindowThreadProcessId</c> → the process's image name.
///
/// <para>
/// The image name comes from <c>QueryFullProcessImageName</c> rather
/// than <see cref="Process.ProcessName"/>. Both answer the same question
/// for ordinary processes, but <c>ProcessName</c> opens a handle with
/// more access than this needs and throws on processes the app cannot
/// touch — which for a tool people run alongside anti-cheat is a real
/// case, not a hypothetical. <c>QueryFullProcessImageName</c> works with
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, the access level
/// specifically added so an unprivileged process can ask what another
/// one is.
/// </para>
///
/// <para>
/// P/Invoke style follows <c>WindowsRawInputReader</c> and
/// <c>Win32MouseOutputWriter</c> in the sibling Input namespace:
/// <c>DllImport</c>, not <c>LibraryImport</c>.
/// </para>
/// </summary>
public sealed class WindowsForegroundAppReader : IForegroundAppReader
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// Cache of the last window handle we resolved. The foreground
    /// window does not change between most polls, and re-opening a
    /// process handle every second to learn the same answer is waste on
    /// a service whose whole job is to be unnoticeable.
    /// </summary>
    private IntPtr lastWindow;
    private string? lastName;

    public string? GetForegroundProcessName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            // No foreground window at all — the lock screen, or the
            // moment between one app closing and the next taking focus.
            // Deliberately does NOT clear the cache: this is transient,
            // and treating it as "the user left the game" would bounce
            // the profile back and forth across an alt-tab.
            return lastName;
        }

        if (window == lastWindow)
        {
            return lastName;
        }

        lastWindow = window;
        lastName = ResolveProcessName(window);
        return lastName;
    }

    private static string? ResolveProcessName(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return null;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, bInheritHandle: false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return null;
            }

            // Path → bare name, lower-cased so rules match regardless of
            // how the user typed them and of how the filesystem cased the
            // executable.
            return Path.GetFileNameWithoutExtension(buffer.ToString(0, size)).ToLowerInvariant();
        }
        catch (Exception)
        {
            // A process that exited between the handle opening and the
            // query. "Unknown" is the right answer and the next poll
            // will have a live one.
            return null;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
