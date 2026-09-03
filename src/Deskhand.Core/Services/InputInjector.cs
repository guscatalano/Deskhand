using System.Runtime.InteropServices;
using Deskhand.Core.Interop;
using static Deskhand.Core.Interop.NativeMethods;

namespace Deskhand.Core.Services;

/// <summary>
/// Synthetic mouse and keyboard input via SendInput. Coordinates are physical pixels
/// on the virtual desktop (the process is Per-Monitor-v2 DPI aware), normalized to the
/// 0..65535 absolute space with MOUSEEVENTF_VIRTUALDESK so secondary monitors work.
/// </summary>
public static class InputInjector
{
    private static void Send(params INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            int err = Marshal.GetLastWin32Error();
            // Error 5 (access denied) is the classic "input was blocked" — e.g. a
            // higher-integrity foreground window (UIPI) or the secure desktop.
            throw new DesktopUnavailableException(
                $"SendInput injected {sent}/{inputs.Length} events (Win32 error {err}). " +
                "The target may be an elevated window or the secure desktop, which this " +
                "user-session process cannot drive.");
        }
    }

    private static (int nx, int ny) ToAbsolute(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        // Windows maps the 0..65535 absolute space back to pixels via >>16 (divide by 65536), so
        // invert with 65536/size (rounded) to land on the intended pixel instead of ~1% off.
        long nx = ((long)(x - vx) * 65536 + vw / 2) / vw;
        long ny = ((long)(y - vy) * 65536 + vh / 2) / vh;
        return (Math.Clamp((int)nx, 0, 65535), Math.Clamp((int)ny, 0, 65535));
    }

    private static INPUT Mouse(uint flags, int nx = 0, int ny = 0, uint data = 0) => new()
    {
        type = INPUT_MOUSE,
        u = new INPUTUNION { mi = new MOUSEINPUT { dx = nx, dy = ny, mouseData = data, dwFlags = flags } },
    };

    public static void MouseMove(int x, int y)
    {
        var (nx, ny) = ToAbsolute(x, y);
        Send(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny));
    }

    public static void MouseClick(string button, int? x, int? y, int count)
    {
        if (x.HasValue && y.HasValue) MouseMove(x.Value, y.Value);

        (uint down, uint up) = button.ToLowerInvariant() switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };

        for (int i = 0; i < Math.Max(1, count); i++)
            Send(Mouse(down), Mouse(up));
    }

    public static void MouseDown(string button, int? x, int? y)
    {
        if (x.HasValue && y.HasValue) MouseMove(x.Value, y.Value);
        uint flag = button.ToLowerInvariant() switch
        {
            "right" => MOUSEEVENTF_RIGHTDOWN,
            "middle" => MOUSEEVENTF_MIDDLEDOWN,
            _ => MOUSEEVENTF_LEFTDOWN,
        };
        Send(Mouse(flag));
    }

    public static void MouseUp(string button, int? x, int? y)
    {
        if (x.HasValue && y.HasValue) MouseMove(x.Value, y.Value);
        uint flag = button.ToLowerInvariant() switch
        {
            "right" => MOUSEEVENTF_RIGHTUP,
            "middle" => MOUSEEVENTF_MIDDLEUP,
            _ => MOUSEEVENTF_LEFTUP,
        };
        Send(Mouse(flag));
    }

    /// <summary>Scroll in wheel notches. Positive dy scrolls up, positive dx scrolls right.</summary>
    public static void MouseScroll(int dx, int dy)
    {
        if (dy != 0) Send(Mouse(MOUSEEVENTF_WHEEL, data: unchecked((uint)(dy * WHEEL_DELTA))));
        if (dx != 0) Send(Mouse(MOUSEEVENTF_HWHEEL, data: unchecked((uint)(dx * WHEEL_DELTA))));
    }

    private static INPUT KeyUnicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0),
            },
        },
    };

    private static INPUT KeyVk(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = up ? KEYEVENTF_KEYUP : 0 },
        },
    };

    /// <summary>Type a literal string as Unicode key events (handles surrogate pairs).</summary>
    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            inputs.Add(KeyUnicode(c, up: false));
            inputs.Add(KeyUnicode(c, up: true));
        }
        Send(inputs.ToArray());
    }

    /// <summary>
    /// Send a keyboard chord such as "ctrl+shift+s", "alt+F4", "enter", or "{TAB}".
    /// Modifiers: ctrl, control, alt, shift, win/meta. The final token is the key.
    /// </summary>
    public static void SendKeys(string chord) => SendKeys(chord, 0);

    /// <summary>As <see cref="SendKeys(string)"/>, but if <paramref name="holdMs"/> &gt; 0 the final key is held
    /// down for that long before release (modifiers held throughout) — for press-and-hold (games, key-repeat).</summary>
    public static void SendKeys(string chord, int holdMs)
    {
        if (string.IsNullOrWhiteSpace(chord)) return;
        var tokens = chord.Trim().Trim('{', '}').Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return;

        var mods = new List<ushort>();
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            ushort? m = tokens[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => (ushort)0x11,
                "alt" or "menu" => (ushort)0x12,
                "shift" => (ushort)0x10,
                "win" or "meta" or "cmd" => (ushort)0x5B,
                _ => null,
            };
            if (m is null) throw new ArgumentException($"Unknown modifier '{tokens[i]}' in chord '{chord}'.");
            mods.Add(m.Value);
        }

        string keyToken = tokens[^1];
        var (vk, needShift) = ResolveKey(keyToken);
        bool autoShift = needShift && !mods.Contains(0x10);

        var down = new List<INPUT>();
        foreach (var m in mods) down.Add(KeyVk(m, up: false));
        if (autoShift) down.Add(KeyVk(0x10, up: false));
        down.Add(KeyVk(vk, up: false));

        var up = new List<INPUT>();
        up.Add(KeyVk(vk, up: true));
        if (autoShift) up.Add(KeyVk(0x10, up: true));
        for (int i = mods.Count - 1; i >= 0; i--) up.Add(KeyVk(mods[i], up: true));

        if (holdMs > 0)
        {
            Send(down.ToArray());
            Thread.Sleep(Math.Clamp(holdMs, 1, 30_000));
            Send(up.ToArray());
        }
        else
        {
            Send(down.Concat(up).ToArray());
        }
    }

    private static (ushort vk, bool needShift) ResolveKey(string token)
    {
        string t = token.ToLowerInvariant();
        ushort? named = t switch
        {
            "enter" or "return" => (ushort)0x0D,
            "tab" => (ushort)0x09,
            "esc" or "escape" => (ushort)0x1B,
            "space" or "spacebar" => (ushort)0x20,
            "backspace" or "back" => (ushort)0x08,
            "delete" or "del" => (ushort)0x2E,
            "insert" or "ins" => (ushort)0x2D,
            "home" => (ushort)0x24,
            "end" => (ushort)0x23,
            "pageup" or "pgup" => (ushort)0x21,
            "pagedown" or "pgdn" => (ushort)0x22,
            "up" => (ushort)0x26,
            "down" => (ushort)0x28,
            "left" => (ushort)0x25,
            "right" => (ushort)0x27,
            "printscreen" or "prtsc" => (ushort)0x2C,
            _ => null,
        };
        if (named is not null) return (named.Value, false);

        if (t.Length > 1 && t[0] == 'f' && int.TryParse(t.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
            return ((ushort)(0x70 + fn - 1), false); // F1..F24 = 0x70..

        if (token.Length == 1)
        {
            short scan = VkKeyScan(token[0]);
            if (scan == -1) throw new ArgumentException($"Cannot map character '{token}'.");
            ushort vk = (ushort)(scan & 0xFF);
            bool shift = (scan & 0x100) != 0;
            return (vk, shift);
        }

        throw new ArgumentException($"Unknown key token '{token}'.");
    }
}
