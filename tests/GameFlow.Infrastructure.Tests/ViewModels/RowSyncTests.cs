using System.Collections.ObjectModel;
using GameFlow.App.ViewModels;
using Xunit;

namespace GameFlow.Infrastructure.Tests.ViewModels;

/// <summary>
/// The list reconciler behind the Add/Remove device rows.
///
/// <para>
/// What matters here is what it does NOT touch. Replacing a row replaces
/// its controls, and a refresh that runs on every catalog change then
/// shows up as buttons blinking — which is the bug this exists to fix, in
/// its second appearance. So the identity tests below assert on the row
/// instances, not on their values: equal-but-new is exactly the failure.
/// </para>
/// </summary>
public sealed class RowSyncTests
{
    private sealed record Row(string Id, string Label);

    // Content comparison deliberately ignores Id — see RowSync.Apply's
    // contract. Passing record equality here is the mistake this file's
    // case-insensitivity test exists to catch.
    private static void Sync(ObservableCollection<Row> rows, params Row[] target) =>
        RowSync.Apply(rows, target, r => r.Id,
            (a, b) => string.Equals(a.Label, b.Label, StringComparison.Ordinal));

    [Fact]
    public void AnUnchangedListKeepsTheVerySameRowObjects()
    {
        var rows = new ObservableCollection<Row>([new("a", "Pad A"), new("b", "Pad B")]);
        var before = rows.ToArray();

        Sync(rows, new Row("a", "Pad A"), new Row("b", "Pad B"));

        Assert.Same(before[0], rows[0]);
        Assert.Same(before[1], rows[1]);
    }

    [Fact]
    public void AddingOneDeviceLeavesTheExistingRowsAlone()
    {
        var rows = new ObservableCollection<Row>([new("a", "Pad A")]);
        var kept = rows[0];

        Sync(rows, new Row("a", "Pad A"), new Row("b", "Pad B"));

        Assert.Same(kept, rows[0]);
        Assert.Equal("b", rows[1].Id);
    }

    [Fact]
    public void RemovingOneDeviceLeavesTheRestAlone()
    {
        var rows = new ObservableCollection<Row>([new("a", "Pad A"), new("b", "Pad B")]);
        var kept = rows[1];

        Sync(rows, new Row("b", "Pad B"));

        Assert.Single(rows);
        Assert.Same(kept, rows[0]);
    }

    [Fact]
    public void ARenamedDeviceIsReplacedSoTheNewNameShows()
    {
        var rows = new ObservableCollection<Row>([new("a", "Old name")]);

        Sync(rows, new Row("a", "New name"));

        Assert.Equal("New name", Assert.Single(rows).Label);
    }

    [Fact]
    public void ReorderingMovesRowsRatherThanRecreatingThem()
    {
        var rows = new ObservableCollection<Row>([new("a", "Pad A"), new("b", "Pad B")]);
        var a = rows[0];
        var b = rows[1];

        Sync(rows, new Row("b", "Pad B"), new Row("a", "Pad A"));

        Assert.Same(b, rows[0]);
        Assert.Same(a, rows[1]);
    }

    [Fact]
    public void AWholesaleReplacementStillEndsUpCorrect()
    {
        var rows = new ObservableCollection<Row>([new("a", "Pad A"), new("b", "Pad B")]);

        Sync(rows, new Row("c", "Pad C"), new Row("d", "Pad D"));

        Assert.Equal(["c", "d"], rows.Select(r => r.Id));
    }

    [Fact]
    public void AnEmptyTargetEmptiesTheList()
    {
        var rows = new ObservableCollection<Row>([new("a", "Pad A")]);

        Sync(rows);

        Assert.Empty(rows);
    }

    [Fact]
    public void DeviceIdsAreMatchedWithoutRegardToCase()
    {
        // Catalog ids come from several backends and do not agree on case.
        var rows = new ObservableCollection<Row>([new("PAD-A", "Pad A")]);
        var kept = rows[0];

        Sync(rows, new Row("pad-a", "Pad A"));

        Assert.Same(kept, Assert.Single(rows));
    }

    [Fact]
    public void InsertionInTheMiddleKeepsBothNeighbours()
    {
        var rows = new ObservableCollection<Row>([new("a", "A"), new("c", "C")]);
        var a = rows[0];
        var c = rows[1];

        Sync(rows, new Row("a", "A"), new Row("b", "B"), new Row("c", "C"));

        Assert.Same(a, rows[0]);
        Assert.Equal("b", rows[1].Id);
        Assert.Same(c, rows[2]);
    }
}
