using System.Globalization;

namespace GameFlow.App.ViewModels;

/// <summary>
/// One axis in the raw device-state panel. <see cref="Normalized"/> maps
/// SDL's signed 16-bit range to 0..1 (0.5 = centre) for a progress bar;
/// <see cref="RawText"/> shows the underlying value.
/// </summary>
public sealed class RawAxisRowViewModel(int index) : ViewModelBase
{
    private short raw;

    public int Index { get; } = index;
    public string Label => $"Axis {Index}";

    /// <summary>
    /// The unscaled SDL reading. The analog calibration wizard compares
    /// these against a baseline, so it needs the raw value rather than
    /// the display forms below.
    /// </summary>
    public short Raw => raw;

    public double Normalized => (raw + 32768.0) / 65535.0;
    public string RawText => raw.ToString(CultureInfo.InvariantCulture);

    public void Update(short value)
    {
        if (raw == value)
        {
            return;
        }
        raw = value;
        OnPropertyChanged(nameof(Normalized));
        OnPropertyChanged(nameof(RawText));
    }
}

/// <summary>One button in the raw device-state panel.</summary>
public sealed class RawButtonRowViewModel(int index) : ViewModelBase
{
    private bool pressed;

    public int Index { get; } = index;
    public string Label => Index.ToString(CultureInfo.InvariantCulture);

    public bool IsPressed
    {
        get => pressed;
        private set
        {
            if (SetProperty(ref pressed, value))
            {
                OnPropertyChanged(nameof(StateBrush));
            }
        }
    }

    /// <summary>Background colour for the button chip — accent when pressed, faint when idle.</summary>
    public string StateBrush => pressed ? "#22C55E" : "#22808080";

    public void Update(bool value) => IsPressed = value;
}

/// <summary>
/// One hat/POV in the raw device-state panel. Decodes the SDL hat
/// bitmask (bit 0 up, 1 right, 2 down, 3 left) into a readable direction.
/// </summary>
public sealed class RawHatRowViewModel(int index) : ViewModelBase
{
    private const byte Up = 0x01;
    private const byte Right = 0x02;
    private const byte Down = 0x04;
    private const byte Left = 0x08;

    private byte mask;

    public int Index { get; } = index;
    public string Label => $"Hat {Index}";

    /// <summary>
    /// The raw SDL direction mask. Exposed so the calibration wizard can
    /// watch hats for a rising edge — a D-pad is a hat, not four buttons.
    /// </summary>
    public byte Mask => mask;

    public string Direction => mask switch
    {
        0 => "Centered",
        Up => "Up",
        Up | Right => "Up-Right",
        Right => "Right",
        Down | Right => "Down-Right",
        Down => "Down",
        Down | Left => "Down-Left",
        Left => "Left",
        Up | Left => "Up-Left",
        _ => "Centered",
    };

    public void Update(byte value)
    {
        if (mask == value)
        {
            return;
        }
        mask = value;
        OnPropertyChanged(nameof(Direction));
    }
}
