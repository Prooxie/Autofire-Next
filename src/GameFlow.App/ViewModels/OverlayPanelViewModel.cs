using System.Collections.ObjectModel;
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

    public OverlayPanelViewModel(WebControllerServer server, OverlayFeed feed)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
        this.feed = feed ?? throw new ArgumentNullException(nameof(feed));

        Refresh();
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
    }

    private static OverlayChoice Find(ObservableCollection<OverlayChoice> choices, string? id) =>
        choices.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
}

/// <summary>One entry in the overlay's theme or controller picker.</summary>
/// <param name="Id">Registry id, or empty for the "let the server decide" entry.</param>
/// <param name="Label">What the combo box shows.</param>
public sealed record OverlayChoice(string Id, string Label);
