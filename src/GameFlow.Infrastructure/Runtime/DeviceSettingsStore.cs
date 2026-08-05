using System.Text.Json;
using GameFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime;

/// <summary>
/// Stores and persists <see cref="DeviceSettings"/> keyed by slot AND
/// device, so the same physical pad can be tuned differently depending
/// on which virtual controller it's driving.
///
/// <para>
/// Thread-safe: the runtime tick reads it per device per frame while the
/// UI writes it whenever someone drags a slider. Reads take a snapshot
/// of the immutable record under lock and release immediately — the
/// conditioning maths then runs outside the lock.
/// </para>
///
/// <para>
/// Writes persist immediately (matching how SlotRegistry already
/// behaves) rather than on a timer or at shutdown: a crash or a forced
/// quit shouldn't cost someone their tuning.
/// </para>
/// </summary>
public sealed class DeviceSettingsStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, DeviceSettings> settings = new(StringComparer.Ordinal);
    private readonly string filePath;
    private readonly ILogger<DeviceSettingsStore> logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public DeviceSettingsStore(ILogger<DeviceSettingsStore> logger, string? overridePath = null)
    {
        this.logger = logger;
        filePath = overridePath ?? Path.Combine(ResolveDataDirectory(), "device-settings.json");
        Load();
    }

    /// <summary>Raised after any mutation so the UI can react without polling.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>
    /// Settings for one device on one slot, or <see cref="DeviceSettings.Default"/>
    /// when it's never been tuned. Never returns null — callers on the
    /// tick path shouldn't have to null-check every frame.
    /// </summary>
    public DeviceSettings Get(string slotId, string deviceId)
    {
        if (string.IsNullOrEmpty(slotId) || string.IsNullOrEmpty(deviceId))
        {
            return DeviceSettings.Default;
        }

        lock (gate)
        {
            return settings.TryGetValue(BuildKey(slotId, deviceId), out var found)
                ? found
                : DeviceSettings.Default;
        }
    }

    public void Set(string slotId, string deviceId, DeviceSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrEmpty(slotId) || string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        lock (gate)
        {
            settings[BuildKey(slotId, deviceId)] = value;
        }

        Persist();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops a device's tuning back to defaults.</summary>
    public void Reset(string slotId, string deviceId)
    {
        if (string.IsNullOrEmpty(slotId) || string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        bool removed;
        lock (gate)
        {
            removed = settings.Remove(BuildKey(slotId, deviceId));
        }

        if (removed)
        {
            Persist();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Forgets every entry belonging to a slot. Called when a slot is
    /// deleted so its tuning doesn't linger and get silently inherited
    /// by a future slot that happens to reuse the id.
    /// </summary>
    public void RemoveSlot(string slotId)
    {
        if (string.IsNullOrEmpty(slotId))
        {
            return;
        }

        var prefix = slotId + "::";
        bool changed;
        lock (gate)
        {
            var doomed = settings.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in doomed)
            {
                _ = settings.Remove(key);
            }
            changed = doomed.Count > 0;
        }

        if (changed)
        {
            Persist();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>True when this pair has been explicitly tuned — lets the UI show a "modified" marker.</summary>
    public bool HasSettings(string slotId, string deviceId)
    {
        if (string.IsNullOrEmpty(slotId) || string.IsNullOrEmpty(deviceId))
        {
            return false;
        }
        lock (gate)
        {
            return settings.ContainsKey(BuildKey(slotId, deviceId));
        }
    }

    private static string BuildKey(string slotId, string deviceId) => $"{slotId}::{deviceId}";

    private void Load()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            var json = File.ReadAllText(filePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DeviceSettings>>(json, JsonOptions);
            if (loaded is null)
            {
                return;
            }

            lock (gate)
            {
                foreach (var pair in loaded)
                {
                    settings[pair.Key] = pair.Value;
                }
            }

            logger.LogInformation("Device settings: loaded {Count} entries.", loaded.Count);
        }
        catch (Exception exception)
        {
            // A corrupt settings file must not stop the app from
            // starting — everything falls back to defaults, which is a
            // usable state, and the file gets rewritten on the next edit.
            logger.LogWarning(exception, "Device settings: could not load {Path}; starting from defaults.", filePath);
        }
    }

    private void Persist()
    {
        try
        {
            Dictionary<string, DeviceSettings> snapshot;
            lock (gate)
            {
                snapshot = new Dictionary<string, DeviceSettings>(settings, StringComparer.Ordinal);
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            // Write to a temp file then move: a crash mid-write leaves
            // the previous good file intact instead of a truncated one.
            var temp = filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temp, filePath, overwrite: true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Device settings: could not persist to {Path}.", filePath);
        }
    }

    private static string ResolveDataDirectory()
    {
        // Same "AutofireNext" folder the rest of the app already uses —
        // deliberately unchanged so existing installs keep their data.
        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDirectory, "AutofireNext");
    }
}
