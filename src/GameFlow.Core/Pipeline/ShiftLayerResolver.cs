using GameFlow.Core.Enums;
using GameFlow.Core.Models;

namespace GameFlow.Core.Pipeline;

/// <summary>
/// Resolves which single <see cref="ShiftLayer"/> (if any) is active this
/// tick, from the physical snapshot and each layer's activation mode.
/// Owned per <see cref="ControllerMappingPipeline"/> instance — layer
/// engagement state (hold timers, cycle position, latch state) belongs to
/// one slot's live session, same as every other stateful helper this
/// pipeline owns (schedulers, latches, executors).
///
/// <para>
/// At most one non-Base layer is active at a time: engaging a new one
/// always replaces whatever was active, including a different latched or
/// cycled layer — there is no stacking. This mirrors the spec's own
/// language ("press a different Latch button to SWITCH").
/// </para>
///
/// <para><b>Interpretation note (Sticky):</b> the source description names
/// the mode but doesn't fully specify its mechanics. This implements the
/// conventional "sticky keys" meaning: engage on the activator's rising
/// edge, stay active through exactly the next OTHER button's rising edge
/// (so that action gets the shifted mapping), then auto-revert to Base
/// starting the following tick. Flagged here rather than silently guessed
/// into something that reads as authoritative.
/// </para>
/// </summary>
public sealed class ShiftLayerResolver
{
    private readonly Dictionary<string, bool> wasPressed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> holdGateStartedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> cycleIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<ButtonId, bool> lastPhysicalButtons = new();

    private DateTimeOffset lastActivityAt = DateTimeOffset.MinValue;
    private bool stickyEngagedThisRise;
    private bool pendingStickyRevert;
    private bool pendingStickyRevertConsumeNow;

    /// <summary>Currently active layer id, or "" for Base.</summary>
    public string ActiveLayerId { get; private set; } = string.Empty;

    /// <summary>
    /// Advances the state machine by one tick. Returns a human-readable
    /// note ("Layer engaged: Aim · 🎯" / "Back to Base") when the active
    /// layer changed this tick, else null.
    /// </summary>
    public string? Resolve(IReadOnlyList<ShiftLayer> layers, ControllerSnapshot physical, DateTimeOffset now)
    {
        // Activity tracking for Toggle's AutoCancelMs and Sticky's
        // next-action reversion — ANY button transitioning (either
        // direction) counts as activity, not just layer activators.
        var anyOtherButtonRoseThisTick = false;
        foreach (var (id, pressed) in physical.Buttons)
        {
            var was = lastPhysicalButtons.GetValueOrDefault(id);
            if (pressed != was)
            {
                lastActivityAt = now;
                if (pressed && !was)
                {
                    anyOtherButtonRoseThisTick = true;
                }
            }
            lastPhysicalButtons[id] = pressed;
        }

        var previousActive = ActiveLayerId;
        var candidate = ActiveLayerId;

        foreach (var layer in layers)
        {
            if (layer.ActivationMode is ShiftLayerActivationMode.NoButton or ShiftLayerActivationMode.Cycle)
            {
                continue; // NoButton never self-activates; Cycle handled in its own pass below
            }
            if (layer.ActivatorButton == ButtonId.None)
            {
                continue;
            }

            var isPressed = physical.IsPressed(layer.ActivatorButton);
            var wasPressedBefore = wasPressed.GetValueOrDefault(layer.Id);

            switch (layer.ActivationMode)
            {
                case ShiftLayerActivationMode.Hold:
                    if (isPressed)
                    {
                        candidate = layer.Id;
                    }
                    else if (candidate == layer.Id)
                    {
                        candidate = string.Empty;
                    }
                    break;

                case ShiftLayerActivationMode.Toggle:
                    if (isPressed && !wasPressedBefore)
                    {
                        holdGateStartedAt[layer.Id] = now;
                    }
                    else if (!isPressed && wasPressedBefore)
                    {
                        var heldMs = holdGateStartedAt.TryGetValue(layer.Id, out var startedAt)
                            ? (now - startedAt).TotalMilliseconds
                            : 0;
                        if (heldMs >= layer.HoldToFireMs)
                        {
                            candidate = candidate == layer.Id ? string.Empty : layer.Id;
                        }
                        // else: a tap shorter than the hold gate — not
                        // consumed as a layer gesture. The button's own
                        // press/release already flowed through `buttons`
                        // untouched by this resolver, so any Base rule
                        // bound to it still fires normally ("does its
                        // normal job").
                    }
                    break;

                case ShiftLayerActivationMode.Latch:
                    if (isPressed && !wasPressedBefore)
                    {
                        candidate = candidate == layer.Id ? string.Empty : layer.Id;
                    }
                    break;

                case ShiftLayerActivationMode.Sticky:
                    if (isPressed && !wasPressedBefore && candidate != layer.Id)
                    {
                        candidate = layer.Id;
                        stickyEngagedThisRise = true; // guard: this tick's own rising edge doesn't immediately cancel itself below
                    }
                    break;
            }

            wasPressed[layer.Id] = isPressed;
        }

        // Cycle: separate pass — steps through OTHER layers rather than
        // gating a layer of its own.
        foreach (var layer in layers)
        {
            if (layer.ActivationMode != ShiftLayerActivationMode.Cycle)
            {
                continue;
            }

            var stops = BuildCycleStops(layer);
            if (stops.Count == 0)
            {
                continue;
            }

            var forwardKey = layer.Id + ":fwd";
            var backKey = layer.Id + ":back";

            var forwardPressed = layer.ActivatorButton != ButtonId.None && physical.IsPressed(layer.ActivatorButton);
            var forwardWas = wasPressed.GetValueOrDefault(forwardKey);
            if (forwardPressed && !forwardWas)
            {
                // Starting index: when Base is in the rotation we are
                // ALREADY sitting on stops[0], so the first press must
                // advance to stops[1]. Defaulting to -1 there made the
                // first press "move" to where you already were, which
                // looked like the cycle button doing nothing at all.
                // Without Base, -1 is correct: first press lands on stops[0].
                var restingIndex = layer.CycleIncludeBase ? 0 : -1;
                var idx = StepForward(cycleIndex.GetValueOrDefault(layer.Id, restingIndex), stops.Count, layer.CycleWrapAround);
                cycleIndex[layer.Id] = idx;
                candidate = stops[idx];
            }
            wasPressed[forwardKey] = forwardPressed;

            var backPressed = layer.CyclePreviousButton != ButtonId.None && physical.IsPressed(layer.CyclePreviousButton);
            var backWas = wasPressed.GetValueOrDefault(backKey);
            if (backPressed && !backWas)
            {
                var backRestingIndex = layer.CycleIncludeBase ? 0 : -1;
                var idx = StepBackward(cycleIndex.GetValueOrDefault(layer.Id, backRestingIndex), stops.Count, layer.CycleWrapAround);
                cycleIndex[layer.Id] = idx;
                candidate = stops[idx];
            }
            wasPressed[backKey] = backPressed;
        }

        // Toggle auto-cancel: idle timeout drops back to Base.
        var activeLayer = layers.FirstOrDefault(l => l.Id == candidate);
        if (activeLayer is { ActivationMode: ShiftLayerActivationMode.Toggle, AutoCancelMs: > 0 }
            && (now - lastActivityAt).TotalMilliseconds >= activeLayer.AutoCancelMs)
        {
            candidate = string.Empty;
        }

        // Sticky reversion: any OTHER button's rising edge, on a tick
        // AFTER the one that engaged it, drops back to Base — but only
        // after this tick's rules have had a chance to run against the
        // still-active layer ("stays active for exactly the next action").
        if (activeLayer?.ActivationMode == ShiftLayerActivationMode.Sticky)
        {
            if (anyOtherButtonRoseThisTick && !stickyEngagedThisRise)
            {
                // Defer the actual drop to the START of the NEXT call —
                // this tick still reports the sticky layer as active.
                pendingStickyRevert = true;
            }
        }
        else
        {
            pendingStickyRevert = false;
        }

        if (pendingStickyRevertConsumeNow)
        {
            candidate = string.Empty;
            pendingStickyRevertConsumeNow = false;
        }
        if (pendingStickyRevert && !stickyEngagedThisRise)
        {
            pendingStickyRevertConsumeNow = true;
            pendingStickyRevert = false;
        }
        stickyEngagedThisRise = false;

        ActiveLayerId = candidate;

        if (candidate == previousActive)
        {
            return null;
        }

        if (string.IsNullOrEmpty(candidate))
        {
            return "Shift layer: back to Base.";
        }

        var engaged = layers.FirstOrDefault(l => l.Id == candidate);
        return engaged is null
            ? null
            : $"Shift layer engaged: {engaged.Name} {engaged.Emoji}".TrimEnd();
    }

    /// <summary>Cycle's ordered stop list — Base ("") first when <see cref="ShiftLayer.CycleIncludeBase"/> is set, then the configured queue.</summary>
    private static List<string> BuildCycleStops(ShiftLayer cycleLayer)
    {
        var stops = new List<string>();
        if (cycleLayer.CycleIncludeBase)
        {
            stops.Add(string.Empty);
        }
        stops.AddRange(cycleLayer.CycleLayerIds);
        return stops;
    }

    private static int StepForward(int index, int count, bool wrap)
    {
        if (index < 0)
        {
            return 0;
        }
        var next = index + 1;
        if (next >= count)
        {
            return wrap ? 0 : count - 1;
        }
        return next;
    }

    private static int StepBackward(int index, int count, bool wrap)
    {
        if (index < 0)
        {
            return count - 1;
        }
        var prev = index - 1;
        if (prev < 0)
        {
            return wrap ? count - 1 : 0;
        }
        return prev;
    }
}
