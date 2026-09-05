using GameFlow.Core.Enums;
using GameFlow.Core.Models;

namespace GameFlow.Infrastructure.Runtime.Input;

/// <summary>
/// Pure transform: given the set of Windows virtual-key codes currently
/// down on a keyboard, produce the equivalent <see cref="ControllerSnapshot"/>
/// per the fixed default layout. Sticks accumulate from key directions and
/// clamp to unit magnitude (diagonals don't exceed 1); triggers are
/// digital (1.0 when the bound key is down).
/// </summary>
public static class KeyboardGamepadSynthesizer
{
    public static ControllerSnapshot Synthesize(string deviceName, IReadOnlySet<int> pressedVirtualKeys)
    {
        float lx = 0, ly = 0, rx = 0, ry = 0, lt = 0, rt = 0;
        var buttons = ButtonMask.Empty;

        foreach (var vk in pressedVirtualKeys)
        {
            if (!KeyboardGamepadLayout.Default.TryGetValue(vk, out var action))
            {
                continue;
            }
            switch (action)
            {
                case GamepadAction.LeftStickUp:    ly += 1f; break;
                case GamepadAction.LeftStickDown:  ly -= 1f; break;
                case GamepadAction.LeftStickLeft:  lx -= 1f; break;
                case GamepadAction.LeftStickRight: lx += 1f; break;
                case GamepadAction.RightStickUp:    ry += 1f; break;
                case GamepadAction.RightStickDown:  ry -= 1f; break;
                case GamepadAction.RightStickLeft:  rx -= 1f; break;
                case GamepadAction.RightStickRight: rx += 1f; break;
                case GamepadAction.LeftTriggerFull:  lt = 1f; break;
                case GamepadAction.RightTriggerFull: rt = 1f; break;
                case GamepadAction.South:         buttons[ButtonId.South] = true; break;
                case GamepadAction.East:          buttons[ButtonId.East] = true; break;
                case GamepadAction.West:          buttons[ButtonId.West] = true; break;
                case GamepadAction.North:         buttons[ButtonId.North] = true; break;
                case GamepadAction.LeftShoulder:  buttons[ButtonId.LeftShoulder] = true; break;
                case GamepadAction.RightShoulder: buttons[ButtonId.RightShoulder] = true; break;
                case GamepadAction.Back:          buttons[ButtonId.Back] = true; break;
                case GamepadAction.Start:         buttons[ButtonId.Start] = true; break;
                case GamepadAction.Guide:         buttons[ButtonId.Guide] = true; break;
                case GamepadAction.DpadUp:        buttons[ButtonId.DpadUp] = true; break;
                case GamepadAction.DpadDown:      buttons[ButtonId.DpadDown] = true; break;
                case GamepadAction.DpadLeft:      buttons[ButtonId.DpadLeft] = true; break;
                case GamepadAction.DpadRight:     buttons[ButtonId.DpadRight] = true; break;
            }
        }

        var left = ClampToUnit(lx, ly);
        var right = ClampToUnit(rx, ry);

        return new ControllerSnapshot
        {
            DeviceName  = deviceName,
            LeftStick   = left,
            RightStick  = right,
            LeftTrigger = lt,
            RightTrigger = rt,
            Buttons     = buttons,
        };
    }

    private static StickVector ClampToUnit(float x, float y)
    {
        var magnitude = MathF.Sqrt(x * x + y * y);
        return magnitude > 1f
            ? new StickVector(x / magnitude, y / magnitude)
            : new StickVector(x, y);
    }
}
