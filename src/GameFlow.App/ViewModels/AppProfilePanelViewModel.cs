using System.Collections.ObjectModel;
using System.Windows.Input;
using GameFlow.Infrastructure.Configuration;
using GameFlow.Infrastructure.Profiles;
using GameFlow.Infrastructure.Runtime.Profiles;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace GameFlow.App.ViewModels;

/// <summary>
/// Backs the Settings dialog's per-app profile section: the master
/// switch, the rule list, and a line saying what is in front right now.
///
/// <para>
/// That last part is not decoration. A rule matches an executable name,
/// and a user has no reliable way to know a game's is <c>eldenring</c>
/// and not <c>Elden Ring</c> — so the panel shows the foreground app's
/// real name and offers it as the value for a new rule. Without it the
/// first thing anyone writes is a rule that silently never fires.
/// </para>
///
/// <para>
/// Persists on change rather than on Apply, matching
/// <see cref="MotionServerPanelViewModel"/>.
/// </para>
/// </summary>
public sealed class AppProfilePanelViewModel : ViewModelBase
{
    private readonly IUserSettingsService userSettings;
    private readonly ProfileSession session;
    private readonly ProfileAutoSwitchService autoSwitch;
    private readonly ILogger<AppProfilePanelViewModel> logger;

    // Same guard as MotionServerPanelViewModel: populating the UI from
    // settings must not read as a user edit and write straight back.
    private bool suppressPersist;

    public AppProfilePanelViewModel(
        IUserSettingsService userSettings,
        ProfileSession session,
        ProfileAutoSwitchService autoSwitch,
        ILogger<AppProfilePanelViewModel> logger)
    {
        this.userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.autoSwitch = autoSwitch ?? throw new ArgumentNullException(nameof(autoSwitch));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        AddRuleCommand = new RelayCommand(AddRule, () => CanAddRule);
        RemoveRuleCommand = new RelayCommand<AppProfileRuleViewModel>(RemoveRule);
        UseCurrentAppCommand = new RelayCommand(UseCurrentApp, () => !string.IsNullOrEmpty(CurrentApp));

        LoadFromSettings();
    }

    /// <summary>The rules, in priority order — first match wins.</summary>
    public ObservableCollection<AppProfileRuleViewModel> Rules { get; } = [];

    /// <summary>Profiles a rule can point at.</summary>
    public ObservableCollection<OverlayChoice> Profiles { get; } = [];

    public ICommand AddRuleCommand { get; }
    public ICommand RemoveRuleCommand { get; }
    public ICommand UseCurrentAppCommand { get; }

    /// <summary>Master switch. See <see cref="AppSettings.AutoSwitchProfilesByApp"/> for why it is off by default.</summary>
    public bool IsEnabled
    {
        get;
        set
        {
            if (SetProperty(ref field, value)) { Persist(); }
        }
    }

    /// <summary>Process name for the rule being composed.</summary>
    public string NewProcessName
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(CanAddRule));
                (AddRuleCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    } = string.Empty;

    /// <summary>Profile the rule being composed points at.</summary>
    public OverlayChoice? NewProfile
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(CanAddRule));
                (AddRuleCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanAddRule =>
        !string.IsNullOrWhiteSpace(NewProcessName) && !string.IsNullOrWhiteSpace(NewProfile?.Id);

    /// <summary>The foreground application right now, or empty off Windows / when unreadable.</summary>
    public string CurrentApp => autoSwitch.CurrentApp ?? string.Empty;

    /// <summary>What the panel tells the user about the current state.</summary>
    public string Status
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentApp))
            {
                return OperatingSystem.IsWindows()
                    ? "No foreground application detected."
                    : "Reading the foreground application is Windows-only for now, so rules will not fire on this system.";
            }

            var switched = autoSwitch.LastSwitchedApp;
            return string.IsNullOrEmpty(switched)
                ? $"In front now: {CurrentApp}. Active profile: {session.CurrentProfile.Name}."
                : $"In front now: {CurrentApp}. Last switch was for {switched}; active profile: {session.CurrentProfile.Name}.";
        }
    }

    /// <summary>
    /// Re-reads the profile list and the foreground app. Called when the
    /// dialog opens: profiles are created and deleted while the app runs,
    /// and the whole point of the status line is that it is current.
    /// </summary>
    public async Task RefreshAsync()
    {
        var previous = NewProfile?.Id;

        Profiles.Clear();
        try
        {
            foreach (var summary in await session.ListProfilesAsync())
            {
                Profiles.Add(new OverlayChoice(summary.Id, summary.Name));
            }
        }
        catch (Exception exception)
        {
            // A profiles folder that cannot be read leaves an empty
            // picker, which disables Add — better than a dialog that
            // will not open.
            logger.LogWarning(exception, "Per-app profiles: could not list profiles for the rule picker.");
        }

        NewProfile = Profiles.FirstOrDefault(p => p.Id == previous) ?? Profiles.FirstOrDefault();

        OnPropertyChanged(nameof(CurrentApp));
        OnPropertyChanged(nameof(Status));
        (UseCurrentAppCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void LoadFromSettings()
    {
        suppressPersist = true;
        try
        {
            var settings = userSettings.Current;
            IsEnabled = settings.AutoSwitchProfilesByApp;

            Rules.Clear();
            foreach (var rule in settings.AppProfileRules)
            {
                Rules.Add(Wrap(rule));
            }
        }
        finally
        {
            suppressPersist = false;
        }
    }

    private AppProfileRuleViewModel Wrap(AppProfileRule rule)
    {
        var vm = new AppProfileRuleViewModel(rule.ProcessName, rule.ProfileId, ProfileNameFor(rule.ProfileId), rule.Enabled);
        // A row's Enabled toggle has to reach persistence, and the row
        // has no business knowing about settings — so the panel listens.
        vm.PropertyChanged += (_, _) => Persist();
        return vm;
    }

    private string ProfileNameFor(string profileId) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase))?.Label
        ?? profileId;

    private void AddRule()
    {
        if (!CanAddRule)
        {
            return;
        }

        // Normalised on the way in so the list shows what will actually
        // be matched — a user who pasted a full path sees the bare name
        // and can tell the rule is right before waiting for it to fire.
        var name = AppProfileMatcher.Normalize(NewProcessName);
        Rules.Add(Wrap(new AppProfileRule(name, NewProfile!.Id)));
        NewProcessName = string.Empty;
        Persist();
    }

    private void RemoveRule(AppProfileRuleViewModel? rule)
    {
        if (rule is not null && Rules.Remove(rule))
        {
            Persist();
        }
    }

    private void UseCurrentApp() => NewProcessName = CurrentApp;

    private void Persist()
    {
        if (suppressPersist)
        {
            return;
        }

        var settings = userSettings.Current with
        {
            AutoSwitchProfilesByApp = IsEnabled,
            AppProfileRules = [.. Rules.Select(r => new AppProfileRule(r.ProcessName, r.ProfileId, r.Enabled))]
        };

        _ = userSettings.ApplyAsync(settings).ContinueWith(
            task => logger.LogWarning(task.Exception, "Per-app profiles: saving the rules failed."),
            TaskContinuationOptions.OnlyOnFaulted);
    }
}

/// <summary>
/// One row in the rule list. Only <see cref="Enabled"/> is mutable —
/// editing a rule's process name or profile in place would need
/// validation and an undo story for what is a two-field record; removing
/// it and adding it again is clearer and is one extra click.
/// </summary>
public sealed class AppProfileRuleViewModel(
    string processName,
    string profileId,
    string profileName,
    bool enabled) : ViewModelBase
{
    public string ProcessName { get; } = processName;
    public string ProfileId { get; } = profileId;

    /// <summary>Display name, falling back to the id when the profile has been deleted — which is exactly when the user needs to see it.</summary>
    public string ProfileName { get; } = profileName;

    public bool Enabled
    {
        get;
        set => SetProperty(ref field, value);
    } = enabled;
}
