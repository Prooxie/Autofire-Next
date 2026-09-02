namespace GameFlow.Infrastructure.Runtime.Input;

/// <summary>
/// Removes the phantom Left Control that Windows injects in front of
/// every AltGr press, and keeps the pressed-key set for one keyboard.
/// </summary>
/// <remarks>
/// <para>
/// On every layout with an AltGr key — Czech, German, Polish, and most
/// other non-US layouts — pressing Right Alt makes Windows synthesise a
/// Left Control press immediately before it, for backwards compatibility
/// with applications that read AltGr as Ctrl+Alt. Raw Input faithfully
/// reports both, so the on-screen keyboard lit Control as well as Alt,
/// and any mapping bound to Control fired on its own whenever the user
/// typed a character that needs AltGr.
/// </para>
/// <para>
/// There is no flag in <c>RAWKEYBOARD</c> that marks the injected press —
/// it is byte-for-byte a real Left Control — so it is identified by the
/// only thing that distinguishes it: it arrives immediately before a
/// Right Alt press. Once identified it stays suppressed for as long as
/// Right Alt is held, which also absorbs key auto-repeat.
/// </para>
/// <para>
/// One instance per keyboard, which is the right scope: a second
/// keyboard on a US layout sends no phantom press, and its Control must
/// go on working while AltGr is held on the first.
/// </para>
/// <para>
/// The clock is injectable so the pairing window can be exercised
/// deterministically in tests; nothing in production passes anything but
/// the default.
/// </para>
/// </remarks>
internal sealed class AltGrGhostFilter(Func<long>? timestampTicks = null)
{
    /// <summary>Left Control, after <see cref="WindowsRawKeyNormalizer"/> has resolved the side.</summary>
    internal const int LeftControl = 0xA2;

    /// <summary>Right Alt — the AltGr key on layouts that have one.</summary>
    internal const int RightAlt = 0xA5;

    /// <summary>
    /// How close the Control press has to be to the Right Alt press to
    /// be treated as the injected one.
    /// </summary>
    /// <remarks>
    /// Windows emits the pair back to back inside a single input burst,
    /// so the real gap is effectively zero and any small window works. It
    /// is not zero-tolerance only because the two packets are read on
    /// separate iterations of the message loop. Deliberately short: a
    /// human genuinely rolling from Ctrl onto AltGr takes far longer than
    /// this, and would otherwise lose their Ctrl.
    /// </remarks>
    internal static readonly TimeSpan PairingWindow = TimeSpan.FromMilliseconds(40);

    private readonly Func<long> clock = timestampTicks ?? (() => DateTime.UtcNow.Ticks);
    private readonly object gate = new();
    private readonly HashSet<int> pressed = [];

    private long leftControlDownTimestamp;
    private bool leftControlIsGhost;

    /// <summary>Applies one key transition to the pressed-key set.</summary>
    /// <param name="virtualKey">Normalized virtual key.</param>
    /// <param name="keyUp"><see langword="true"/> for a release.</param>
    public void Apply(int virtualKey, bool keyUp)
    {
        lock (gate)
        {
            if (keyUp)
            {
                pressed.Remove(virtualKey);

                // Releasing AltGr ends the suppression. Windows also sends
                // a phantom Control release here, which the Remove above
                // has already absorbed as a no-op.
                if (virtualKey == RightAlt || (virtualKey == LeftControl && !pressed.Contains(RightAlt)))
                {
                    leftControlIsGhost = false;
                }

                return;
            }

            switch (virtualKey)
            {
                case LeftControl:
                    // Auto-repeat while AltGr is held keeps re-sending the
                    // phantom press; swallow those too.
                    if (leftControlIsGhost && pressed.Contains(RightAlt))
                    {
                        return;
                    }

                    leftControlDownTimestamp = clock();
                    break;

                case RightAlt when pressed.Contains(LeftControl) &&
                                   clock() - leftControlDownTimestamp <= PairingWindow.Ticks:
                    pressed.Remove(LeftControl);
                    leftControlIsGhost = true;
                    break;
            }

            pressed.Add(virtualKey);
        }
    }

    /// <summary>A point-in-time copy of the pressed keys.</summary>
    public IReadOnlySet<int> Snapshot()
    {
        lock (gate) { return new HashSet<int>(pressed); }
    }
}
