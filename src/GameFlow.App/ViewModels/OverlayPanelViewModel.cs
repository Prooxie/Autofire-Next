using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using GameFlow.Infrastructure.Runtime.Web;
using GameFlow.Infrastructure.Runtime.Web.Overlay;

namespace GameFlow.App.ViewModels;

/// <summary>
/// Backs the Settings dialog's stream-overlay section: pick a controller
/// and a skin, get a URL to paste into an OBS browser source.
///
/// <para>
/// Its own view-model rather than more surface on
/// <see cref="ShellViewModel"/>, for the same reason
/// <see cref="MotionServerPanelViewModel"/> is — see that type.
/// </para>
///
/// <para>
/// Nothing here is persisted. The URL is derived from the pickers every
/// time, and the pickers exist only to build it; once the URL is in OBS,
/// OBS owns it. Saving a "current overlay theme" would imply the app
/// drives what a browser source shows, which it does not — the URL does.
/// </para>
/// </summary>
public sealed class OverlayPanelViewModel : ViewModelBase
{
    private readonly WebControllerServer server;
    private readonly OverlayFeed feed;
    private readonly WindowsFirewallAccess firewallAccess;

    public OverlayPanelViewModel(WebControllerServer server, OverlayFeed feed, WindowsFirewallAccess firewall)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
        this.feed = feed ?? throw new ArgumentNullException(nameof(feed));
        this.firewallAccess = firewall ?? throw new ArgumentNullException(nameof(firewall));

        AllowThroughFirewallCommand = new AsyncRelayCommand(AllowThroughFirewallAsync);
        AllowOnPublicNetworkCommand = new AsyncRelayCommand(AllowOnPublicNetworkAsync);
        OpenNetworkSettingsCommand = new RelayCommand(firewall.OpenNetworkSettings);

        Refresh();
    }

    // ─── Reachability ─────────────────────────────────────────────────
    //
    // Binding the port and being reachable are different things, and the
    // gap between them is the single most confusing failure this feature
    // has: the address in the box is correct, the log says the server is
    // running, and the phone still times out — because Windows Firewall
    // drops the inbound connection and says so to nobody.
    //
    // The subtler half is that a rule can exist and still not apply. A
    // firewall rule names the network categories it covers, and home
    // Ethernet is very often left categorised Public because Windows only
    // asked once. So the rule sits there, enabled, covering Private, on a
    // machine whose network is Public — and the phone still cannot
    // connect. These members distinguish the two cases, because the fix
    // is different for each.

    private FirewallStatus firewall = new(FirewallRuleState.Unknown, false, null);
    private bool firewallBusy;

    /// <summary>Requests the inbound firewall rule; raises a UAC prompt.</summary>
    public ICommand AllowThroughFirewallCommand { get; }

    /// <summary>Widens the rule to Public networks; raises a UAC prompt.</summary>
    public ICommand AllowOnPublicNetworkCommand { get; }

    /// <summary>Opens Windows' own network-category settings.</summary>
    public ICommand OpenNetworkSettingsCommand { get; }

    /// <summary>
    /// Plain-language state of "can a phone actually reach this?".
    /// </summary>
    public string ReachabilityMessage
    {
        get
        {
            if (!server.IsRunning)
            {
                return "The web server is not running.";
            }

            if (server.ListenUrl?.Contains("localhost", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "Serving this PC only — Windows would not let GameFlow listen on the network. " +
                       "Running GameFlow as administrator once is usually enough to fix this.";
            }

            return firewall.State switch
            {
                FirewallRuleState.Active =>
                    "Windows Firewall is allowing incoming connections on this port.",

                FirewallRuleState.WrongProfile =>
                    $"GameFlow is allowed through Windows Firewall, but not on this PC's " +
                    $"{firewall.NetworkNames} network — and that is the one a phone would connect over, " +
                    "so it still times out. Setting that network to Private in Windows is the safer fix; " +
                    "allowing Public networks works too.",

                FirewallRuleState.Missing =>
                    "Windows Firewall has no rule for GameFlow, so phones will time out trying to " +
                    "reach this address. Allow it through to fix that.",

                _ => "If a phone cannot load this address, Windows Firewall is the usual cause.",
            };
        }
    }

    /// <summary>True while the reachability state is a known problem, so the view can call it out.</summary>
    public bool HasReachabilityWarning =>
        server.IsRunning &&
        (firewall.State is FirewallRuleState.Missing or FirewallRuleState.WrongProfile ||
         server.ListenUrl?.Contains("localhost", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// Gates the "Allow through Windows Firewall" button: only offered
    /// where it can help, and never while a prompt is already open.
    /// </summary>
    public bool CanAllowThroughFirewall =>
        WindowsFirewallAccess.IsSupported && server.IsRunning && !firewallBusy &&
        firewall.State is FirewallRuleState.Missing or FirewallRuleState.Unknown;

    /// <summary>
    /// Shown only when a rule is already in place but the current network
    /// is outside it — the one case where widening to Public is the
    /// answer rather than a needless exposure.
    /// </summary>
    public bool CanAllowOnPublicNetwork =>
        WindowsFirewallAccess.IsSupported && server.IsRunning && !firewallBusy &&
        firewall.State == FirewallRuleState.WrongProfile;

    /// <summary>
    /// Re-reads the firewall state.
    /// </summary>
    /// <remarks>
    /// Runs off the constructor path deliberately — it walks the firewall
    /// rule collection over COM, and blocking the Settings dialog opening
    /// on that would be a poor trade for a line of status text.
    /// </remarks>
    public async Task RefreshReachabilityAsync()
    {
        firewall = await firewallAccess.GetStatusAsync().ConfigureAwait(true);
        RaiseReachabilityChanged();
    }

    private Task AllowThroughFirewallAsync() => AddRuleAsync(includePublic: false);

    private Task AllowOnPublicNetworkAsync() => AddRuleAsync(includePublic: true);

    private async Task AddRuleAsync(bool includePublic)
    {
        firewallBusy = true;
        RaiseReachabilityChanged();
        try
        {
            await firewallAccess.TryAddInboundRuleAsync(server.Port, includePublic).ConfigureAwait(true);

            // Re-read rather than assume: the user may have declined the
            // UAC prompt, and the rule's real profiles are what matters.
            firewall = await firewallAccess.GetStatusAsync().ConfigureAwait(true);
        }
        finally
        {
            firewallBusy = false;
            RaiseReachabilityChanged();
        }
    }

    private void RaiseReachabilityChanged()
    {
        OnPropertyChanged(nameof(ReachabilityMessage));
        OnPropertyChanged(nameof(HasReachabilityWarning));
        OnPropertyChanged(nameof(CanAllowThroughFirewall));
        OnPropertyChanged(nameof(CanAllowOnPublicNetwork));
    }

    /// <summary>Installed skins, plus a leading "match the controller" entry.</summary>
    public ObservableCollection<OverlayChoice> Themes { get; } = [];

    /// <summary>Configured controllers, plus a leading "the first one" entry.</summary>
    public ObservableCollection<OverlayChoice> Slots { get; } = [];

    public OverlayChoice? SelectedTheme
    {
        get;
        set
        {
            if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(Url)); }
        }
    }

    public OverlayChoice? SelectedSlot
    {
        get;
        set
        {
            if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(Url)); }
        }
    }

    /// <summary>
    /// True to show the pad in the streamer's hands instead of the
    /// virtual controller the game receives.
    /// </summary>
    public bool ShowPhysical
    {
        get;
        set
        {
            if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(Url)); }
        }
    }

    /// <summary>
    /// Address a phone opens to become a controller. The web server is shared
    /// with the OBS overlay, so this lives beside the overlay URL rather than
    /// duplicating server state in another view-model.
    /// </summary>
    public string PhoneControllerUrl => server.IsRunning && !string.IsNullOrEmpty(server.ListenUrl)
        ? server.ListenUrl
        : "The web server is not running, so phones cannot connect yet.";

    /// <summary>True when <see cref="PhoneControllerUrl"/> is copyable.</summary>
    public bool CanCopyPhoneControllerUrl => server.IsRunning && !string.IsNullOrEmpty(server.ListenUrl);

    /// <summary>
    /// The URL to paste into OBS, or an explanation of why there isn't
    /// one yet. Recomputed rather than cached — it is three string
    /// concatenations and it is read when a combo box changes, not per
    /// frame.
    /// </summary>
    public string Url
    {
        get
        {
            if (!server.IsRunning || string.IsNullOrEmpty(server.ListenUrl))
            {
                return "The web server is not running, so there is no overlay URL yet.";
            }

            var query = new List<string>();
            if (!string.IsNullOrEmpty(SelectedTheme?.Id)) { query.Add("theme=" + Uri.EscapeDataString(SelectedTheme.Id)); }
            if (!string.IsNullOrEmpty(SelectedSlot?.Id)) { query.Add("slot=" + Uri.EscapeDataString(SelectedSlot.Id)); }
            if (ShowPhysical) { query.Add("side=physical"); }

            return server.ListenUrl + "/overlay" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        }
    }

    /// <summary>True once there is a real URL — gates the Copy button.</summary>
    public bool CanCopy => server.IsRunning && !string.IsNullOrEmpty(server.ListenUrl);

    /// <summary>
    /// Re-reads the theme and controller lists. Called on construction
    /// and whenever the dialog is shown: both lists change while the app
    /// runs — a slot is created, a theme pack is installed — and a stale
    /// picker would hand out a URL naming something that no longer
    /// exists.
    /// </summary>
    public void Refresh()
    {
        var previousTheme = SelectedTheme?.Id;
        var previousSlot = SelectedSlot?.Id;

        Themes.Clear();
        // Empty id means "send no theme parameter", which lets the server
        // pick from the slot's own output kind. That is the better
        // default: it keeps following the controller if the user changes
        // what the slot emits, where a pinned theme would silently go on
        // showing the old one.
        Themes.Add(new OverlayChoice(string.Empty, "Match the controller"));
        foreach (var theme in feed.Themes())
        {
            Themes.Add(new OverlayChoice(theme.Id, $"{theme.DisplayName} ({theme.Id})"));
        }

        Slots.Clear();
        Slots.Add(new OverlayChoice(string.Empty, "First controller"));
        foreach (var slot in feed.Slots())
        {
            Slots.Add(new OverlayChoice(slot.Id, string.IsNullOrWhiteSpace(slot.Name) ? slot.Id : slot.Name));
        }

        SelectedTheme = Find(Themes, previousTheme);
        SelectedSlot = Find(Slots, previousSlot);

        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(PhoneControllerUrl));
        OnPropertyChanged(nameof(CanCopyPhoneControllerUrl));
        RaiseReachabilityChanged();
    }

    private static OverlayChoice Find(ObservableCollection<OverlayChoice> choices, string? id) =>
        choices.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
}

/// <summary>One entry in the overlay's theme or controller picker.</summary>
/// <param name="Id">Registry id, or empty for the "let the server decide" entry.</param>
/// <param name="Label">What the combo box shows.</param>
public sealed record OverlayChoice(string Id, string Label);
