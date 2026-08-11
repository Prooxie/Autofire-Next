using GameFlow.Infrastructure.Runtime.Input;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Input;

public sealed class KeyboardStateSourceTests
{
    [Fact]
    public void Selected_device_state_is_preferred_when_it_has_keys()
    {
        var deviceKeys = new HashSet<int> { 0x41 };
        var aggregateKeys = new HashSet<int> { 0x42 };
        var source = new StubKeyboardStateSource(deviceKeys, aggregateKeys);

        var pressed = source.GetPressedKeysWithAggregateFallback("keyboard-1");

        Assert.Same(deviceKeys, pressed);
        Assert.Equal(0, source.AggregateReads);
    }

    [Fact]
    public void Aggregate_state_is_used_when_selected_native_handle_is_empty()
    {
        var aggregateKeys = new HashSet<int> { 0x42 };
        var source = new StubKeyboardStateSource(new HashSet<int>(), aggregateKeys);

        var pressed = source.GetPressedKeysWithAggregateFallback("keyboard-1");

        Assert.Same(aggregateKeys, pressed);
        Assert.Equal(1, source.AggregateReads);
    }

    private sealed class StubKeyboardStateSource(
        IReadOnlySet<int> deviceKeys,
        IReadOnlySet<int> aggregateKeys) : IKeyboardStateSource
    {
        public int AggregateReads { get; private set; }

        public IReadOnlySet<int> GetPressedKeys(string deviceId) => deviceKeys;

        public IReadOnlySet<int> GetPressedKeysAggregate()
        {
            AggregateReads++;
            return aggregateKeys;
        }
    }
}
