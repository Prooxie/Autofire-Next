using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using GameFlow.Core.Enums;
using GameFlow.Core.Models.Rules;
using GameFlow.Infrastructure.Localization;

namespace GameFlow.App.ViewModels;

/// <summary>A pickable enum value with a display label, for the touchpad combo boxes.</summary>
public sealed record TouchOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One row in the gesture list. Holds its own editable state and pushes
/// every change back through <see cref="onChanged"/>, which the parent
/// turns into a save — matching the rest of the slot editor, where there
/// is no separate "apply" step.
/// </summary>
public sealed class TouchGestureRowViewModel : ViewModelBase
{
    private readonly Action onChanged;
    private bool loading = true;

    public TouchGestureRowViewModel(
        TouchGestureBinding binding, TouchpadEditorViewModel parent, Action onChanged)
    {
        this.onChanged = onChanged;
        Parent = parent;
        Id = binding.Id;

        enabled = binding.Enabled;
        kind = parent.KindOptions.FirstOrDefault(o => o.Value == binding.Kind) ?? parent.KindOptions[0];
        swipeDirection = parent.SwipeDirectionOptions.FirstOrDefault(o => o.Value == binding.SwipeDirection)
            ?? parent.SwipeDirectionOptions[0];
        eightWay = binding.EightWay;
        fingerCount = Math.Clamp(binding.FingerCount, 1, 5);
        tapCount = Math.Clamp(binding.TapCount, 1, 3);
        pinchDirection = parent.PinchDirectionOptions.FirstOrDefault(o => o.Value == binding.PinchDirection)
            ?? parent.PinchDirectionOptions[0];
        rotateDirection = parent.RotateDirectionOptions.FirstOrDefault(o => o.Value == binding.RotateDirection)
            ?? parent.RotateDirectionOptions[0];
        shape = parent.ShapeOptions.FirstOrDefault(o => o.Value == binding.Shape) ?? parent.ShapeOptions[0];
        targetButton = parent.ButtonOptions.FirstOrDefault(o => o.Value == binding.TargetButton)
            ?? parent.ButtonOptions[0];
        holdMilliseconds = binding.HoldMilliseconds;

        loading = false;
    }

    public string Id { get; }
    public TouchpadEditorViewModel Parent { get; }

    private bool enabled;
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }

    private TouchOption<TouchGestureKind> kind;
    public TouchOption<TouchGestureKind> Kind
    {
        get => kind;
        set
        {
            if (Set(ref kind, value))
            {
                // Which detail controls apply depends entirely on the
                // kind, so every visibility flag has to re-evaluate.
                OnPropertyChanged(nameof(ShowSwipeDetail));
                OnPropertyChanged(nameof(ShowTapDetail));
                OnPropertyChanged(nameof(ShowPinchDetail));
                OnPropertyChanged(nameof(ShowRotateDetail));
                OnPropertyChanged(nameof(ShowShapeDetail));
            }
        }
    }

    private TouchOption<TouchSwipeDirection> swipeDirection;
    public TouchOption<TouchSwipeDirection> SwipeDirection
    {
        get => swipeDirection;
        set => Set(ref swipeDirection, value);
    }

    private bool eightWay;
    public bool EightWay { get => eightWay; set => Set(ref eightWay, value); }

    private int fingerCount;
    public int FingerCount
    {
        get => fingerCount;
        set => Set(ref fingerCount, Math.Clamp(value, 1, 5));
    }

    private int tapCount;
    public int TapCount
    {
        get => tapCount;
        set => Set(ref tapCount, Math.Clamp(value, 1, 3));
    }

    private TouchOption<TouchPinchDirection> pinchDirection;
    public TouchOption<TouchPinchDirection> PinchDirection
    {
        get => pinchDirection;
        set => Set(ref pinchDirection, value);
    }

    private TouchOption<TouchRotateDirection> rotateDirection;
    public TouchOption<TouchRotateDirection> RotateDirection
    {
        get => rotateDirection;
        set => Set(ref rotateDirection, value);
    }

    private TouchOption<TouchShape> shape;
    public TouchOption<TouchShape> Shape { get => shape; set => Set(ref shape, value); }

    private TouchOption<ButtonId> targetButton;
    public TouchOption<ButtonId> TargetButton { get => targetButton; set => Set(ref targetButton, value); }

    private int holdMilliseconds;
    public int HoldMilliseconds
    {
        get => holdMilliseconds;
        // Floored at one tick's worth rather than zero: a zero-length
        // pulse would be scheduled and expire on the same tick, so the
        // press would never reach the output at all.
        set => Set(ref holdMilliseconds, Math.Clamp(value, 1, 5000));
    }

    public bool ShowSwipeDetail => Kind.Value == TouchGestureKind.Swipe;
    public bool ShowTapDetail => Kind.Value == TouchGestureKind.Tap;
    public bool ShowPinchDetail => Kind.Value == TouchGestureKind.Pinch;
    public bool ShowRotateDetail => Kind.Value == TouchGestureKind.Rotate;
    public bool ShowShapeDetail => Kind.Value == TouchGestureKind.Shape;

    /// <summary>
    /// Pinch and rotate are two-finger gestures by definition, so the
    /// finger-count control is meaningless for them.
    /// </summary>
    public bool ShowFingerCount =>
        Kind.Value is not (TouchGestureKind.Pinch or TouchGestureKind.Rotate);

    public TouchGestureBinding ToBinding() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Kind = Kind.Value,
        SwipeDirection = SwipeDirection.Value,
        EightWay = EightWay,
        // Forced to two for the gestures that can't mean anything else,
        // so a stale value left over from a kind change can't make the
        // binding unmatchable.
        FingerCount = Kind.Value is TouchGestureKind.Pinch or TouchGestureKind.Rotate ? 2 : FingerCount,
        TapCount = TapCount,
        PinchDirection = PinchDirection.Value,
        RotateDirection = RotateDirection.Value,
        Shape = Shape.Value,
        TargetButton = TargetButton.Value,
        HoldMilliseconds = HoldMilliseconds
    };

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name))
        {
            return false;
        }
        if (!loading)
        {
            onChanged();
        }
        return true;
    }
}

/// <summary>
/// Backs the per-slot Touchpad tab. Loads the slot's
/// <see cref="TouchpadMapRule"/>, binds every toggle to it, and saves
/// through the supplied callback on each change — "every toggle saves per
/// slot", with no separate apply step, matching how the rest of the slot
/// editor behaves.
///
/// <para>
/// The tab is only shown for slots whose assigned hardware actually
/// carries a touch surface; see <c>SlotsViewModel.SelectedSlotHasTouchpad</c>.
/// </para>
/// </summary>
public sealed class TouchpadEditorViewModel : ViewModelBase, IDisposable
{
    private readonly ILocalizationService localization;
    private Action<TouchpadMapRule?>? saver;
    private bool loading;
    private bool disposed;

    public TouchpadEditorViewModel(ILocalizationService localization)
    {
        this.localization = localization ?? throw new ArgumentNullException(nameof(localization));

        KindOptions = BuildKindOptions();
        SwipeDirectionOptions = BuildSwipeOptions();
        PinchDirectionOptions = BuildPinchOptions();
        RotateDirectionOptions = BuildRotateOptions();
        ShapeOptions = BuildShapeOptions();
        ButtonOptions = BuildButtonOptions();
        StickOptions =
        [
            new TouchOption<StickId>(StickId.Left, Loc("TouchpadStickLeft", "Left stick")),
            new TouchOption<StickId>(StickId.Right, Loc("TouchpadStickRight", "Right stick")),
        ];
        targetStick = StickOptions[1];

        AddGestureCommand = new RelayCommand(AddGesture);
        RemoveGestureCommand = new RelayCommand<string>(RemoveGesture);
        Gestures.CollectionChanged += OnGesturesChanged;

        // Named handler, not a lambda: the localization service is a
        // process-wide singleton, so an anonymous subscription can never
        // be detached and keeps this editor (and every row it holds)
        // alive for the life of the app.
        this.localization.CultureChanged += OnCultureChanged;
    }

    private void OnGesturesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasGestures));

    private void OnCultureChanged(object? sender, EventArgs e) => RaiseLabels();

    /// <summary>Detaches the singleton-owned culture subscription.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        Gestures.CollectionChanged -= OnGesturesChanged;
        localization.CultureChanged -= OnCultureChanged;
    }

    // ─── Option sources ───

    public IReadOnlyList<TouchOption<TouchGestureKind>> KindOptions { get; }
    public IReadOnlyList<TouchOption<TouchSwipeDirection>> SwipeDirectionOptions { get; }
    public IReadOnlyList<TouchOption<TouchPinchDirection>> PinchDirectionOptions { get; }
    public IReadOnlyList<TouchOption<TouchRotateDirection>> RotateDirectionOptions { get; }
    public IReadOnlyList<TouchOption<TouchShape>> ShapeOptions { get; }
    public IReadOnlyList<TouchOption<ButtonId>> ButtonOptions { get; }
    public IReadOnlyList<TouchOption<StickId>> StickOptions { get; }

    public ObservableCollection<TouchGestureRowViewModel> Gestures { get; } = [];

    /// <summary>
    /// Drives the "no gestures yet" placeholder. An explicit bool rather
    /// than negating <c>Gestures.Count</c> in XAML: Avalonia's <c>!</c>
    /// binding operator is defined over booleans, and handing it an int
    /// yields an unset value instead of the intended inversion — so the
    /// placeholder would simply never appear.
    /// </summary>
    public bool HasGestures => Gestures.Count > 0;

    public ICommand AddGestureCommand { get; }
    public ICommand RemoveGestureCommand { get; }

    private bool hasSlot;
    /// <summary>True when a slot with a touch surface is loaded — gates the whole tab.</summary>
    public bool HasSlot { get => hasSlot; private set => SetProperty(ref hasSlot, value); }

    // ─── Joystick mode ───

    private bool stickEnabled = true;
    public bool StickEnabled { get => stickEnabled; set => Set(ref stickEnabled, value); }

    private TouchOption<StickId> targetStick;
    public TouchOption<StickId> TargetStick { get => targetStick; set => Set(ref targetStick, value); }

    private double stickSensitivity = 2.5;
    public double StickSensitivity
    {
        get => stickSensitivity;
        set => Set(ref stickSensitivity, Math.Clamp(value, 0.1, 10.0));
    }

    // ─── D-pad mode ───

    private bool dpadEnabled;
    public bool DpadEnabled { get => dpadEnabled; set => Set(ref dpadEnabled, value); }

    private bool dpadEightWay = true;
    public bool DpadEightWay { get => dpadEightWay; set => Set(ref dpadEightWay, value); }

    private double dpadDeadzoneRadius = 0.05;
    public double DpadDeadzoneRadius
    {
        get => dpadDeadzoneRadius;
        set => Set(ref dpadDeadzoneRadius, Math.Clamp(value, 0.0, 0.5));
    }

    // ─── Mouse mode ───

    private bool mouseEnabled;
    public bool MouseEnabled { get => mouseEnabled; set => Set(ref mouseEnabled, value); }

    private double mouseSensitivityX = 1.0;
    public double MouseSensitivityX
    {
        get => mouseSensitivityX;
        set => Set(ref mouseSensitivityX, Math.Clamp(value, 0.1, 10.0));
    }

    private double mouseSensitivityY = 1.0;
    public double MouseSensitivityY
    {
        get => mouseSensitivityY;
        set => Set(ref mouseSensitivityY, Math.Clamp(value, 0.1, 10.0));
    }

    private bool invertMouseX;
    public bool InvertMouseX { get => invertMouseX; set => Set(ref invertMouseX, value); }

    private bool invertMouseY;
    public bool InvertMouseY { get => invertMouseY; set => Set(ref invertMouseY, value); }

    // ─── Gesture recognizer tuning ───

    private bool gesturesEnabled;
    public bool GesturesEnabled { get => gesturesEnabled; set => Set(ref gesturesEnabled, value); }

    private double swipeMinDistance = 0.15;
    public double SwipeMinDistance
    {
        get => swipeMinDistance;
        set => Set(ref swipeMinDistance, Math.Clamp(value, 0.01, 1.0));
    }

    private int tapMaxMilliseconds = 250;
    public int TapMaxMilliseconds
    {
        get => tapMaxMilliseconds;
        set => Set(ref tapMaxMilliseconds, Math.Clamp(value, 30, 2000));
    }

    private int multiTapWindowMilliseconds = 300;
    public int MultiTapWindowMilliseconds
    {
        get => multiTapWindowMilliseconds;
        set => Set(ref multiTapWindowMilliseconds, Math.Clamp(value, 50, 2000));
    }

    private int longPressMilliseconds = 500;
    public int LongPressMilliseconds
    {
        get => longPressMilliseconds;
        set => Set(ref longPressMilliseconds, Math.Clamp(value, 100, 5000));
    }

    private double pinchThreshold = 0.3;
    public double PinchThreshold
    {
        get => pinchThreshold;
        set => Set(ref pinchThreshold, Math.Clamp(value, 0.05, 0.9));
    }

    private double rotateThresholdDegrees = 30;
    public double RotateThresholdDegrees
    {
        get => rotateThresholdDegrees;
        set => Set(ref rotateThresholdDegrees, Math.Clamp(value, 5, 180));
    }

    private double shapeMatchThreshold = 0.8;
    public double ShapeMatchThreshold
    {
        get => shapeMatchThreshold;
        set => Set(ref shapeMatchThreshold, Math.Clamp(value, 0.5, 0.99));
    }

    // ─── Localized labels ───

    public string JoystickSectionLabel => Loc("TouchpadJoystickSection", "Joystick");
    public string JoystickHint => Loc("TouchpadJoystickHint",
        "The stick centres wherever your finger lands, like a phone game's virtual stick.");
    public string StickEnabledLabel => Loc("TouchpadStickEnabled", "Map touch to a stick");
    public string TargetStickLabel => Loc("TouchpadTargetStick", "Target stick");
    public string StickSensitivityLabel => Loc("TouchpadStickSensitivity", "Sensitivity");

    public string DpadSectionLabel => Loc("TouchpadDpadSection", "D-pad");
    public string DpadEnabledLabel => Loc("TouchpadDpadEnabled", "Map touch to the D-pad");
    public string DpadEightWayLabel => Loc("TouchpadDpadEightWay", "8-way (diagonals)");
    public string DpadDeadzoneLabel => Loc("TouchpadDpadDeadzone", "Deadzone radius");

    public string MouseSectionLabel => Loc("TouchpadMouseSection", "Mouse");
    public string MouseHint => Loc("TouchpadMouseHint",
        "Moves the cursor from frame to frame, the way a laptop touchpad does.");
    public string MouseEnabledLabel => Loc("TouchpadMouseEnabled", "Map touch to the mouse");
    public string MouseSensitivityXLabel => Loc("TouchpadMouseSensitivityX", "Sensitivity X");
    public string MouseSensitivityYLabel => Loc("TouchpadMouseSensitivityY", "Sensitivity Y");
    public string InvertMouseXLabel => Loc("TouchpadInvertMouseX", "Invert X");
    public string InvertMouseYLabel => Loc("TouchpadInvertMouseY", "Invert Y");

    public string GesturesSectionLabel => Loc("TouchpadGesturesSection", "Gestures");
    public string GesturesEnabledLabel => Loc("TouchpadGesturesEnabled", "Enable gestures");
    public string AddGestureLabel => Loc("TouchpadAddGesture", "Add gesture");
    public string RemoveLabel => Loc("TouchpadRemove", "Remove");
    public string NoGesturesLabel => Loc("TouchpadNoGestures", "No gestures yet.");
    public string GestureTargetLabel => Loc("TouchpadGestureTarget", "Press");
    public string GestureHoldLabel => Loc("TouchpadGestureHold", "Hold (ms)");
    public string GestureFingersLabel => Loc("TouchpadGestureFingers", "Fingers");
    public string GestureEightWayLabel => Loc("TouchpadGestureEightWay", "8-way");
    public string GestureTapCountLabel => Loc("TouchpadGestureTapCount", "Taps");

    public string TuningSectionLabel => Loc("TouchpadTuningSection", "Recognition");
    public string SwipeMinDistanceLabel => Loc("TouchpadSwipeMinDistance", "Swipe distance");
    public string TapMaxLabel => Loc("TouchpadTapMax", "Tap max (ms)");
    public string MultiTapWindowLabel => Loc("TouchpadMultiTapWindow", "Multi-tap window (ms)");
    public string LongPressLabel => Loc("TouchpadLongPress", "Long press (ms)");
    public string PinchThresholdLabel => Loc("TouchpadPinchThreshold", "Pinch threshold");
    public string RotateThresholdLabel => Loc("TouchpadRotateThreshold", "Rotate threshold (deg)");
    public string ShapeThresholdLabel => Loc("TouchpadShapeThreshold", "Shape match");

    /// <summary>
    /// Loads a slot's touchpad settings. A null rule means the slot has
    /// never been configured, in which case the editor shows defaults but
    /// saves nothing until the user actually changes something — so
    /// merely clicking through slots doesn't write a rule onto every one
    /// of them.
    /// </summary>
    public void Load(TouchpadMapRule? rule, Action<TouchpadMapRule?> save)
    {
        loading = true;
        try
        {
            saver = save;
            HasSlot = true;
            var source = rule ?? new TouchpadMapRule();

            StickEnabled = source.StickEnabled;
            TargetStick = StickOptions.FirstOrDefault(o => o.Value == source.TargetStick) ?? StickOptions[1];
            StickSensitivity = source.StickSensitivity;

            DpadEnabled = source.DpadEnabled;
            DpadEightWay = source.DpadEightWay;
            DpadDeadzoneRadius = source.DpadDeadzoneRadius;

            MouseEnabled = source.MouseEnabled;
            MouseSensitivityX = source.MouseSensitivityX;
            MouseSensitivityY = source.MouseSensitivityY;
            InvertMouseX = source.InvertMouseX;
            InvertMouseY = source.InvertMouseY;

            GesturesEnabled = source.GesturesEnabled;
            SwipeMinDistance = source.SwipeMinDistance;
            TapMaxMilliseconds = source.TapMaxMilliseconds;
            MultiTapWindowMilliseconds = source.MultiTapWindowMilliseconds;
            LongPressMilliseconds = source.LongPressMilliseconds;
            PinchThreshold = source.PinchThreshold;
            RotateThresholdDegrees = source.RotateThresholdDegrees;
            ShapeMatchThreshold = source.ShapeMatchThreshold;

            Gestures.Clear();
            foreach (var binding in source.Gestures)
            {
                Gestures.Add(new TouchGestureRowViewModel(binding, this, Commit));
            }
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>Clears the editor (no slot selected, or the slot has no touch surface).</summary>
    public void Clear()
    {
        loading = true;
        try
        {
            saver = null;
            HasSlot = false;
            Gestures.Clear();
        }
        finally
        {
            loading = false;
        }
    }

    private void AddGesture()
    {
        var binding = new TouchGestureBinding
        {
            Kind = TouchGestureKind.Swipe,
            SwipeDirection = TouchSwipeDirection.Up,
            TargetButton = ButtonId.None
        };
        Gestures.Add(new TouchGestureRowViewModel(binding, this, Commit));
        Commit();
    }

    private void RemoveGesture(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }
        var row = Gestures.FirstOrDefault(g => g.Id == id);
        if (row is not null)
        {
            Gestures.Remove(row);
            Commit();
        }
    }

    private TouchpadMapRule BuildRule() => new()
    {
        Name = "Touchpad",
        StickEnabled = StickEnabled,
        TargetStick = TargetStick.Value,
        StickSensitivity = (float)StickSensitivity,
        DpadEnabled = DpadEnabled,
        DpadEightWay = DpadEightWay,
        DpadDeadzoneRadius = (float)DpadDeadzoneRadius,
        MouseEnabled = MouseEnabled,
        MouseSensitivityX = (float)MouseSensitivityX,
        MouseSensitivityY = (float)MouseSensitivityY,
        InvertMouseX = InvertMouseX,
        InvertMouseY = InvertMouseY,
        GesturesEnabled = GesturesEnabled,
        Gestures = [.. Gestures.Select(g => g.ToBinding())],
        SwipeMinDistance = (float)SwipeMinDistance,
        TapMaxMilliseconds = TapMaxMilliseconds,
        MultiTapWindowMilliseconds = MultiTapWindowMilliseconds,
        LongPressMilliseconds = LongPressMilliseconds,
        PinchThreshold = (float)PinchThreshold,
        RotateThresholdDegrees = (float)RotateThresholdDegrees,
        ShapeMatchThreshold = (float)ShapeMatchThreshold
    };

    private void Commit()
    {
        if (loading || saver is null)
        {
            return;
        }
        saver(BuildRule());
    }

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name))
        {
            return false;
        }
        Commit();
        return true;
    }

    private string Loc(string key, string fallback)
    {
        // The localizer echoes the key back when no resource matches, so
        // an untranslated key would otherwise surface in the UI as raw
        // "TouchpadHeader" text.
        var value = localization[key];
        return string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
    }

    private IReadOnlyList<TouchOption<TouchGestureKind>> BuildKindOptions() =>
    [
        new(TouchGestureKind.Swipe, Loc("TouchGestureSwipe", "Swipe")),
        new(TouchGestureKind.Tap, Loc("TouchGestureTap", "Tap")),
        new(TouchGestureKind.LongPress, Loc("TouchGestureLongPress", "Long press")),
        new(TouchGestureKind.Pinch, Loc("TouchGesturePinch", "Pinch")),
        new(TouchGestureKind.Rotate, Loc("TouchGestureRotate", "Rotate")),
        new(TouchGestureKind.Shape, Loc("TouchGestureShape", "Shape")),
    ];

    private IReadOnlyList<TouchOption<TouchSwipeDirection>> BuildSwipeOptions() =>
    [
        new(TouchSwipeDirection.Up, Loc("TouchDirUp", "Up")),
        new(TouchSwipeDirection.UpRight, Loc("TouchDirUpRight", "Up-right")),
        new(TouchSwipeDirection.Right, Loc("TouchDirRight", "Right")),
        new(TouchSwipeDirection.DownRight, Loc("TouchDirDownRight", "Down-right")),
        new(TouchSwipeDirection.Down, Loc("TouchDirDown", "Down")),
        new(TouchSwipeDirection.DownLeft, Loc("TouchDirDownLeft", "Down-left")),
        new(TouchSwipeDirection.Left, Loc("TouchDirLeft", "Left")),
        new(TouchSwipeDirection.UpLeft, Loc("TouchDirUpLeft", "Up-left")),
    ];

    private IReadOnlyList<TouchOption<TouchPinchDirection>> BuildPinchOptions() =>
    [
        new(TouchPinchDirection.In, Loc("TouchPinchIn", "In")),
        new(TouchPinchDirection.Out, Loc("TouchPinchOut", "Out")),
    ];

    private IReadOnlyList<TouchOption<TouchRotateDirection>> BuildRotateOptions() =>
    [
        new(TouchRotateDirection.Clockwise, Loc("TouchRotateCw", "Clockwise")),
        new(TouchRotateDirection.CounterClockwise, Loc("TouchRotateCcw", "Counter-clockwise")),
    ];

    private IReadOnlyList<TouchOption<TouchShape>> BuildShapeOptions() =>
    [
        new(TouchShape.CircleClockwise, Loc("TouchShapeCircleCw", "Circle (clockwise)")),
        new(TouchShape.CircleCounterClockwise, Loc("TouchShapeCircleCcw", "Circle (counter-clockwise)")),
        new(TouchShape.Square, Loc("TouchShapeSquare", "Square")),
        new(TouchShape.Triangle, Loc("TouchShapeTriangle", "Triangle")),
        new(TouchShape.Z, Loc("TouchShapeZ", "Z")),
        new(TouchShape.Checkmark, Loc("TouchShapeCheckmark", "Checkmark")),
    ];

    /// <summary>
    /// Every button a gesture can press, led by a "None" entry that
    /// leaves the binding inert — the state a freshly added row starts
    /// in, so adding one never fires something unexpected.
    /// </summary>
    private IReadOnlyList<TouchOption<ButtonId>> BuildButtonOptions()
    {
        var options = new List<TouchOption<ButtonId>>
        {
            new(ButtonId.None, Loc("TouchButtonNone", "— none —"))
        };
        foreach (var id in Enum.GetValues<ButtonId>())
        {
            if (id != ButtonId.None)
            {
                options.Add(new TouchOption<ButtonId>(id, id.ToString()));
            }
        }
        return options;
    }

    private void RaiseLabels()
    {
        OnPropertyChanged(nameof(JoystickSectionLabel));
        OnPropertyChanged(nameof(JoystickHint));
        OnPropertyChanged(nameof(StickEnabledLabel));
        OnPropertyChanged(nameof(TargetStickLabel));
        OnPropertyChanged(nameof(StickSensitivityLabel));
        OnPropertyChanged(nameof(DpadSectionLabel));
        OnPropertyChanged(nameof(DpadEnabledLabel));
        OnPropertyChanged(nameof(DpadEightWayLabel));
        OnPropertyChanged(nameof(DpadDeadzoneLabel));
        OnPropertyChanged(nameof(MouseSectionLabel));
        OnPropertyChanged(nameof(MouseHint));
        OnPropertyChanged(nameof(MouseEnabledLabel));
        OnPropertyChanged(nameof(MouseSensitivityXLabel));
        OnPropertyChanged(nameof(MouseSensitivityYLabel));
        OnPropertyChanged(nameof(InvertMouseXLabel));
        OnPropertyChanged(nameof(InvertMouseYLabel));
        OnPropertyChanged(nameof(GesturesSectionLabel));
        OnPropertyChanged(nameof(GesturesEnabledLabel));
        OnPropertyChanged(nameof(AddGestureLabel));
        OnPropertyChanged(nameof(RemoveLabel));
        OnPropertyChanged(nameof(NoGesturesLabel));
        OnPropertyChanged(nameof(GestureTargetLabel));
        OnPropertyChanged(nameof(GestureHoldLabel));
        OnPropertyChanged(nameof(GestureFingersLabel));
        OnPropertyChanged(nameof(GestureEightWayLabel));
        OnPropertyChanged(nameof(GestureTapCountLabel));
        OnPropertyChanged(nameof(TuningSectionLabel));
        OnPropertyChanged(nameof(SwipeMinDistanceLabel));
        OnPropertyChanged(nameof(TapMaxLabel));
        OnPropertyChanged(nameof(MultiTapWindowLabel));
        OnPropertyChanged(nameof(LongPressLabel));
        OnPropertyChanged(nameof(PinchThresholdLabel));
        OnPropertyChanged(nameof(RotateThresholdLabel));
        OnPropertyChanged(nameof(ShapeThresholdLabel));
    }
}
