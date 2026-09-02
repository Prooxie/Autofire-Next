using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime.Web;

/// <summary>
/// Whether the inbound firewall rule for the web controller exists and
/// applies to the network the machine is actually on.
/// </summary>
public enum FirewallRuleState
{
    /// <summary>Could not be determined — not Windows, or the query failed.</summary>
    Unknown = 0,

    /// <summary>No enabled inbound rule for GameFlow.</summary>
    Missing,

    /// <summary>
    /// A rule exists, but its profiles do not include the category the
    /// current network is in — so it has no effect here.
    /// </summary>
    WrongProfile,

    /// <summary>A rule exists and covers the current network.</summary>
    Active,
}

/// <summary>
/// The firewall picture the UI needs in one value.
/// </summary>
/// <param name="State">Whether an effective rule is in place.</param>
/// <param name="ActiveNetworkIsPublic">
/// True when the machine's current network is categorised Public, which
/// is the case Windows treats most restrictively and the one users hit
/// without realising.
/// </param>
/// <param name="NetworkNames">Networks in the active category, for the message.</param>
public sealed record FirewallStatus(
    FirewallRuleState State,
    bool ActiveNetworkIsPublic,
    string? NetworkNames);

/// <summary>
/// Checks for, and offers to create, the inbound Windows Firewall rule
/// the web controller needs before a phone can reach it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Binding the listener and being reachable are
/// two different things, and the failure looks identical to a bug. The
/// server can start perfectly, log
/// <c>running on http://192.168.x.x:8080</c>, and still leave the phone
/// sitting on a connection timeout, because Windows Firewall blocks
/// unsolicited inbound TCP by default.
/// </para>
/// <para>
/// <b>Existing is not the same as applying.</b> A firewall rule carries
/// the set of network categories it covers, and a rule that omits the
/// current one does nothing at all. Home Ethernet connections in
/// particular are very often left categorised Public, because Windows
/// only ever asked once. So "a rule named GameFlow exists" is the wrong
/// question — an earlier version of this class asked exactly that, and
/// consequently reported all-clear on a machine where the phone still
/// could not connect. <see cref="GetStatusAsync"/> compares the rule's
/// profiles against the profiles currently in force.
/// </para>
/// <para>
/// <b>Detection goes through the firewall COM API, not netsh output.</b>
/// It returns the profile bitmasks as numbers, where netsh returns
/// localised prose — and this app ships in eight languages. Creating the
/// rule still shells out to netsh, because that path needs elevation and
/// a UAC prompt is easier to raise around a process than around a COM
/// call.
/// </para>
/// <para>
/// <b>Public networks are the user's call.</b> The rule is created for
/// private and domain networks only. Widening it to Public means
/// accepting inbound connections on untrusted networks — cafés, hotels,
/// conference Wi-Fi — so it is a separate, explicitly labelled action
/// rather than something this class decides on the user's behalf.
/// </para>
/// </remarks>
public sealed class WindowsFirewallAccess(ILogger<WindowsFirewallAccess> logger)
{
    private readonly ILogger<WindowsFirewallAccess> logger = logger;

    /// <summary>
    /// Name the rule is created under, and looked up by. Stable: it is
    /// the identity of the rule across app restarts and upgrades.
    /// </summary>
    public const string RuleName = "GameFlow web controller";

    // INetFwRule profile bitmask, per the Windows Firewall COM API.
    private const int ProfileDomain = 0x1;
    private const int ProfilePrivate = 0x2;
    private const int ProfilePublic = 0x4;

    /// <summary>Inbound direction, per NET_FW_RULE_DIRECTION_.</summary>
    private const int DirectionInbound = 1;

    /// <summary>False on platforms where this whole concern does not apply.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Reads whether an effective inbound rule is in place, and what
    /// kind of network the machine is on.
    /// </summary>
    public Task<FirewallStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // OperatingSystem.IsWindows() rather than the IsSupported property:
        // both say the same thing, but only the direct call is a guard the
        // platform-compatibility analyzer can follow into the COM code below.
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new FirewallStatus(FirewallRuleState.Unknown, false, null));
        }

        // COM calls block; keep them off the caller's thread since this
        // runs while a dialog is opening.
        return Task.Run(ReadStatus, cancellationToken);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private FirewallStatus ReadStatus()
    {
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null || Activator.CreateInstance(policyType) is not { } policyInstance)
            {
                return new FirewallStatus(FirewallRuleState.Unknown, false, null);
            }

            dynamic policy = policyInstance;

            // Bitmask of the categories currently in force. More than one
            // can apply at once on a machine with several adapters up —
            // this one has Ethernet, Tailscale and ZeroTier, for instance.
            int activeProfiles = policy.CurrentProfileTypes;
            var isPublic = (activeProfiles & ProfilePublic) != 0;

            var state = FirewallRuleState.Missing;
            var uncovered = 0;
            foreach (dynamic rule in policy.Rules)
            {
                string? name = rule.Name;
                if (!string.Equals(name, RuleName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (rule.Direction != DirectionInbound || rule.Enabled != true)
                {
                    continue;
                }

                int ruleProfiles = rule.Profiles;

                // Covered means covered EVERYWHERE, not somewhere.
                //
                // Windows picks the firewall profile per network adapter,
                // so a machine with several networks up is on several
                // profiles at once. Asking merely whether the rule and the
                // active set overlap gets this wrong in the exact case
                // users hit: a private VPN adapter alongside a home
                // Ethernet that Windows filed as Public. The overlap on
                // Private says "allowed" while the phone, which is on the
                // Ethernet, still cannot connect.
                //
                // So the question is whether any active profile is left
                // UNCOVERED. A rule set to all profiles (int.MaxValue)
                // covers everything and falls out of this naturally.
                uncovered = activeProfiles & ~ruleProfiles;
                state = uncovered == 0
                    ? FirewallRuleState.Active
                    : FirewallRuleState.WrongProfile;

                if (state == FirewallRuleState.Active)
                {
                    break;
                }
            }

            logger.LogDebug(
                "Web controller firewall: state={State} activeProfiles=0x{Active:X} uncovered=0x{Uncovered:X} public={Public}.",
                state, activeProfiles, uncovered, isPublic);

            // The message names the networks the rule does NOT reach, which
            // is the actionable half; naming every active profile would
            // include the ones already working.
            return new FirewallStatus(
                state,
                isPublic,
                DescribeProfiles(state == FirewallRuleState.WrongProfile ? uncovered : activeProfiles));
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Web controller: could not query the Windows Firewall.");
            return new FirewallStatus(FirewallRuleState.Unknown, false, null);
        }
    }

    private static string DescribeProfiles(int profiles)
    {
        var names = new List<string>(3);
        if ((profiles & ProfileDomain) != 0) { names.Add("Domain"); }
        if ((profiles & ProfilePrivate) != 0) { names.Add("Private"); }
        if ((profiles & ProfilePublic) != 0) { names.Add("Public"); }
        return names.Count > 0 ? string.Join(", ", names) : "none";
    }

    /// <summary>
    /// Asks Windows to create the inbound rule, raising a UAC prompt.
    /// </summary>
    /// <param name="port">TCP port to open.</param>
    /// <param name="includePublicNetworks">
    /// Whether to cover Public networks too. Off by default: it accepts
    /// inbound connections on untrusted networks, so it must be a
    /// deliberate choice made with that stated.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait, not the prompt.</param>
    /// <returns>
    /// <see langword="true"/> when the rule was created. Declining the UAC
    /// prompt returns <see langword="false"/> and is not an error — it is
    /// an answer.
    /// </returns>
    public async Task<bool> TryAddInboundRuleAsync(
        int port,
        bool includePublicNetworks = false,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return false;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath))
        {
            logger.LogWarning("Web controller: cannot add a firewall rule without the executable path.");
            return false;
        }

        var profiles = includePublicNetworks ? "private,domain,public" : "private,domain";

        // Delete-then-add, so repeated presses replace the rule instead of
        // stacking duplicates of it — and so widening it to Public later
        // replaces the narrower rule rather than sitting beside it. cmd /c
        // runs the pair under a single elevation prompt; the delete is
        // allowed to fail on first run.
        var command =
            $"netsh advfirewall firewall delete rule name=\"{RuleName}\" dir=in >nul 2>&1 & " +
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" " +
            $"dir=in action=allow protocol=TCP localport={port} " +
            $"program=\"{executablePath}\" profile={profiles} " +
            $"description=\"Lets phones and OBS on your local network reach the GameFlow web controller.\"";

        try
        {
            var exitCode = await RunElevatedAsync("cmd.exe", $"/c {command}", cancellationToken)
                .ConfigureAwait(false);

            if (exitCode == 0)
            {
                logger.LogInformation(
                    "Web controller: added inbound firewall rule {Rule} for TCP {Port} ({Profiles}).",
                    RuleName, port, profiles);
                return true;
            }

            logger.LogWarning(
                "Web controller: adding the firewall rule exited with {ExitCode}; the port is probably still blocked.",
                exitCode);
            return false;
        }
        catch (Exception exception)
        {
            // Declining UAC surfaces as Win32Exception 1223.
            if (exception is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 })
            {
                logger.LogInformation("Web controller: the firewall prompt was declined.");
            }
            else
            {
                logger.LogWarning(exception, "Web controller: adding the firewall rule failed.");
            }

            return false;
        }
    }

    /// <summary>
    /// Opens the Windows network-category page, where a home network can
    /// be switched from Public to Private.
    /// </summary>
    /// <remarks>
    /// Deliberately a hand-off rather than an automated change: the
    /// category governs discovery and sharing for the whole machine, not
    /// just this app, so it is not GameFlow's to set.
    /// </remarks>
    public void OpenNetworkSettings()
    {
        if (!IsSupported)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Web controller: could not open Windows network settings.");
        }
    }

    /// <summary>Runs a console command elevated and returns its exit code.</summary>
    private static async Task<int> RunElevatedAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            // Elevation goes through the shell verb, which rules out
            // redirection — the two are mutually exclusive on Windows.
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Verb = "runas",
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}
