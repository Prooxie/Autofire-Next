using System.Runtime.InteropServices;

namespace GameFlow.Infrastructure.Runtime.Input.Mac;

/// <summary>
/// Raw P/Invoke surface for macOS mouse SYNTHESIS (CGEventPost) via Core
/// Graphics.
///
/// <para>
/// <b>Reading no longer lives here.</b> This file used to carry a whole
/// CGEventTap capture surface as well — tap creation, the CGEventField
/// enum, run loop plumbing. Input moved to <see cref="MacHidInterop"/>
/// because IOHIDManager reports which device an event came from and
/// CGEventTap cannot. What is left is only what
/// <see cref="MacMouseOutputWriter"/> needs, and none of it has a
/// per-device dimension to lose: synthesis puts one event into the
/// system, and "which keyboard typed it" is not a question a synthetic
/// event answers.
/// </para>
///
/// <para>
/// The two constants that were flagged as least trustworthy —
/// kCGKeyboardEventKeycode and kCGMouseEventButtonNumber, both guesses at
/// a CGEventField ordering recalled rather than read from a header — went
/// with the read path. Nothing remaining depends on that enum at all.
/// </para>
///
/// <para>
/// <b>Still unverified.</b> Every value below comes from trained
/// knowledge of Core Graphics Event Services, a stable API unchanged
/// since Mac OS X 10.4, with no macOS SDK, Apple headers or Apple
/// toolchain anywhere in this build environment to check it against.
/// That is categorically weaker than EvdevInterop.cs and UinputInterop.cs,
/// where a real C compiler printed every struct layout and ioctl number
/// from this machine's own kernel headers. The function signatures and
/// CGEventType values here are the best-documented part of that surface,
/// which is some comfort but not verification.
/// </para>
/// </summary>
internal static partial class MacEventInterop
{
    /// <summary>kCGHIDEventTap — post at the lowest level, where the HID system itself injects. Higher confidence: a small, widely documented enum.</summary>
    internal const int kCGHIDEventTap = 0;

    /// <summary>kCGEventMouseMoved. Higher confidence: heavily cited, stable CGEventType value.</summary>
    internal const uint kCGEventMouseMoved = 5;

    // ─── CGPoint — matches Apple's public CGGeometry.h layout (two
    // doubles) — this specific struct is about as stable/public as any
    // Apple type gets. ───
    [StructLayout(LayoutKind.Sequential)]
    internal struct CGPoint
    {
        public double X;
        public double Y;

        public CGPoint(double x, double y) { X = x; Y = y; }
    }

    // ─── Core Graphics ───

    /// <summary>Used only as a probe: a null-source event carries the current cursor location, which is how the writer finds its starting position.</summary>
    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    internal static partial IntPtr CGEventCreate(IntPtr source);

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    internal static partial CGPoint CGEventGetLocation(IntPtr eventRef);

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    internal static partial IntPtr CGEventCreateMouseEvent(
        IntPtr source, uint mouseType, CGPoint mouseCursorPosition, long mouseButton);

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    internal static partial void CGEventPost(int tap, IntPtr eventRef);

    // ─── Core Foundation ───

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    internal static partial void CFRelease(IntPtr cfObject);
}
