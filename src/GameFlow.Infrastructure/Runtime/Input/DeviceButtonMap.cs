using GameFlow.Core.Enums;

namespace GameFlow.Infrastructure.Runtime.Input;

/// <summary>
/// One direction of one hat (POV) switch — the shape a D-pad actually
/// arrives in on nearly every gamepad.
/// </summary>
/// <param name="HatIndex">Which hat on the device, in SDL's order.</param>
/// <param name="Direction">
/// The SDL hat bit that means this direction: 1 up, 2 right, 4 down,
/// 8 left. A single bit, never a diagonal — a diagonal is two directions
/// held at once, not a fifth one.
/// </param>
public readonly record struct HatDirectionBinding(int HatIndex, byte Direction);

/// <summary>
/// A per-physical-device button remap: maps a canonical
/// <see cref="ButtonId"/> to the raw input that actually produces it on
/// this device. Used to normalize controllers the OS/SDL recognizes with a
/// different button order. Unmapped entries fall back to the default (SDL
/// gamepad mapping or the tentative joystick order).
/// </summary>
public sealed class DeviceButtonMap
{
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Canonical button → raw joystick button index.</summary>
    public Dictionary<ButtonId, int> Buttons { get; set; } = new();

    /// <summary>
    /// Canonical button → a hat direction.
    ///
    /// <para>
    /// Separate from <see cref="Buttons"/> because a hat is not a button
    /// and cannot be stored as one: four D-pad directions live in ONE hat
    /// as bits of a mask, so an index alone cannot identify a direction.
    /// Without this the calibration wizard could not capture a D-pad at
    /// all — it watched raw buttons, the D-pad moved a hat, and pressing
    /// it appeared to do nothing.
    /// </para>
    /// </summary>
    public Dictionary<ButtonId, HatDirectionBinding> Hats { get; set; } = new();

    /// <summary>True when nothing has been captured for this device.</summary>
    public bool IsEmpty => Buttons.Count == 0 && Hats.Count == 0;

    public DeviceButtonMap Clone() => new()
    {
        DeviceId = DeviceId,
        Buttons = new Dictionary<ButtonId, int>(Buttons),
        Hats = new Dictionary<ButtonId, HatDirectionBinding>(Hats),
    };
}

/// <summary>
/// Rising-edge detection over raw buttons and hats, for the calibration
/// wizard.
///
/// <para>
/// Pure and separate from the view model so the hat case is testable. It
/// is the one that was missing, and its failure mode is silent: the wizard
/// simply never advances, which reads as the D-pad not working rather than
/// as the wizard not listening.
/// </para>
/// </summary>
public static class ButtonCapture
{
    /// <summary>What a press resolved to — a raw button, or one direction of one hat.</summary>
    public readonly record struct Captured(int? ButtonIndex, HatDirectionBinding? Hat);

    /// <summary>
    /// Returns whatever was newly pressed between two samples, or null.
    /// Buttons win over hats when both changed in the same tick, only
    /// because something has to.
    /// </summary>
    public static Captured? Detect(
        IReadOnlySet<int> pressedButtonsBefore,
        IReadOnlySet<int> pressedButtonsNow,
        IReadOnlyList<byte> hatsBefore,
        IReadOnlyList<byte> hatsNow)
    {
        ArgumentNullException.ThrowIfNull(pressedButtonsBefore);
        ArgumentNullException.ThrowIfNull(pressedButtonsNow);
        ArgumentNullException.ThrowIfNull(hatsBefore);
        ArgumentNullException.ThrowIfNull(hatsNow);

        foreach (var index in pressedButtonsNow)
        {
            if (!pressedButtonsBefore.Contains(index))
            {
                return new Captured(index, null);
            }
        }

        for (var hat = 0; hat < hatsNow.Count; hat++)
        {
            var before = hat < hatsBefore.Count ? hatsBefore[hat] : (byte)0;
            var added = (byte)(hatsNow[hat] & ~before);
            if (added == 0)
            {
                continue;
            }

            // Lowest set bit only. Rolling a thumb onto the D-pad can set
            // two bits in one tick, and binding a canonical "D-pad Up" to
            // up+right would then never match a clean press of up.
            var direction = (byte)(added & (byte)-(sbyte)added);
            return new Captured(null, new HatDirectionBinding(hat, direction));
        }

        return null;
    }

    /// <summary>True when this hat mask currently includes the bound direction.</summary>
    public static bool IsPressed(HatDirectionBinding binding, IReadOnlyList<byte> hats) =>
        binding.HatIndex >= 0
        && binding.HatIndex < hats.Count
        && binding.Direction != 0
        && (hats[binding.HatIndex] & binding.Direction) == binding.Direction;
}
