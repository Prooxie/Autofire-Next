using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Core.Models.Rules;
using GameFlow.Core.Pipeline;
using Xunit;

namespace GameFlow.Core.Tests;

/// <summary>
/// <see cref="ControllerFrameResult.Notes"/> is a diagnostic strip that only
/// ever displays the latest frame, but it is produced by a method that runs
/// up to 1000 times a second per slot. Two allocations were removed from that
/// path: the notes list is now created on demand, and each remap rule's note
/// text is formatted once when the pipeline is built rather than on every
/// tick the rule fires.
///
/// <para>
/// These tests hold the observable behaviour still across that change — the
/// notes a caller actually reads must be identical to what the per-tick
/// formatting produced.
/// </para>
/// </summary>
public sealed class ControllerMappingPipelineNotesTests
{
    private static ControllerSnapshot SnapshotWith(params ButtonId[] pressed)
    {
        var buttons = ButtonState.Clone(ButtonState.CreateEmptyMap());
        foreach (var button in pressed)
        {
            buttons[button] = true;
        }

        return new ControllerSnapshot { Buttons = buttons };
    }

    [Fact]
    public void A_quiet_frame_reports_no_notes()
    {
        using var pipeline = new ControllerMappingPipeline(new ProfileDocument());

        var result = pipeline.Process(SnapshotWith(), DateTimeOffset.UtcNow);

        Assert.Empty(result.Notes);
    }

    [Fact]
    public void A_firing_remap_reports_its_source_and_target()
    {
        var profile = new ProfileDocument
        {
            Rules =
            [
                new ButtonRemapRule
                {
                    Id = "remap",
                    SourceButton = ButtonId.South,
                    TargetButton = ButtonId.North
                }
            ]
        };

        using var pipeline = new ControllerMappingPipeline(profile);

        // Not pressed: the rule contributes nothing at all.
        Assert.Empty(pipeline.Process(SnapshotWith(), DateTimeOffset.UtcNow).Notes);

        var result = pipeline.Process(SnapshotWith(ButtonId.South), DateTimeOffset.UtcNow);

        Assert.Equal("Remapped South -> North.", Assert.Single(result.Notes));
        Assert.True(result.VirtualSnapshot.IsPressed(ButtonId.North));
    }

    [Fact]
    public void A_blocking_remap_reports_the_button_it_blocked()
    {
        var profile = new ProfileDocument
        {
            Rules =
            [
                new ButtonRemapRule
                {
                    Id = "blocked",
                    Mode = RuleMode.DoNothing,
                    SourceButton = ButtonId.Guide,
                    TargetButton = ButtonId.North
                }
            ]
        };

        using var pipeline = new ControllerMappingPipeline(profile);

        var result = pipeline.Process(SnapshotWith(ButtonId.Guide), DateTimeOffset.UtcNow);

        Assert.Equal("Blocked Guide.", Assert.Single(result.Notes));
        Assert.False(result.VirtualSnapshot.IsPressed(ButtonId.Guide));
    }

    [Fact]
    public void Each_remap_reports_its_own_note_when_several_fire_together()
    {
        // The note text is now looked up by the rule's position in the
        // pipeline's rule array, so a profile with several remaps is the
        // case that would expose an off-by-one in that indexing.
        var profile = new ProfileDocument
        {
            Rules =
            [
                new ButtonRemapRule { Id = "a", SourceButton = ButtonId.South, TargetButton = ButtonId.North },
                new ButtonRemapRule { Id = "b", Mode = RuleMode.DoNothing, SourceButton = ButtonId.Back, TargetButton = ButtonId.West },
                new ButtonRemapRule { Id = "c", SourceButton = ButtonId.East, TargetButton = ButtonId.LeftShoulder }
            ]
        };

        using var pipeline = new ControllerMappingPipeline(profile);

        var result = pipeline.Process(SnapshotWith(ButtonId.South, ButtonId.East), DateTimeOffset.UtcNow);

        Assert.Equal(
            ["Remapped South -> North.", "Blocked Back.", "Remapped East -> LeftShoulder."],
            result.Notes);
    }

    [Fact]
    public void A_held_remap_reports_the_same_note_instance_every_tick()
    {
        // The note is formatted once at construction; re-formatting it per
        // tick is the allocation this replaced, so sharing the instance is
        // the property worth pinning.
        var profile = new ProfileDocument
        {
            Rules =
            [
                new ButtonRemapRule { Id = "held", SourceButton = ButtonId.South, TargetButton = ButtonId.North }
            ]
        };

        using var pipeline = new ControllerMappingPipeline(profile);
        var now = DateTimeOffset.UtcNow;

        var first = pipeline.Process(SnapshotWith(ButtonId.South), now);
        var second = pipeline.Process(SnapshotWith(ButtonId.South), now.AddMilliseconds(1));

        Assert.Same(first.Notes[0], second.Notes[0]);
    }
}
