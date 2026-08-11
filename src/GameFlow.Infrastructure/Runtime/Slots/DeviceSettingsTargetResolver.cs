namespace GameFlow.Infrastructure.Runtime.Slots;

/// <summary>
/// A persistence target for the tuning editor. A target is normally one
/// physical device on one slot; the slot-default target keeps the editor
/// useful before a device is assigned and supplies defaults for devices
/// that do not have an explicit override yet.
/// </summary>
public sealed record DeviceSettingsTarget(
    string DeviceId,
    string DisplayName,
    bool IsSlotDefault);

/// <summary>
/// Resolves a stable tuning target without requiring a currently connected
/// input device. This policy is shared by both tuning entry points so the
/// dashboard dialog and Devices page cannot disagree about availability.
/// </summary>
public static class DeviceSettingsTargetResolver
{
    public static DeviceSettingsTarget Resolve(
        ControllerSlot slot,
        IReadOnlyList<InputDeviceInfo>? visibleDevices = null,
        string? preferredDeviceId = null,
        string? preferredDisplayName = null)
    {
        ArgumentNullException.ThrowIfNull(slot);

        // The Devices page deliberately lets the user tune its selected
        // physical device for any slot, including before it is assigned.
        if (!string.IsNullOrWhiteSpace(preferredDeviceId))
        {
            return new DeviceSettingsTarget(
                preferredDeviceId,
                string.IsNullOrWhiteSpace(preferredDisplayName)
                    ? preferredDeviceId
                    : preferredDisplayName,
                IsSlotDefault: false);
        }

        // An assignment survives unplugging. Prefer it over slot defaults so
        // disconnecting a pad does not silently switch the editor to another
        // persistence key halfway through a tuning session.
        var assignedDeviceId = slot.InputDeviceIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (assignedDeviceId is not null)
        {
            var device = visibleDevices?.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, assignedDeviceId, StringComparison.OrdinalIgnoreCase));

            return new DeviceSettingsTarget(
                assignedDeviceId,
                device?.DisplayName ?? assignedDeviceId,
                IsSlotDefault: false);
        }

        var slotName = string.IsNullOrWhiteSpace(slot.Name)
            ? $"Virtual controller {slot.Index + 1}"
            : slot.Name.Trim();

        return new DeviceSettingsTarget(
            DeviceSettingsStore.SlotDefaultsDeviceId,
            $"{slotName} defaults",
            IsSlotDefault: true);
    }
}
