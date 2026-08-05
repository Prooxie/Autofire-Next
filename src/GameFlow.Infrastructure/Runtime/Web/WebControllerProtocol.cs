using System.Text.Json;
using GameFlow.Core.Enums;
using GameFlow.Core.Models;

namespace GameFlow.Infrastructure.Runtime.Web;

/// <summary>
/// Wire format between the browser gamepad and the runtime.
///
/// <para>
/// <b>The bit order below is a fixed contract</b>, duplicated in the
/// page's JavaScript (see the BIT table there). It is deliberately NOT
/// derived from <see cref="ButtonId"/>'s declaration order: that enum
/// can be reordered during ordinary refactoring, and if it were the
/// wire contract, doing so would silently make every connected phone
/// report the wrong buttons — a bug with no compile error and no
/// obvious cause. Changing anything here means changing the JavaScript
/// in the same commit.
/// </para>
/// </summary>
public static class WebControllerProtocol
{
    private const int MaxTouchContacts = 5;

    /// <summary>Bit index → button. Index IS the wire bit position.</summary>
    private static readonly ButtonId[] BitOrder =
    [
        ButtonId.South,          // 0
        ButtonId.East,           // 1
        ButtonId.West,           // 2
        ButtonId.North,          // 3
        ButtonId.LeftShoulder,   // 4
        ButtonId.RightShoulder,  // 5
        ButtonId.Back,           // 6
        ButtonId.Start,          // 7
        ButtonId.Guide,          // 8
        ButtonId.LeftStick,      // 9
        ButtonId.RightStick,     // 10
        ButtonId.DpadUp,         // 11
        ButtonId.DpadDown,       // 12
        ButtonId.DpadLeft,       // 13
        ButtonId.DpadRight,      // 14
        ButtonId.Touchpad        // 15
    ];

    /// <summary>
    /// Parses one input frame. Returns null on malformed JSON rather
    /// than throwing — the input is a socket message from a phone on
    /// the network, so a garbled or hostile frame must not be able to
    /// take down the receive loop.
    /// </summary>
    public static ControllerSnapshot? TryParseInput(string json, int padIndex)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var buttonMask = ReadInt(root, "b");
            var buttons = ButtonState.Clone(ButtonState.CreateEmptyMap());
            for (var bit = 0; bit < BitOrder.Length; bit++)
            {
                if ((buttonMask & (1 << bit)) != 0)
                {
                    buttons[BitOrder[bit]] = true;
                }
            }

            var snapshot = new ControllerSnapshot
            {
                DeviceName = $"Web Controller #{padIndex + 1}",
                Buttons = buttons,
                LeftStick = new StickVector(ReadAxis(root, "lx"), ReadAxis(root, "ly")),
                RightStick = new StickVector(ReadAxis(root, "rx"), ReadAxis(root, "ry")),
                LeftTrigger = ReadUnit(root, "lt"),
                RightTrigger = ReadUnit(root, "rt"),

                // Phone motion. The browser reports rotation in degrees/s
                // and the page converts to radians/s before sending, so
                // these arrive already in SDL's units — meaning a phone
                // drives GyroMapRule (reference frames, smoothing, Aim
                // Engage) exactly like a DualSense, with no phone-specific
                // path anywhere downstream.
                HasGyro = ReadInt(root, "gyro") != 0,
                GyroPitch = ReadSigned(root, "gp"),
                GyroYaw = ReadSigned(root, "gy"),
                GyroRoll = ReadSigned(root, "gr"),
                AccelX = ReadSigned(root, "ax"),
                AccelY = ReadSigned(root, "ay"),
                AccelZ = ReadSigned(root, "az"),

                Timestamp = DateTimeOffset.UtcNow
            };

            var touchContacts = ReadTouchContacts(root);
            return touchContacts.Count == 0
                ? snapshot
                : snapshot.WithTouchContacts(touchContacts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serializes the pad-assignment handshake the browser shows as "PAD #n". -1 means every slot is taken.</summary>
    public static string BuildPadAssignment(int padIndex) =>
        JsonSerializer.Serialize(new { pad = padIndex });

    /// <summary>Serializes a rumble command for the browser's Vibration API.</summary>
    public static string BuildRumble(WebRumbleCommand command) =>
        JsonSerializer.Serialize(new
        {
            rumble = new
            {
                low = command.LowFrequency,
                high = command.HighFrequency,
                ms = command.DurationMs
            }
        });

    // The ValueKind check is load-bearing, not belt-and-braces: JsonElement's
    // TryGet* methods THROW on a wrong-typed element rather than returning
    // false, so {"b":"7"} from a hostile phone would escape TryParseInput's
    // JsonException catch and kill the receive loop.
    private static int ReadInt(JsonElement root, string name) =>
        TryReadInt(root, name, out var value) ? value : 0;

    private static bool TryReadInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    /// <summary>Signed stick axis, clamped — a phone could send anything, including NaN.</summary>
    private static float ReadAxis(JsonElement root, string name) => ClampFinite(ReadFloat(root, name), -1f, 1f);

    /// <summary>Unsigned trigger, clamped.</summary>
    private static float ReadUnit(JsonElement root, string name) => ClampFinite(ReadFloat(root, name), 0f, 1f);

    /// <summary>Non-Number elements read as 0 — see the note on <see cref="ReadInt"/>.</summary>
    private static float ReadFloat(JsonElement root, string name) =>
        TryReadFloat(root, name, out var value) ? value : 0f;

    private static bool TryReadFloat(JsonElement root, string name, out float value)
    {
        value = 0f;
        if (!root.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out var parsed))
        {
            return false;
        }

        value = (float)parsed;
        return float.IsFinite(value);
    }

    /// <summary>
    /// Reads the browser touchpad's live contacts. Five is both the
    /// product limit and an important network-input bound: a hostile
    /// client cannot make one 60 Hz frame allocate an arbitrary list.
    /// Malformed contacts are skipped individually so one bad finger
    /// does not discard the rest of the frame.
    /// </summary>
    private static IReadOnlyList<TouchContact> ReadTouchContacts(JsonElement root)
    {
        if (!root.TryGetProperty("touch", out var touch)
            || touch.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var contacts = new List<TouchContact>(Math.Min(touch.GetArrayLength(), MaxTouchContacts));
        foreach (var item in touch.EnumerateArray())
        {
            if (contacts.Count == MaxTouchContacts)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.Object
                || !TryReadInt(item, "i", out var fingerIndex)
                || fingerIndex < 0
                || ContainsFinger(contacts, fingerIndex)
                || !TryReadFloat(item, "x", out var x)
                || !TryReadFloat(item, "y", out var y))
            {
                continue;
            }

            var pressure = TryReadFloat(item, "p", out var reportedPressure)
                ? ClampFinite(reportedPressure, 0f, 1f)
                : 1f;
            contacts.Add(new TouchContact(
                fingerIndex,
                ClampFinite(x, 0f, 1f),
                ClampFinite(y, 0f, 1f),
                pressure));
        }

        return contacts;
    }

    private static bool ContainsFinger(List<TouchContact> contacts, int fingerIndex)
    {
        for (var i = 0; i < contacts.Count; i++)
        {
            if (contacts[i].FingerIndex == fingerIndex)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Signed, UNBOUNDED value for motion (rad/s, m/s²). Unlike sticks
    /// and triggers these have no natural clamp range — a fast flick
    /// legitimately exceeds any fixed bound, and clamping would silently
    /// cap it. Still rejects NaN/infinity, which would otherwise poison
    /// every downstream calculation.
    /// </summary>
    private static float ReadSigned(JsonElement root, string name)
    {
        var value = ReadFloat(root, name);
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return 0f;
        }
        // Sanity ceiling only — far beyond any real hand movement, so it
        // never truncates genuine input, but stops a hostile client
        // sending 1e30 and overflowing the maths downstream.
        return Math.Clamp(value, -1000f, 1000f);
    }

    private static float ClampFinite(float value, float min, float max)
    {
        // NaN fails every comparison, so it would slip past a plain
        // Math.Clamp and poison the stick maths downstream.
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return 0f;
        }
        return Math.Clamp(value, min, max);
    }
}
