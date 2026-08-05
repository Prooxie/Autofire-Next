using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using GameFlow.Infrastructure.Runtime;

namespace GameFlow.App.ViewModels;

/// <summary>
/// One entry in the left menu column (compact style): an icon + name +
/// click command that jumps to the right tab and selects the underlying
/// device or slot. The instance carries no state beyond its display
/// fields, so simple <see langword="get"/>-only properties work — no
/// INPC needed.
/// </summary>
public sealed class MenuColumnItemViewModel
{
    public string Id { get; }
    public string Name { get; }
    public string IconText { get; }
    public bool IsConnected { get; }
    public ICommand SelectCommand { get; }
    public string SecondaryText { get; }
    public bool HasSecondaryText => !string.IsNullOrWhiteSpace(SecondaryText);
    public int? BatteryPercentage { get; }
    public DeviceBatteryState BatteryState { get; }
    public bool HasBattery => BatteryPercentage is >= 0 and <= 100
        && BatteryState is not DeviceBatteryState.NoBattery;
    public string BatteryText => HasBattery ? $"{BatteryPercentage}%" : string.Empty;
    public string BatteryIcon => BatteryState switch
    {
        DeviceBatteryState.Charging => "⚡",
        DeviceBatteryState.Charged => "●",
        _ when BatteryPercentage is <= 20 => "▱",
        _ => "▰",
    };
    public string BatteryBrush => BatteryState == DeviceBatteryState.Charging
        ? "#38BDF8"
        : BatteryPercentage is <= 20 ? "#F87171" : "#4ADE80";
    public string BatteryTooltip => BatteryState switch
    {
        DeviceBatteryState.Charging => $"Charging · {BatteryPercentage}%",
        DeviceBatteryState.Charged => "Fully charged",
        DeviceBatteryState.OnBattery => $"Battery · {BatteryPercentage}%",
        _ => BatteryText,
    };

    /// <summary>True when this row offers a dashboard pin toggle (physical devices only).</summary>
    public bool CanPin { get; }

    /// <summary>Pin-state glyph: filled when the device is pinned to the dashboard.</summary>
    public string PinIcon { get; }

    /// <summary>Tooltip for the pin toggle.</summary>
    public string PinTooltip { get; }

    public ICommand? PinCommand { get; }

    public MenuColumnItemViewModel(
        string id, string name, string iconText, bool isConnected, Action onSelect,
        string? secondaryText = null)
        : this(id, name, iconText, isConnected, onSelect, isPinned: false, onTogglePin: null,
            secondaryText: secondaryText)
    {
    }

    /// <summary>
    /// Pin-capable overload (sidebar physical devices): adds a toggle
    /// that shows/hides a layout-only panel for this device on the
    /// dashboard — no virtual controller involved.
    /// </summary>
    public MenuColumnItemViewModel(
        string id, string name, string iconText, bool isConnected, Action onSelect,
        bool isPinned, Action? onTogglePin, string? secondaryText = null,
        int? batteryPercentage = null,
        DeviceBatteryState batteryState = DeviceBatteryState.Unknown)
    {
        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;
        IconText = iconText;
        IsConnected = isConnected;
        SecondaryText = secondaryText ?? string.Empty;
        BatteryPercentage = batteryPercentage;
        BatteryState = batteryState;
        SelectCommand = new RelayCommand(onSelect);
        CanPin = onTogglePin is not null;
        PinIcon = isPinned ? "◉" : "◎";
        PinTooltip = isPinned
            ? "Remove this device's layout panel from the dashboard"
            : "Show this device's layout on the dashboard (no virtual controller needed)";
        PinCommand = onTogglePin is null ? null : new RelayCommand(onTogglePin);
    }
}
