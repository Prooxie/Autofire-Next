using System.Text.Json;
using GameFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Profiles;

public sealed class ProfileSession(IProfileRepository repository, ILogger<ProfileSession> logger)
{
    private readonly IProfileRepository repository = repository;
    private readonly ILogger<ProfileSession> logger = logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool isInitialized;

    public event EventHandler? Changed;

    public ProfileDocument CurrentProfile { get; private set; } = ProfileDefaults.CreateSpeedrunnerDefault();

    public AppSettings Settings { get; private set; } = new();

    public string CurrentProfilePath => repository.GetProfilePath(CurrentProfile.Id);

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (isInitialized)
        {
            return;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (isInitialized)
            {
                return;
            }

            Settings = await repository.LoadSettingsAsync(cancellationToken);

            var current = await repository.LoadAsync(Settings.ActiveProfileId, cancellationToken);
            if (current is null)
            {
                current = ProfileDefaults.CreateSpeedrunnerDefault();
                await repository.SaveAsync(current, cancellationToken);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Created default profile at {ProfilePath}.", repository.GetProfilePath(current.Id));
                }
            }
            else
            {
                var migrated = MigrateProfile(current);
                if (!ReferenceEquals(migrated, current) && migrated != current)
                {
                    current = migrated;
                    await repository.SaveAsync(current, cancellationToken);
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("Migrated profile {ProfileId} to version {Version}.", current.Id, current.Version);
                    }
                }
            }

            CurrentProfile = current;
            isInitialized = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    public async Task SaveProfileJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        var profile = JsonSerializer.Deserialize<ProfileDocument>(json, ProfileJsonOptions.Default)
            ?? throw new InvalidOperationException("Profile JSON could not be parsed.");

        await SaveCurrentProfileAsync(profile, cancellationToken);
    }

    /// <summary>
    /// Persists <paramref name="profile"/> as the current profile and updates
    /// the <see cref="Settings"/> active-profile pointer. Raises <see cref="Changed"/>.
    /// </summary>
    public async Task SaveCurrentProfileAsync(ProfileDocument profile, CancellationToken cancellationToken = default)
    {
        CurrentProfile = profile;
        await repository.SaveAsync(profile, cancellationToken);

        Settings = Settings with { ActiveProfileId = profile.Id };
        await repository.SaveSettingsAsync(Settings, cancellationToken);

        logger.LogInformation(
            "Saved profile {ProfileId} ({ProfileName}) and set as active.",
            profile.Id,
            profile.Name);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replaces the current profile with a fresh copy of the speedrunner
    /// default and persists it.
    /// </summary>
    public async Task ResetToDefaultAsync(CancellationToken cancellationToken = default)
    {
        CurrentProfile = ProfileDefaults.CreateSpeedrunnerDefault();
        await repository.SaveAsync(CurrentProfile, cancellationToken);

        Settings = Settings with { ActiveProfileId = CurrentProfile.Id };
        await repository.SaveSettingsAsync(Settings, cancellationToken);

        logger.LogInformation("Profile reset to speedrunner default.");

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Persists the chosen UI culture into the settings file.
    /// </summary>
    public async Task SetCultureAsync(string cultureCode, CancellationToken cancellationToken = default)
    {
        var previous = Settings.SelectedCulture;
        Settings = Settings with { SelectedCulture = cultureCode };
        await repository.SaveSettingsAsync(Settings, cancellationToken);

        if (!string.Equals(previous, cultureCode, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Persisted UI culture: {PreviousCulture} -> {NewCulture}.",
                previous,
                cultureCode);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string SerializeCurrentProfile()
    {
        return JsonSerializer.Serialize(CurrentProfile, ProfileJsonOptions.Default);
    }

    public async Task<IReadOnlyList<ProfileSummary>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        var ids = await repository.ListAsync(cancellationToken);
        var list = new List<ProfileSummary>();
        foreach (var id in ids)
        {
            var p = await repository.LoadAsync(id, cancellationToken);
            if (p is not null)
            {
                list.Add(new ProfileSummary(p.Id, p.Name));
            }
        }
        return list;
    }

    /// <summary>
    /// Loads the profile with the given <paramref name="profileId"/> and
    /// makes it active. Silently no-ops if the profile file is missing.
    /// </summary>
    public async Task SwitchToProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await repository.LoadAsync(profileId, cancellationToken);
            if (profile is null)
            {
                logger.LogWarning("Switch requested to profile {ProfileId} but no such file exists.", profileId);
                return;
            }

            var previousId = CurrentProfile.Id;
            CurrentProfile = MigrateProfile(profile);
            Settings = Settings with { ActiveProfileId = profileId };
            await repository.SaveSettingsAsync(Settings, cancellationToken);

            logger.LogInformation(
                "Switched active profile: {PreviousProfileId} -> {NewProfileId} ({ProfileName}).",
                previousId,
                CurrentProfile.Id,
                CurrentProfile.Name);
        }
        finally
        {
            _ = gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Creates a new profile from the speedrunner default with the given
    /// <paramref name="name"/>, persists it, and makes it active.
    /// </summary>
    public async Task<ProfileDocument> CreateNewProfileAsync(string name, CancellationToken cancellationToken = default)
    {
        var id = $"profile-{Guid.NewGuid():N}";
        var profile = ProfileDefaults.CreateSpeedrunnerDefault() with { Id = id, Name = name };

        logger.LogInformation("Creating new profile {ProfileId} ({ProfileName}).", id, name);

        await SaveCurrentProfileAsync(profile, cancellationToken);
        return profile;
    }

    /// <summary>
    /// Saves a copy of the currently-active profile under a new id and
    /// <paramref name="newName"/>, then makes the copy active.
    /// </summary>
    public async Task<ProfileDocument> DuplicateCurrentProfileAsync(string newName, CancellationToken cancellationToken = default)
    {
        var id = $"profile-{Guid.NewGuid():N}";
        var sourceId = CurrentProfile.Id;
        var profile = CurrentProfile with { Id = id, Name = newName };
        await repository.SaveAsync(profile, cancellationToken);

        logger.LogInformation(
            "Duplicated profile {SourceProfileId} -> {NewProfileId} ({NewProfileName}).",
            sourceId,
            id,
            newName);

        await SaveCurrentProfileAsync(profile, cancellationToken);
        return profile;
    }

    /// <summary>
    /// Deletes the profile with the given <paramref name="profileId"/>.
    /// Refuses to delete the currently-active profile (no-ops in that case).
    /// </summary>
    public async Task DeleteProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (string.Equals(profileId, CurrentProfile.Id, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Refused to delete profile {ProfileId} because it is currently active.",
                profileId);
            return;  // Never delete the active profile
        }

        await repository.DeleteAsync(profileId, cancellationToken);
        logger.LogInformation("Deleted profile {ProfileId}.", profileId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Parses the supplied profile JSON, assigns it a fresh id, persists
    /// it, and makes it active. Throws on parse failure.
    /// </summary>
    public async Task ImportProfileAsync(string json, CancellationToken cancellationToken = default)
    {
        var profile = JsonSerializer.Deserialize<ProfileDocument>(json, ProfileJsonOptions.Default)
            ?? throw new InvalidOperationException("Could not parse the profile JSON.");
        var id = $"profile-{Guid.NewGuid():N}";
        profile = profile with { Id = id };

        logger.LogInformation(
            "Importing profile (assigned new id {NewProfileId}, original name '{ProfileName}').",
            id,
            profile.Name);

        await SaveCurrentProfileAsync(profile, cancellationToken);
    }

    /// <summary>Polling rate shipped as the default in profile versions up to 4.</summary>
    private const int LegacyDefaultPollingRateHz = 250;

    private static ProfileDocument MigrateProfile(ProfileDocument profile)
    {
        var inputProvider = NormalizeInputProvider(profile.InputProvider);
        var preferredInputDeviceId = profile.PreferredInputDeviceId?.Trim() ?? string.Empty;
        var version = Math.Max(profile.Version, 5);

        // v5: the polling-rate default went from 250 Hz to 1000.
        //
        // Only rewritten when it still holds EXACTLY the old default on a
        // profile written before v5 — that value was shipped, not chosen,
        // and leaving it would mean the new default never reaching anyone
        // who already had a profile, which is everyone. Any other value is
        // a deliberate choice and is left alone, including a deliberate
        // 250 set after this migration has stamped the version.
        var pollingRateHz = profile.Version < 5 && profile.PollingRateHz == LegacyDefaultPollingRateHz
            ? 1000
            : profile.PollingRateHz;

        return version == profile.Version &&
            pollingRateHz == profile.PollingRateHz &&
            string.Equals(inputProvider, profile.InputProvider, StringComparison.Ordinal) &&
            string.Equals(preferredInputDeviceId, profile.PreferredInputDeviceId, StringComparison.Ordinal)
            ? profile
            : (profile with
            {
                Version = version,
                PollingRateHz = pollingRateHz,
                InputProvider = inputProvider,
                PreferredInputDeviceId = preferredInputDeviceId
            });
    }

    private static string NormalizeInputProvider(string? inputProvider)
    {
        return string.IsNullOrWhiteSpace(inputProvider)
            ? "sdl"
            : inputProvider.Trim().ToLowerInvariant() switch
            {
                "sdlgamepad" => "sdl",
                "sdl3" => "sdl",
                "sdl-unified" => "sdl",
                "sdl-unified-gamepad" => "sdl",
                // Retired providers — profiles that referenced them keep
                // working by migrating to the SDL3 unified source.
                "xinput" => "sdl",
                "openxinput" => "sdl",
                "x360ce" => "sdl",
                "ps3" or "dualshock3" => "sdl",
                "gameinput" => "sdl",
                "windows-midi" or "winmidi" or "midi" => "sdl",
                _ => inputProvider.Trim().ToLowerInvariant()
            };
    }
}
