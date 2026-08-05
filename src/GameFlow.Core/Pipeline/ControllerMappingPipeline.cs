using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Core.Models.Rules;
using GameFlow.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameFlow.Core.Pipeline;

public sealed class ControllerMappingPipeline(ProfileDocument profile) : IDisposable
{
    private readonly ProfileDocument profile = profile;

    /// <summary>
    /// Owned per pipeline (i.e. per slot / per active profile), matching
    /// every other piece of per-rule execution state below (schedulers,
    /// latches) -- a script's compiled bytecode and its persistent Lua
    /// `state` table are meaningless shared across two different slots.
    /// ControlScriptRule.cs's own doc comment: "Runtime execution is
    /// intentionally handled by higher layers" -- this IS that layer.
    /// </summary>
    private readonly LuaScriptEngine luaScriptEngine = new(NullLogger<LuaScriptEngine>.Instance);
    private readonly ShiftLayerResolver shiftLayerResolver = new();
    private readonly GyroProcessor gyroProcessor = new();

    /// <summary>Previous tick time, for converting the gyro's angular RATE into this tick's mouse movement.</summary>
    private DateTimeOffset lastGyroTickAt = DateTimeOffset.MinValue;

    private bool disposed;
    private readonly Dictionary<string, StickPulseScheduler> stickAutofireSchedulers = [];
    private readonly Dictionary<string, StickPulseScheduler> freezeSchedulers = [];
    private readonly Dictionary<string, BinaryPulseScheduler> buttonAutofireSchedulers = [];
    private readonly Dictionary<string, FreezeLatch> freezeLatches = [];

    /// <summary>
    /// Per-rule executor for looping multi-button autofire. Keyed by
    /// rule id so swapping a rule in/out of the profile rebuilds the
    /// state for that rule only — and never bleeds timeline state
    /// between two rules with the same trigger.
    /// </summary>
    private readonly Dictionary<string, MultiButtonAutofireExecutor> multiButtonExecutors = [];

    /// <summary>Per-rule executor for ButtonComboRule — same keyed-by-rule-id lifecycle as <see cref="multiButtonExecutors"/>.</summary>
    private readonly Dictionary<string, ButtonComboExecutor> comboExecutors = [];

    /// <summary>Per-rule SOCD conflict-tracking state, keyed by rule id.</summary>
    private sealed class SocdState
    {
        public ButtonId? CurrentFirst;
        public bool NegativeWasPressed;
        public bool PositiveWasPressed;
    }
    private readonly Dictionary<string, SocdState> socdStates = [];

    private SocdState GetOrCreateSocdState(string ruleId) =>
        socdStates.TryGetValue(ruleId, out var state) ? state : socdStates[ruleId] = new SocdState();

    /// <summary>Per-rule touch anchor point + previous-tick down state, keyed by rule id.</summary>
    private sealed class TouchAnchorState
    {
        public float AnchorX;
        public float AnchorY;
        public bool WasDown;

        /// <summary>
        /// Previous tick's raw touch position, for the mouse mode's
        /// frame-to-frame delta — DELIBERATELY separate from
        /// AnchorX/AnchorY, which stay fixed at the touch-down point for
        /// the stick/D-pad modes. Mouse movement is relative motion
        /// (how a laptop touchpad works), not anchor-relative.
        /// </summary>
        public float PrevMouseX;
        public float PrevMouseY;
    }
    private readonly Dictionary<string, TouchAnchorState> touchAnchors = [];

    /// <summary>
    /// Screen pixels a full 0..1 (edge-to-edge) touchpad sweep moves the
    /// cursor at MouseSensitivityX/Y = 1.0. Touchpads are physically
    /// small, so this favors a smaller-than-edge-to-edge swipe covering
    /// a meaningful chunk of a typical monitor over requiring a full
    /// sweep for any real movement; sensitivity scales linearly from
    /// here. Not derived from spec (no pixel figure was given) — a
    /// documented, tunable starting point.
    /// </summary>
    private const float MouseDeltaReferencePixels = 600f;

    private TouchAnchorState GetOrCreateTouchAnchor(string ruleId) =>
        touchAnchors.TryGetValue(ruleId, out var state) ? state : touchAnchors[ruleId] = new TouchAnchorState();

    /// <summary>Per-rule gesture recognizer, keyed by rule id. Stateful across ticks — a stroke spans many.</summary>
    private readonly Dictionary<string, TouchGestureEngine> touchGestureEngines = [];

    private TouchGestureEngine GetOrCreateGestureEngine(string ruleId) =>
        touchGestureEngines.TryGetValue(ruleId, out var engine)
            ? engine
            : touchGestureEngines[ruleId] = new TouchGestureEngine();

    /// <summary>
    /// Buttons a gesture is currently holding down, and when each hold
    /// expires. A gesture is an instantaneous event but has to present to
    /// the game as a press with duration, so firing one schedules an
    /// expiry here and every subsequent tick re-asserts the button until
    /// that time passes.
    /// </summary>
    private readonly Dictionary<ButtonId, DateTimeOffset> gestureHoldsUntil = [];

    /// <summary>
    /// Scratch buffer for the contact list handed to the recognizer.
    /// Reused because this is rebuilt every tick at up to 1000 Hz per
    /// slot, and a fresh list each time is pure garbage.
    /// </summary>
    private readonly List<TouchContact> gestureContactBuffer = [];

    /// <summary>
    /// The frame's contacts as the recognizer should see them.
    ///
    /// <para>
    /// Sources that track every finger (SDL, and so every DualSense /
    /// DualShock 4) fill <see cref="ControllerSnapshot.TouchContacts"/>
    /// and are passed through untouched. Sources that report only a
    /// primary contact get a single synthetic finger built from
    /// <c>TouchX</c>/<c>TouchY</c> — enough for swipes, taps, long
    /// presses and shapes, all of which only ever follow one finger.
    /// Pinch, rotate and the two-to-five-finger gestures simply never
    /// fire there, which is the honest outcome: the position data they
    /// need was never reported. Note this deliberately does NOT consult
    /// <c>TouchContactCount</c> — a source can count fingers it cannot
    /// place, and inventing positions to match the count would fabricate
    /// pinches out of nothing.
    /// </para>
    /// </summary>
    private IReadOnlyList<TouchContact> ResolveGestureContacts(ControllerSnapshot physical)
    {
        if (physical.TouchContacts.Count > 0)
        {
            return physical.TouchContacts;
        }

        gestureContactBuffer.Clear();
        if (physical.TouchDown)
        {
            gestureContactBuffer.Add(new TouchContact(0, physical.TouchX, physical.TouchY));
        }
        return gestureContactBuffer;
    }

    /// <summary>
    /// Runs one rule's gesture recognizer for this tick and schedules a
    /// timed press for every binding the emitted gestures satisfy.
    ///
    /// <para>
    /// One gesture can satisfy several bindings, and several gestures can
    /// land on the same tick (a pinch and a rotate often do), so this
    /// walks the full cross product rather than stopping at the first
    /// hit. When two bindings target the SAME button, the longer hold
    /// wins — extending an in-flight press is harmless, whereas letting a
    /// shorter one overwrite it would cut the first gesture's press
    /// short.
    /// </para>
    /// </summary>
    private void ProcessGestures(
        TouchpadMapRule rule, ControllerSnapshot physical,
        Dictionary<ButtonId, bool> buttons, DateTimeOffset now)
    {
        if (!rule.GesturesEnabled || rule.Gestures.Count == 0 || rule.Mode == RuleMode.DoNothing)
        {
            return;
        }

        var engine = GetOrCreateGestureEngine(rule.Id);
        var gestures = engine.Tick(ResolveGestureContacts(physical), rule, now);
        if (gestures.Count == 0)
        {
            return;
        }

        for (var g = 0; g < gestures.Count; g++)
        {
            for (var b = 0; b < rule.Gestures.Count; b++)
            {
                var binding = rule.Gestures[b];
                if (binding.TargetButton == ButtonId.None
                    || !TouchGestureEngine.Matches(binding, gestures[g]))
                {
                    continue;
                }

                var until = now.AddMilliseconds(Math.Max(1, binding.HoldMilliseconds));
                if (!gestureHoldsUntil.TryGetValue(binding.TargetButton, out var existing) || until > existing)
                {
                    gestureHoldsUntil[binding.TargetButton] = until;
                }

                // Press on the firing tick too, not just from the next
                // one. At a low polling rate a tick is several
                // milliseconds, and deferring would shave that off the
                // front of every gesture press for no reason.
                buttons[binding.TargetButton] = true;
            }
        }
    }

    /// <summary>
    /// Holds down every button with an unexpired gesture press and drops
    /// the entries that have run out. Expired holds are removed rather
    /// than set to false: the button may be genuinely held by the
    /// player's own thumb, and forcing it false would fight them.
    /// </summary>
    private void ApplyGestureHolds(Dictionary<ButtonId, bool> buttons, DateTimeOffset now)
    {
        if (gestureHoldsUntil.Count == 0)
        {
            return;
        }

        List<ButtonId>? expired = null;
        foreach (var (button, until) in gestureHoldsUntil)
        {
            if (now < until)
            {
                buttons[button] = true;
            }
            else
            {
                // Collected rather than removed in place — mutating a
                // dictionary while enumerating it throws.
                (expired ??= []).Add(button);
            }
        }

        if (expired is not null)
        {
            foreach (var button in expired)
            {
                gestureHoldsUntil.Remove(button);
            }
        }
    }

    /// <summary>
    /// Buckets an anchor-relative touch vector into D-pad button(s) and
    /// writes all four — clearing the ones NOT in the active wedge, not
    /// just setting the active one(s), so a stale press from a previous
    /// tick can't linger. Below <paramref name="deadzone"/>, all four
    /// stay false. 8-way diagonals hold two adjacent buttons at once
    /// (the standard way to represent 8 directions on 4 buttons).
    /// </summary>
    private static void ApplyWedgeDpad(float dx, float dy, float deadzone, bool eightWay, Dictionary<ButtonId, bool> buttons)
    {
        buttons[ButtonId.DpadUp] = false;
        buttons[ButtonId.DpadDown] = false;
        buttons[ButtonId.DpadLeft] = false;
        buttons[ButtonId.DpadRight] = false;

        if (MathF.Sqrt((dx * dx) + (dy * dy)) < deadzone)
        {
            return;
        }

        // atan2 convention here: 0deg = right (+X), 90deg = up (+Y), sweeping counter-clockwise, normalized to 0..360.
        var degrees = MathF.Atan2(dy, dx) * (180f / MathF.PI);
        if (degrees < 0f)
        {
            degrees += 360f;
        }

        if (!eightWay)
        {
            if (degrees is >= 315f or < 45f) { buttons[ButtonId.DpadRight] = true; }
            else if (degrees is >= 45f and < 135f) { buttons[ButtonId.DpadUp] = true; }
            else if (degrees is >= 135f and < 225f) { buttons[ButtonId.DpadLeft] = true; }
            else { buttons[ButtonId.DpadDown] = true; }
            return;
        }

        if (degrees is >= 337.5f or < 22.5f) { buttons[ButtonId.DpadRight] = true; }
        else if (degrees is >= 22.5f and < 67.5f) { buttons[ButtonId.DpadUp] = true; buttons[ButtonId.DpadRight] = true; }
        else if (degrees is >= 67.5f and < 112.5f) { buttons[ButtonId.DpadUp] = true; }
        else if (degrees is >= 112.5f and < 157.5f) { buttons[ButtonId.DpadUp] = true; buttons[ButtonId.DpadLeft] = true; }
        else if (degrees is >= 157.5f and < 202.5f) { buttons[ButtonId.DpadLeft] = true; }
        else if (degrees is >= 202.5f and < 247.5f) { buttons[ButtonId.DpadDown] = true; buttons[ButtonId.DpadLeft] = true; }
        else if (degrees is >= 247.5f and < 292.5f) { buttons[ButtonId.DpadDown] = true; }
        else { buttons[ButtonId.DpadDown] = true; buttons[ButtonId.DpadRight] = true; }
    }

    /// <summary>Stick Trim's current ramped output per rule id, persisted across ticks so the ramp has continuity.</summary>
    private readonly Dictionary<string, float> trimValues = [];

    /// <summary>Stick Trim's last-processed tick timestamp per rule id, for computing the per-tick ramp step (dt).</summary>
    private readonly Dictionary<string, DateTimeOffset> lastTrimTickAt = [];

    /// <summary>
    /// Compiled Formula evaluators per MultiSourceMapRule id. Null =
    /// compile failed (noted once, then inert). The pipeline is rebuilt
    /// on every profile save, so a formula can't change within one
    /// instance's life — rule.Id is a sufficient key.
    /// </summary>
    private readonly Dictionary<string, Func<float[], float>?> compiledFormulas = [];

    /// <summary>
    /// Runtime "is this rule currently muted" overlay maintained by
    /// <see cref="RuleToggleRule"/> executions. Targets that appear
    /// here are skipped by every <c>.Where(IsActive)</c> filter below.
    /// Resets to empty on each app launch (not persisted) — the
    /// profile JSON's Enabled flag stays the source of truth across
    /// restarts.
    /// </summary>
    private readonly HashSet<string> runtimeDisabledIds = [];

    /// <summary>
    /// Last-frame state of each rule-toggle's source button, used for
    /// rising-edge detection. Keyed by rule id (not button id) so two
    /// toggle rules sharing the same trigger don't fight over the
    /// edge.
    /// </summary>
    private readonly Dictionary<string, bool> toggleSourceWasPressed = [];

    // ── Rules bucketed by type, materialized ONCE at construction ──
    // The profile is immutable for this pipeline's lifetime (a profile
    // change disposes this instance and builds a new one), so bucketing
    // here is always correct and never goes stale. Previously every
    // pass ran profile.Rules.OfType<T>().Where(IsActive) per tick: 14
    // passes x (OfType iterator + Where iterator + IsActive delegate)
    // was ~670k allocations/second at 1000 Hz across 16 slots, purely
    // to re-filter a list that never changes. Arrays also iterate
    // without an enumerator allocation, unlike List<T> through an
    // interface. Order within each bucket is preserved, so
    // last-write-wins rule precedence is unchanged.
    private readonly RuleToggleRule[] ruleToggleRules = profile.Rules.OfType<RuleToggleRule>().ToArray();
    private readonly ButtonRemapRule[] buttonRemapRules = profile.Rules.OfType<ButtonRemapRule>().ToArray();
    private readonly ButtonAutofireRule[] buttonAutofireRules = profile.Rules.OfType<ButtonAutofireRule>().ToArray();
    private readonly MultiButtonAutofireRule[] multiButtonRules = profile.Rules.OfType<MultiButtonAutofireRule>().ToArray();
    private readonly ButtonComboRule[] buttonComboRules = profile.Rules.OfType<ButtonComboRule>().ToArray();
    private readonly SocdCleanRule[] socdCleanRules = profile.Rules.OfType<SocdCleanRule>().ToArray();
    private readonly StickAutofireRule[] stickAutofireRules = profile.Rules.OfType<StickAutofireRule>().ToArray();
    private readonly FreezeLastDirectionRule[] freezeRules = profile.Rules.OfType<FreezeLastDirectionRule>().ToArray();
    private readonly StickTrimRule[] stickTrimRules = profile.Rules.OfType<StickTrimRule>().ToArray();
    private readonly GyroMapRule[] gyroRules = profile.Rules.OfType<GyroMapRule>().ToArray();
    private readonly TouchpadMapRule[] touchpadRules = profile.Rules.OfType<TouchpadMapRule>().ToArray();
    private readonly MultiSourceMapRule[] multiSourceRules = profile.Rules.OfType<MultiSourceMapRule>().ToArray();
    private readonly ControlScriptRule[] controlScriptRules = profile.Rules.OfType<ControlScriptRule>().ToArray();
    private readonly StickThresholdRule[] stickThresholdRules = profile.Rules.OfType<StickThresholdRule>().ToArray();

    // Issue #12: Track previous button states for rising-edge detection on freeze rules.
    private readonly Dictionary<ButtonId, bool> previousButtonStates = [];

    /// <summary>Dense array size for the ControlScriptRule bool[] round-trip -- see the Script pass in <see cref="Process"/>.</summary>
    private static readonly int buttonIdCount = Enum.GetValues<ButtonId>().Length;

    /// <summary>
    /// Releases this pipeline's owned <see cref="LuaScriptEngine"/> (compiled
    /// scripts + their persistent Lua state tables). Callers that replace a
    /// pipeline instance -- profile switch, slot rebuild -- must call this on
    /// the OLD instance, the same discipline applied to output sinks: a
    /// replaced-without-disposed pipeline leaks every script it had loaded.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        luaScriptEngine.Dispose();
    }

    /// <summary>
    /// Helper that combines a rule's authored Enabled flag with the
    /// runtime-disabled overlay maintained by <see cref="RuleToggleRule"/>
    /// executions. Used in every <c>.Where(...)</c> below so toggle
    /// state actually mutes downstream rule processing.
    /// </summary>
    private bool IsActive(MappingRule rule) =>
        rule.Enabled && !runtimeDisabledIds.Contains(rule.Id)
        && (string.IsNullOrEmpty(rule.LayerId) || rule.LayerId == shiftLayerResolver.ActiveLayerId);

    public ControllerFrameResult Process(ControllerSnapshot physical, DateTimeOffset now)
    {
        var notes = new List<string>();
        var buttons = ButtonState.Clone(physical.Buttons);

        // Shift layers resolve FIRST — every subsequent pass gates
        // through IsActive, which now also checks layer membership, so
        // this must run before anything else reads it.
        var layerNote = shiftLayerResolver.Resolve(profile.ShiftLayers, physical, now);
        if (layerNote is not null)
        {
            notes.Add(layerNote);
        }

        // RuleToggleRule pass — must run before any rule whose Enabled
        // state it might flip. On the rising edge of the toggle's
        // source button, every target id is flipped in/out of the
        // runtime-disabled set, which the IsActive helper consults
        // below.
        foreach (var rule in ruleToggleRules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            if (rule.SourceButton == ButtonId.None) { continue; }

            var pressed = physical.IsPressed(rule.SourceButton);
            toggleSourceWasPressed.TryGetValue(rule.Id, out var wasPressed);

            if (pressed && !wasPressed)
            {
                foreach (var targetId in rule.TargetRuleIds)
                {
                    if (string.IsNullOrWhiteSpace(targetId)) { continue; }
                    if (!runtimeDisabledIds.Remove(targetId))
                    {
                        runtimeDisabledIds.Add(targetId);
                    }
                }
                notes.Add($"Toggled {rule.TargetRuleIds.Count} rule(s) via {rule.SourceButton}.");
            }
            toggleSourceWasPressed[rule.Id] = pressed;

            if (rule.SuppressSourceButton)
            {
                buttons[rule.SourceButton] = false;
            }
        }

        // Issue #10: Use the threshold-adjusted map as the baseline for the output sticks.
        // Previously leftStick/rightStick were seeded from physical.LeftStick/RightStick,
        // which meant StickThresholdRule deadzones only affected autofire source reads,
        // not the final virtual output. Now thresholds apply to the output directly.
        var transformedSticks = BuildSourceStickMap(physical);
        var leftStick = transformedSticks[StickId.Left];
        var rightStick = transformedSticks[StickId.Right];
        var leftTrigger = physical.LeftTrigger;
        var rightTrigger = physical.RightTrigger;

        // Accumulated this tick by TouchpadMapRule's mouse mode below.
        // Unlike buttons/sticks/triggers, this has no "current state" to
        // read back — it's a relative delta for whatever consumes
        // ControllerFrameResult (the runtime's mouse writer), reset to
        // zero every tick.
        var mouseDeltaX = 0f;
        var mouseDeltaY = 0f;

        // SOCD cleaning runs first among the button passes — it's input
        // hygiene (resolving a conflicting opposite-direction pair down
        // to what the pair SHOULD report), not a creative remap, so
        // everything downstream (remaps, autofire, combos) should see
        // the already-cleaned result rather than raw dual input.
        foreach (var rule in socdCleanRules)
        {
            if (!IsActive(rule))
            {
                continue;
            }

            if (rule.Mode == RuleMode.Passthrough
                || rule.NegativeButton == ButtonId.None || rule.PositiveButton == ButtonId.None)
            {
                continue;
            }
            if (rule.Mode == RuleMode.DoNothing)
            {
                buttons[rule.NegativeButton] = false;
                buttons[rule.PositiveButton] = false;
                continue;
            }

            var negNow = physical.IsPressed(rule.NegativeButton);
            var posNow = physical.IsPressed(rule.PositiveButton);
            var state = GetOrCreateSocdState(rule.Id);

            if (!negNow || !posNow)
            {
                // No conflict this tick (at most one held) — nothing to
                // clean, both pass through as-is. Reset so the NEXT
                // overlap episode re-establishes "which was first" fresh.
                state.CurrentFirst = null;
            }
            else
            {
                if (state.CurrentFirst is null)
                {
                    // Conflict just started. Whichever one was ALREADY
                    // held before this tick is "first"; a same-tick
                    // simultaneous press (both rising together) is an
                    // arbitrary but consistent tie-break toward Negative.
                    var negRising = !state.NegativeWasPressed;
                    var posRising = !state.PositiveWasPressed;
                    state.CurrentFirst = negRising && !posRising ? rule.PositiveButton
                        : posRising && !negRising ? rule.NegativeButton
                        : rule.NegativeButton;
                }

                ButtonId? winner = rule.SocdMode switch
                {
                    SocdMode.FirstWins => state.CurrentFirst,
                    SocdMode.LastWins => state.CurrentFirst == rule.NegativeButton ? rule.PositiveButton : rule.NegativeButton,
                    SocdMode.Neutral => null,
                    _ => null
                };

                if (winner != rule.NegativeButton) { buttons[rule.NegativeButton] = false; }
                if (winner != rule.PositiveButton) { buttons[rule.PositiveButton] = false; }
            }

            state.NegativeWasPressed = negNow;
            state.PositiveWasPressed = posNow;
        }

        foreach (var rule in buttonRemapRules)
        {
            if (!IsActive(rule))
            {
                continue;
            }

            if (rule.Mode == RuleMode.Passthrough)
            {
                continue;
            }

            if (rule.Mode == RuleMode.DoNothing)
            {
                buttons[rule.SourceButton] = false;
                notes.Add($"Blocked {rule.SourceButton}.");
                continue;
            }

            if (!physical.IsPressed(rule.SourceButton))
            {
                continue;
            }

            buttons[rule.TargetButton] = true;
            if (rule.SuppressSourceButton)
            {
                buttons[rule.SourceButton] = false;
            }

            notes.Add($"Remapped {rule.SourceButton} -> {rule.TargetButton}.");
        }

        foreach (var rule in buttonAutofireRules)
        {
            if (!IsActive(rule))
            {
                continue;
            }

            var scheduler = GetOrCreateBinaryScheduler(rule);
            scheduler.SetDesired(physical.IsPressed(rule.SourceButton), now);
            var pulse = scheduler.Tick(now);

            if (rule.Mode == RuleMode.DoNothing)
            {
                buttons[rule.SourceButton] = false;
                continue;
            }

            if (rule.Mode == RuleMode.Passthrough)
            {
                continue;
            }

            if (pulse.IsPressed)
            {
                buttons[rule.TargetButton] = true;
            }

            if (rule.SuppressSourceButton)
            {
                buttons[rule.SourceButton] = false;
            }
        }

        foreach (var rule in multiButtonRules)
        {
            if (!IsActive(rule))
            {
                continue;
            }

            if (rule.Mode == RuleMode.Passthrough)
            {
                continue;
            }
            if (rule.Mode == RuleMode.DoNothing)
            {
                if (rule.SourceButton != ButtonId.None)
                {
                    buttons[rule.SourceButton] = false;
                }
                continue;
            }

            if (!multiButtonExecutors.TryGetValue(rule.Id, out var executor))
            {
                executor = new MultiButtonAutofireExecutor(rule);
                multiButtonExecutors[rule.Id] = executor;
            }
            executor.Apply(physical, buttons, now);
        }

        // ButtonComboRule: fully built (ButtonComboExecutor.cs) since
        // before this pipeline existed, but never called — see this
        // file's history for ControlScriptRule, which had the exact
        // same problem. Positioned beside MultiButtonAutofireRule above
        // since both are button-triggered macro/combo mechanisms.
        foreach (var rule in buttonComboRules)
        {
            if (!IsActive(rule))
            {
                continue;
            }

            if (rule.Mode == RuleMode.Passthrough)
            {
                continue;
            }
            if (rule.Mode == RuleMode.DoNothing)
            {
                if (rule.SourceButton != ButtonId.None)
                {
                    buttons[rule.SourceButton] = false;
                }
                continue;
            }

            if (!comboExecutors.TryGetValue(rule.Id, out var comboExecutor))
            {
                comboExecutor = new ButtonComboExecutor(rule);
                comboExecutors[rule.Id] = comboExecutor;
            }

            // ButtonComboExecutor.Apply wants a dense bool[] (it indexes
            // by (int)ButtonId directly) — same round-trip as the Script
            // pass below, scoped to just this rule's execution.
            var comboArray = new bool[buttonIdCount];
            foreach (var (id, pressed) in buttons)
            {
                comboArray[(int)id] = pressed;
            }

            comboExecutor.Apply(physical, comboArray, now);

            for (var i = 0; i < comboArray.Length; i++)
            {
                buttons[(ButtonId)i] = comboArray[i];
            }
        }

        foreach (var rule in stickAutofireRules)
        {
            if (!IsActive(rule))
            {
                continue;
            }

            if (rule.Mode == RuleMode.Passthrough)
            {
                continue;
            }

            if (rule.Mode == RuleMode.DoNothing)
            {
                SetStick(ref leftStick, ref rightStick, rule.TargetStick, StickVector.Zero);
                continue;
            }

            var source = transformedSticks[rule.SourceStick]
                .WithDeadzone(rule.ActivationDeadzone)
                .AmplifyToFull(rule.ActivationFullAt);
            var scheduler = GetOrCreateStickScheduler(rule);
            scheduler.SetDesired(source, now);
            var pulse = scheduler.Tick(now).Value;

            var current = GetStick(leftStick, rightStick, rule.TargetStick);
            var merged = ApplyBlend(current, pulse, rule.BlendMode);
            SetStick(ref leftStick, ref rightStick, rule.TargetStick, merged);

            if (rule.SuppressSourceStick)
            {
                SetStick(ref leftStick, ref rightStick, rule.SourceStick, StickVector.Zero);
            }
        }

        foreach (var rule in freezeRules)
        {
            if (!IsActive(rule))
            {
                continue;
            }

            if (rule.Mode == RuleMode.Passthrough)
            {
                continue;
            }

            if (rule.Mode == RuleMode.DoNothing)
            {
                SetStick(ref leftStick, ref rightStick, rule.TargetStick, StickVector.Zero);
                if (rule.SuppressActivationButton)
                {
                    buttons[rule.ActivationButton] = false;
                }
                continue;
            }

            var latch = GetOrCreateFreezeLatch(rule);
            var buttonNowPressed = physical.IsPressed(rule.ActivationButton);
            var buttonWasPressed = previousButtonStates.GetValueOrDefault(rule.ActivationButton);
            previousButtonStates[rule.ActivationButton] = buttonNowPressed;

            // Issue #12: Capture the stick vector only at the RISING EDGE of the
            // activation button. This means the latch holds exactly what the stick
            // was doing the instant you pressed the button (including zero / idle),
            // rather than the last-ever non-zero historical position.
            if (buttonNowPressed && !buttonWasPressed)
            {
                latch.CaptureSnapshot(transformedSticks[rule.CaptureStick]);
            }

            if (buttonNowPressed)
            {
                var frozenVector = latch.Current;

                if (rule.PulseEnabled)
                {
                    var scheduler = GetOrCreateFreezeScheduler(rule);
                    scheduler.SetDesired(frozenVector, now);
                    frozenVector = scheduler.Tick(now).Value;
                }

                var currentTarget = GetStick(leftStick, rightStick, rule.TargetStick);
                var merged = ApplyBlend(currentTarget, frozenVector, rule.BlendMode);
                SetStick(ref leftStick, ref rightStick, rule.TargetStick, merged);

                if (rule.SuppressActivationButton)
                {
                    buttons[rule.ActivationButton] = false;
                }

                if (rule.SuppressCaptureStick)
                {
                    SetStick(ref leftStick, ref rightStick, rule.CaptureStick, StickVector.Zero);
                }
            }
            else
            {
                // Button released: clear the freeze scheduler so the next press starts fresh.
                if (freezeSchedulers.TryGetValue(rule.Id, out var scheduler))
                {
                    scheduler.Clear();
                }
            }
        }

        // Stick Trim: "squeeze a digital trigger like it's analog."
        // Grouped with the other stick-driven, latch-style rules above.
        foreach (var rule in stickTrimRules)
        {
            if (!IsActive(rule))
            {
                continue;
            }

            if (rule.Mode == RuleMode.Passthrough || rule.ArmButton == ButtonId.None)
            {
                continue;
            }
            if (rule.Mode == RuleMode.DoNothing)
            {
                trimValues.Remove(rule.Id);
                continue;
            }

            var armed = physical.IsPressed(rule.ArmButton);
            var current = trimValues.GetValueOrDefault(rule.Id);
            var dtSeconds = MathF.Max(0f, (float)(now - (lastTrimTickAt.TryGetValue(rule.Id, out var lastAt) ? lastAt : now)).TotalSeconds);
            lastTrimTickAt[rule.Id] = now;

            float next;
            if (armed)
            {
                var stickValue = GetStick(leftStick, rightStick, rule.ModulatorStick);
                var magnitude = stickValue.Magnitude;
                var target = magnitude < rule.Deadzone ? 0f
                    : Math.Clamp((magnitude - rule.Deadzone) / (1f - rule.Deadzone), 0f, 1f);

                if (rule.SuppressModulatorStick)
                {
                    SetStick(ref leftStick, ref rightStick, rule.ModulatorStick, StickVector.Zero);
                }

                // Ramp toward the target rather than jumping straight to
                // it — a fast stick flick shouldn't spike the trigger
                // instantly. RampRatePerSecond <= 0 would never move, so
                // treat it as "jump immediately" instead of dividing by
                // zero / stalling forever.
                var maxStep = rule.RampRatePerSecond > 0f ? rule.RampRatePerSecond * dtSeconds : float.MaxValue;
                next = current < target
                    ? MathF.Min(current + maxStep, target)
                    : MathF.Max(current - maxStep, target);
            }
            else
            {
                // Released: NOT a ramp in either direction — "snaps back
                // to zero the MOMENT you let go" means instant, and the
                // freeze alternative means instant too (simply don't
                // move at all). Ramping only happens while armed.
                next = rule.ResetOnRelease ? 0f : current;
            }

            trimValues[rule.Id] = next;

            if (rule.TargetTrigger == TriggerId.Left)
            {
                leftTrigger = next;
            }
            else
            {
                rightTrigger = next;
            }
        }

        // Gyro → stick or mouse. Runs before the multi-source rows so a
        // row can read the gyro-driven stick as one of its sources, and
        // before the touchpad pass so an explicit touchpad stick mapping
        // wins over gyro if both target the same stick (last write wins,
        // the same ordering convention every other rule here follows).
        foreach (var rule in gyroRules)
        {
            if (!IsActive(rule))
            {
                continue;
            }

            if (rule.Mode == RuleMode.Passthrough)
            {
                continue;
            }
            if (rule.Mode == RuleMode.DoNothing)
            {
                if (rule.OutputTarget == GyroOutputTarget.Stick)
                {
                    SetStick(ref leftStick, ref rightStick, rule.TargetStick, StickVector.Zero);
                }
                continue;
            }

            var aim = gyroProcessor.Process(rule, physical);

            if (rule.OutputTarget == GyroOutputTarget.Stick)
            {
                // Write even when disengaged, so releasing an Aim Engage
                // button actually re-centres the stick instead of
                // leaving it stuck at the last deflection.
                SetStick(ref leftStick, ref rightStick, rule.TargetStick, GyroProcessor.ToStick(aim));
            }
            else if (aim.Engaged)
            {
                // First tick has no previous timestamp to measure
                // against — a zero delta means no movement rather than
                // the clamp ceiling, which would jump the cursor on the
                // very first frame. Same guard as the touchpad's
                // first-contact case.
                var deltaSeconds = lastGyroTickAt == DateTimeOffset.MinValue
                    ? 0f
                    : (float)Math.Clamp((now - lastGyroTickAt).TotalSeconds, 0d, 0.1d);
                var (dx, dy) = GyroProcessor.ToMouseDelta(aim, deltaSeconds);
                mouseDeltaX += dx;
                mouseDeltaY += dy;
            }
        }
        lastGyroTickAt = now;

        // Touchpad → stick anchor / wedge D-pad. See TouchpadMapRule.cs
        // for why mouse X/Y isn't here.
        foreach (var rule in touchpadRules)
        {
            if (!IsActive(rule))
            {
                // A rule switched off mid-stroke must not resume that
                // stroke when it comes back — its start timestamp would
                // by then be arbitrarily old, and the finger still resting
                // on the pad would classify as an enormous long press.
                if (touchGestureEngines.TryGetValue(rule.Id, out var idleEngine))
                {
                    idleEngine.Reset();
                }
                continue;
            }

            if (rule.Mode == RuleMode.Passthrough)
            {
                continue;
            }

            ProcessGestures(rule, physical, buttons, now);

            var state = GetOrCreateTouchAnchor(rule.Id);
            var down = rule.Mode != RuleMode.DoNothing && physical.TouchDown;

            if (down && !state.WasDown)
            {
                state.AnchorX = physical.TouchX;
                state.AnchorY = physical.TouchY;
            }

            if (down)
            {
                var dx = physical.TouchX - state.AnchorX;
                // Touch Y follows SDL's screen convention (0 = top); this
                // codebase's stick Y is up-positive (see the real stick
                // read a few hundred lines up: `-NormalizeSignedAxis(...Y)`)
                // — negate here to match that same convention.
                var dy = -(physical.TouchY - state.AnchorY);

                if (rule.StickEnabled)
                {
                    var stick = new StickVector(
                        Math.Clamp(dx * rule.StickSensitivity, -1f, 1f),
                        Math.Clamp(dy * rule.StickSensitivity, -1f, 1f));
                    SetStick(ref leftStick, ref rightStick, rule.TargetStick, stick);
                }

                if (rule.DpadEnabled)
                {
                    ApplyWedgeDpad(dx, dy, rule.DpadDeadzoneRadius, rule.DpadEightWay, buttons);
                }

                if (rule.MouseEnabled && state.WasDown)
                {
                    // Frame-to-frame, NOT anchor-relative — how a laptop
                    // touchpad actually works: each tick's finger
                    // movement nudges the cursor, it doesn't matter
                    // where the finger originally landed. Gated on
                    // state.WasDown (not just `down`) so the FIRST
                    // contact tick contributes no delta — there is no
                    // meaningful "previous position" to diff against yet,
                    // and without this gate that tick would read as a
                    // teleport from (0,0) to wherever the finger landed.
                    var mdx = physical.TouchX - state.PrevMouseX;
                    // Touch Y and screen Y both grow downward already —
                    // UNLIKE the stick/D-pad negation above, no flip
                    // needed here; a finger dragged down should move the
                    // cursor down.
                    var mdy = physical.TouchY - state.PrevMouseY;

                    mouseDeltaX += mdx * rule.MouseSensitivityX * MouseDeltaReferencePixels * (rule.InvertMouseX ? -1f : 1f);
                    mouseDeltaY += mdy * rule.MouseSensitivityY * MouseDeltaReferencePixels * (rule.InvertMouseY ? -1f : 1f);
                }

                state.PrevMouseX = physical.TouchX;
                state.PrevMouseY = physical.TouchY;
            }
            else
            {
                if (rule.StickEnabled)
                {
                    SetStick(ref leftStick, ref rightStick, rule.TargetStick, StickVector.Zero);
                }
                if (rule.DpadEnabled)
                {
                    buttons[ButtonId.DpadUp] = false;
                    buttons[ButtonId.DpadDown] = false;
                    buttons[ButtonId.DpadLeft] = false;
                    buttons[ButtonId.DpadRight] = false;
                }
            }

            state.WasDown = down;
        }

        // Re-assert every gesture-driven button that hasn't expired.
        // Done AFTER the touchpad rules so a gesture bound to a D-pad
        // direction wins over the wedge D-pad's own writes for the length
        // of its pulse — the gesture is the more deliberate act — and
        // BEFORE multi-source rows and scripts, so both can read
        // gesture-driven buttons as ordinary sources.
        ApplyGestureHolds(buttons, now);

        // Multi-source mapping rows: many inputs, one output, a combine
        // mode (or formula) deciding how they fold together. Sources
        // read from the ACCUMULATED working state — buttons the remap
        // passes already produced, sticks/triggers as shaped so far —
        // so rows compose with everything above; scripts (below) still
        // get the final say. A row OWNS its target for the tick: below
        // threshold releases a button target even if it's physically
        // held, which is what makes "these sources and nothing else"
        // mappings possible.
        foreach (var rule in multiSourceRules)
        {
            if (!IsActive(rule))
            {
                continue;
            }

            if (rule.Mode == RuleMode.Passthrough || rule.Sources.Count == 0)
            {
                continue;
            }
            if (rule.Mode == RuleMode.DoNothing)
            {
                WriteMapTarget(rule, 0f, ref leftStick, ref rightStick, ref leftTrigger, ref rightTrigger, buttons);
                continue;
            }

            var values = new float[rule.Sources.Count];
            for (var i = 0; i < rule.Sources.Count; i++)
            {
                values[i] = ReadMapSource(rule.Sources[i], buttons, leftStick, rightStick, leftTrigger, rightTrigger, physical);
            }

            float combined;
            if (rule.CombineMode == CombineMode.Formula)
            {
                if (!compiledFormulas.TryGetValue(rule.Id, out var evaluator))
                {
                    evaluator = Formulas.FormulaExpression.Compile(rule.Formula, rule.Sources.Count, out var formulaError);
                    compiledFormulas[rule.Id] = evaluator;
                    if (evaluator is null)
                    {
                        notes.Add($"Formula in '{rule.Name}' failed to compile: {formulaError}");
                    }
                }
                if (evaluator is null)
                {
                    continue; // compile failed — noted once above, rule inert until the profile is edited (which rebuilds this pipeline)
                }
                combined = evaluator(values);
            }
            else
            {
                combined = CombineValues(rule.CombineMode, values);
            }

            if (rule.SuppressSources)
            {
                foreach (var source in rule.Sources)
                {
                    SuppressMapSource(source, buttons, ref leftStick, ref rightStick, ref leftTrigger, ref rightTrigger);
                }
            }

            WriteMapTarget(rule, combined, ref leftStick, ref rightStick, ref leftTrigger, ref rightTrigger, buttons);
        }

        foreach (var rule in controlScriptRules)
        {
            if (!IsActive(rule))
            {
                continue;
            }

            if (rule.Mode == RuleMode.Passthrough)
            {
                continue;
            }
            if (rule.Mode == RuleMode.DoNothing || string.IsNullOrWhiteSpace(rule.ScriptCode))
            {
                continue;
            }

            // LuaScriptEngine wants a dense bool[] indexed by (int)ButtonId
            // (its press()/release() callbacks index it directly); the
            // rest of this method carries button state as a
            // Dictionary<ButtonId,bool>. Round-trip through the array
            // only for the duration of this one rule's execution.
            var buttonArray = new bool[buttonIdCount];
            foreach (var (id, pressed) in buttons)
            {
                buttonArray[(int)id] = pressed;
            }

            // virtualBefore isn't read by Execute today (checked against
            // the engine's own source); passing `physical` rather than a
            // null-forgiving placeholder keeps this call honest if that
            // ever changes.
            luaScriptEngine.Execute(
                rule, physical, virtualBefore: physical, buttonArray,
                ref leftStick, ref rightStick, ref leftTrigger, ref rightTrigger, now);

            for (var i = 0; i < buttonArray.Length; i++)
            {
                buttons[(ButtonId)i] = buttonArray[i];
            }

            // SuppressSourceInput: the doc comment on ControlScriptRule
            // flags this as "future-facing" with no fixed semantics yet.
            // The one unambiguous case is ControlKey naming a button
            // outright ("South") -- suppress that source button the same
            // way every other rule's SuppressSourceButton does. A
            // dotted key ("LeftStick.Button", "RightTrigger.Analog")
            // names a stick or trigger; deliberately NOT guessing which
            // axis/sub-control to zero there, since a wrong guess would
            // silently kill legitimate output rather than just no-op.
            if (rule.SuppressSourceInput
                && Enum.TryParse<ButtonId>(rule.ControlKey, ignoreCase: true, out var sourceButton))
            {
                buttons[sourceButton] = false;
            }
        }

        var virtualSnapshot = physical
            .WithStick(StickId.Left, leftStick.Clamp())
            .WithStick(StickId.Right, rightStick.Clamp())
            .WithButtons(buttons)
            .WithTriggers(leftTrigger, rightTrigger)
            with
            {
                Timestamp = now,
                DeviceName = $"{physical.DeviceName} / virtual"
            };

        return new ControllerFrameResult(physical with { Timestamp = now }, virtualSnapshot, notes)
        {
            ActiveLayerId = shiftLayerResolver.ActiveLayerId,
            MouseDeltaX = mouseDeltaX,
            MouseDeltaY = mouseDeltaY
        };
    }

    /// <summary>
    /// Builds a threshold-adjusted stick map. This map now also seeds the baseline
    /// output stick values (leftStick, rightStick) so that StickThresholdRule
    /// deadzones and amplification are reflected in the final virtual output.
    /// </summary>
    private Dictionary<StickId, StickVector> BuildSourceStickMap(ControllerSnapshot snapshot)
    {
        var map = new Dictionary<StickId, StickVector>
        {
            [StickId.Left] = snapshot.LeftStick,
            [StickId.Right] = snapshot.RightStick
        };

        foreach (var rule in stickThresholdRules)
        {
            if (!IsActive(rule) || rule.Mode == RuleMode.Passthrough)
            {
                continue;
            }

            if (rule.Mode == RuleMode.DoNothing)
            {
                map[rule.TargetStick] = StickVector.Zero;
                continue;
            }

            map[rule.TargetStick] = map[rule.TargetStick]
                .WithDeadzone(rule.Deadzone)
                .AmplifyToFull(rule.FullAt);
        }

        return map;
    }

    private static StickVector ApplyBlend(StickVector current, StickVector pulse, StickBlendMode blendMode)
    {
        return blendMode switch
        {
            StickBlendMode.Replace => pulse.Clamp(),
            StickBlendMode.Additive => (current + pulse).Clamp(),
            _ => current
        };
    }

    private static StickVector GetStick(StickVector leftStick, StickVector rightStick, StickId stickId)
    {
        return stickId == StickId.Left ? leftStick : rightStick;
    }

    /// <summary>Reads one <see cref="MapSource"/> as a float from the accumulated working state. Button 0/1, trigger 0..1, axis -1..1, magnitude 0..1; Invert flips (1-v unsigned, -v signed).</summary>
    private static float ReadMapSource(
        MapSource source, Dictionary<ButtonId, bool> buttons,
        StickVector leftStick, StickVector rightStick, float leftTrigger, float rightTrigger,
        ControllerSnapshot physical)
    {
        var value = source.Kind switch
        {
            MapSourceKind.Button => buttons.GetValueOrDefault(source.Button) ? 1f : 0f,
            MapSourceKind.StickAxisX => GetStick(leftStick, rightStick, source.Stick).X,
            MapSourceKind.StickAxisY => GetStick(leftStick, rightStick, source.Stick).Y,
            MapSourceKind.StickMagnitude => GetStick(leftStick, rightStick, source.Stick).Magnitude,
            MapSourceKind.Trigger => source.Trigger == TriggerId.Left ? leftTrigger : rightTrigger,
            // Gyro is read from the physical snapshot rather than the
            // accumulated working state: no earlier pass writes angular
            // velocity, so there's nothing accumulated to read.
            MapSourceKind.GyroPitch => physical.GyroPitch,
            MapSourceKind.GyroYaw => physical.GyroYaw,
            MapSourceKind.GyroRoll => physical.GyroRoll,
            _ => 0f
        };

        if (!source.Invert)
        {
            return value;
        }
        // Signed sources mirror around zero; unsigned ones (button,
        // trigger, magnitude — all bounded to 0..1) flip within that
        // range. Gyro belongs with the SIGNED group: angular velocity is
        // signed AND unbounded, so "1 - v" would be meaningless for it.
        return source.Kind is MapSourceKind.StickAxisX or MapSourceKind.StickAxisY
            or MapSourceKind.GyroPitch or MapSourceKind.GyroYaw or MapSourceKind.GyroRoll
            ? -value
            : 1f - value;
    }

    /// <summary>Folds the source values per the (non-Formula) combine mode.</summary>
    private static float CombineValues(CombineMode mode, float[] values)
    {
        const float FirstActiveEpsilon = 0.01f;
        switch (mode)
        {
            case CombineMode.Maximum:
            {
                var best = values[0];
                for (var i = 1; i < values.Length; i++) { best = MathF.Max(best, values[i]); }
                return best;
            }
            case CombineMode.Minimum:
            {
                var worst = values[0];
                for (var i = 1; i < values.Length; i++) { worst = MathF.Min(worst, values[i]); }
                return worst;
            }
            case CombineMode.Sum:
            {
                var sum = 0f;
                foreach (var v in values) { sum += v; }
                return sum;
            }
            case CombineMode.Average:
            {
                var sum = 0f;
                foreach (var v in values) { sum += v; }
                return sum / values.Length;
            }
            case CombineMode.Multiply:
            {
                var product = 1f;
                foreach (var v in values) { product *= v; }
                return product;
            }
            case CombineMode.FirstActive:
            {
                foreach (var v in values)
                {
                    if (MathF.Abs(v) > FirstActiveEpsilon) { return v; }
                }
                return 0f;
            }
            default:
                return 0f;
        }
    }

    /// <summary>Zeroes one source's own contribution in the virtual output.</summary>
    private static void SuppressMapSource(
        MapSource source, Dictionary<ButtonId, bool> buttons,
        ref StickVector leftStick, ref StickVector rightStick, ref float leftTrigger, ref float rightTrigger)
    {
        switch (source.Kind)
        {
            case MapSourceKind.Button:
                if (source.Button != ButtonId.None) { buttons[source.Button] = false; }
                break;
            case MapSourceKind.StickAxisX:
            {
                var stick = GetStick(leftStick, rightStick, source.Stick) with { X = 0f };
                SetStick(ref leftStick, ref rightStick, source.Stick, stick);
                break;
            }
            case MapSourceKind.StickAxisY:
            {
                var stick = GetStick(leftStick, rightStick, source.Stick) with { Y = 0f };
                SetStick(ref leftStick, ref rightStick, source.Stick, stick);
                break;
            }
            case MapSourceKind.StickMagnitude:
                SetStick(ref leftStick, ref rightStick, source.Stick, StickVector.Zero);
                break;
            case MapSourceKind.Trigger:
                if (source.Trigger == TriggerId.Left) { leftTrigger = 0f; } else { rightTrigger = 0f; }
                break;

            // Gyro axes are deliberately absent: they're read straight
            // from the physical snapshot, and nothing in the virtual
            // output carries angular velocity, so there is no
            // contribution to suppress. Falling through is correct here,
            // not a missing case.
        }
    }

    /// <summary>Writes the combined value to the row's target — button via threshold, axis preserving its sibling axis, trigger clamped.</summary>
    private static void WriteMapTarget(
        MultiSourceMapRule rule, float combined,
        ref StickVector leftStick, ref StickVector rightStick, ref float leftTrigger, ref float rightTrigger,
        Dictionary<ButtonId, bool> buttons)
    {
        switch (rule.TargetKind)
        {
            case MapTargetKind.Button:
                if (rule.TargetButton != ButtonId.None)
                {
                    buttons[rule.TargetButton] = combined >= rule.PressThreshold;
                }
                break;
            case MapTargetKind.StickAxisX:
            {
                var stick = GetStick(leftStick, rightStick, rule.TargetStick) with { X = Math.Clamp(combined, -1f, 1f) };
                SetStick(ref leftStick, ref rightStick, rule.TargetStick, stick);
                break;
            }
            case MapTargetKind.StickAxisY:
            {
                var stick = GetStick(leftStick, rightStick, rule.TargetStick) with { Y = Math.Clamp(combined, -1f, 1f) };
                SetStick(ref leftStick, ref rightStick, rule.TargetStick, stick);
                break;
            }
            case MapTargetKind.Trigger:
                var clamped = Math.Clamp(combined, 0f, 1f);
                if (rule.TargetTrigger == TriggerId.Left) { leftTrigger = clamped; } else { rightTrigger = clamped; }
                break;
        }
    }

    private static void SetStick(ref StickVector leftStick, ref StickVector rightStick, StickId stickId, StickVector value)
    {
        if (stickId == StickId.Left)
        {
            leftStick = value;
            return;
        }

        rightStick = value;
    }

    private StickPulseScheduler GetOrCreateStickScheduler(StickAutofireRule rule)
    {
        return stickAutofireSchedulers.TryGetValue(rule.Id, out var scheduler)
                ? scheduler
                : stickAutofireSchedulers[rule.Id] = new StickPulseScheduler(rule.Timing);
    }

    private StickPulseScheduler GetOrCreateFreezeScheduler(FreezeLastDirectionRule rule)
    {
        return freezeSchedulers.TryGetValue(rule.Id, out var scheduler)
                ? scheduler
                : freezeSchedulers[rule.Id] = new StickPulseScheduler(rule.Timing);
    }

    private BinaryPulseScheduler GetOrCreateBinaryScheduler(ButtonAutofireRule rule)
    {
        return buttonAutofireSchedulers.TryGetValue(rule.Id, out var scheduler)
                ? scheduler
                : buttonAutofireSchedulers[rule.Id] = new BinaryPulseScheduler(rule.Timing);
    }

    private FreezeLatch GetOrCreateFreezeLatch(FreezeLastDirectionRule rule)
    {
        return freezeLatches.TryGetValue(rule.Id, out var latch)
                ? latch
                : freezeLatches[rule.Id] = new FreezeLatch();
    }
}
