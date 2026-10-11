using Avalonia.Input;

namespace MRemoteMac.Rdp;

/// <summary>Maps Avalonia physical keys (USB HID / W3C codes) to RDP scancodes (set 1, FreeRDP encoding: code | 0x100 if extended).</summary>
public static class ScanCodes
{
    private const uint E = RdpInput.Extended;

    public const uint LeftControl = 0x1D, RightControl = 0x1D | E, LeftAlt = 0x38, RightAlt = 0x38 | E;
    public const uint LeftShift = 0x2A, RightShift = 0x36, LeftWin = 0x5B | E, RightWin = 0x5C | E;
    public const uint Delete = 0x53 | E, Escape = 0x01, Tab = 0x0F;

    private static readonly Dictionary<PhysicalKey, uint> Map = new()
    {
        [PhysicalKey.A] = 0x1E, [PhysicalKey.B] = 0x30, [PhysicalKey.C] = 0x2E, [PhysicalKey.D] = 0x20,
        [PhysicalKey.E] = 0x12, [PhysicalKey.F] = 0x21, [PhysicalKey.G] = 0x22, [PhysicalKey.H] = 0x23,
        [PhysicalKey.I] = 0x17, [PhysicalKey.J] = 0x24, [PhysicalKey.K] = 0x25, [PhysicalKey.L] = 0x26,
        [PhysicalKey.M] = 0x32, [PhysicalKey.N] = 0x31, [PhysicalKey.O] = 0x18, [PhysicalKey.P] = 0x19,
        [PhysicalKey.Q] = 0x10, [PhysicalKey.R] = 0x13, [PhysicalKey.S] = 0x1F, [PhysicalKey.T] = 0x14,
        [PhysicalKey.U] = 0x16, [PhysicalKey.V] = 0x2F, [PhysicalKey.W] = 0x11, [PhysicalKey.X] = 0x2D,
        [PhysicalKey.Y] = 0x15, [PhysicalKey.Z] = 0x2C,
        [PhysicalKey.Digit1] = 0x02, [PhysicalKey.Digit2] = 0x03, [PhysicalKey.Digit3] = 0x04, [PhysicalKey.Digit4] = 0x05,
        [PhysicalKey.Digit5] = 0x06, [PhysicalKey.Digit6] = 0x07, [PhysicalKey.Digit7] = 0x08, [PhysicalKey.Digit8] = 0x09,
        [PhysicalKey.Digit9] = 0x0A, [PhysicalKey.Digit0] = 0x0B,
        [PhysicalKey.Backquote] = 0x29, [PhysicalKey.Minus] = 0x0C, [PhysicalKey.Equal] = 0x0D,
        [PhysicalKey.Backspace] = 0x0E, [PhysicalKey.Tab] = 0x0F, [PhysicalKey.BracketLeft] = 0x1A,
        [PhysicalKey.BracketRight] = 0x1B, [PhysicalKey.Enter] = 0x1C, [PhysicalKey.CapsLock] = 0x3A,
        [PhysicalKey.Semicolon] = 0x27, [PhysicalKey.Quote] = 0x28, [PhysicalKey.Backslash] = 0x2B,
        [PhysicalKey.ShiftLeft] = 0x2A, [PhysicalKey.IntlBackslash] = 0x56, [PhysicalKey.Comma] = 0x33,
        [PhysicalKey.Period] = 0x34, [PhysicalKey.Slash] = 0x35, [PhysicalKey.ShiftRight] = 0x36,
        [PhysicalKey.ControlLeft] = 0x1D, [PhysicalKey.AltLeft] = 0x38, [PhysicalKey.Space] = 0x39,
        [PhysicalKey.AltRight] = 0x38 | E, [PhysicalKey.ControlRight] = 0x1D | E,
        [PhysicalKey.MetaLeft] = 0x5B | E, [PhysicalKey.MetaRight] = 0x5C | E, [PhysicalKey.ContextMenu] = 0x5D | E,
        [PhysicalKey.Escape] = 0x01,
        [PhysicalKey.F1] = 0x3B, [PhysicalKey.F2] = 0x3C, [PhysicalKey.F3] = 0x3D, [PhysicalKey.F4] = 0x3E,
        [PhysicalKey.F5] = 0x3F, [PhysicalKey.F6] = 0x40, [PhysicalKey.F7] = 0x41, [PhysicalKey.F8] = 0x42,
        [PhysicalKey.F9] = 0x43, [PhysicalKey.F10] = 0x44, [PhysicalKey.F11] = 0x57, [PhysicalKey.F12] = 0x58,
        [PhysicalKey.F13] = 0x64, [PhysicalKey.F14] = 0x65, [PhysicalKey.F15] = 0x66, [PhysicalKey.F16] = 0x67,
        [PhysicalKey.F17] = 0x68, [PhysicalKey.F18] = 0x69, [PhysicalKey.F19] = 0x6A, [PhysicalKey.F20] = 0x6B,
        [PhysicalKey.PrintScreen] = 0x37 | E, [PhysicalKey.ScrollLock] = 0x46, [PhysicalKey.Pause] = 0x45 | E,
        [PhysicalKey.Insert] = 0x52 | E, [PhysicalKey.Home] = 0x47 | E, [PhysicalKey.PageUp] = 0x49 | E,
        [PhysicalKey.Delete] = 0x53 | E, [PhysicalKey.End] = 0x4F | E, [PhysicalKey.PageDown] = 0x51 | E,
        [PhysicalKey.ArrowUp] = 0x48 | E, [PhysicalKey.ArrowLeft] = 0x4B | E, [PhysicalKey.ArrowDown] = 0x50 | E,
        [PhysicalKey.ArrowRight] = 0x4D | E,
        [PhysicalKey.NumLock] = 0x45, [PhysicalKey.NumPadDivide] = 0x35 | E, [PhysicalKey.NumPadMultiply] = 0x37,
        [PhysicalKey.NumPadSubtract] = 0x4A, [PhysicalKey.NumPadAdd] = 0x4E, [PhysicalKey.NumPadEnter] = 0x1C | E,
        [PhysicalKey.NumPad1] = 0x4F, [PhysicalKey.NumPad2] = 0x50, [PhysicalKey.NumPad3] = 0x51,
        [PhysicalKey.NumPad4] = 0x4B, [PhysicalKey.NumPad5] = 0x4C, [PhysicalKey.NumPad6] = 0x4D,
        [PhysicalKey.NumPad7] = 0x47, [PhysicalKey.NumPad8] = 0x48, [PhysicalKey.NumPad9] = 0x49,
        [PhysicalKey.NumPad0] = 0x52, [PhysicalKey.NumPadDecimal] = 0x53, [PhysicalKey.NumPadEqual] = 0x59,
        [PhysicalKey.IntlRo] = 0x73, [PhysicalKey.IntlYen] = 0x7D, [PhysicalKey.Convert] = 0x79,
        [PhysicalKey.NonConvert] = 0x7B, [PhysicalKey.KanaMode] = 0x70,
    };

    public static bool TryGet(PhysicalKey key, out uint scancode) => Map.TryGetValue(key, out scancode);
}
