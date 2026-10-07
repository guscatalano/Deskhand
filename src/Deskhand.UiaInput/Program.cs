using System.Runtime.InteropServices;

// Deskhand uiAccess input tool — a tiny signed, uiAccess=true binary that can drive the secure desktop
// (UAC prompt / lock / logon), which a normal SYSTEM process cannot. Proof-of-concept + building block.
//
//   deskhand-uia whoami            print token uiAccess flag + desktop (verify the grant took)
//   deskhand-uia key <vk> [<vk>…]  press/release virtual-key codes (e.g. 0x1B = Esc, 0x0D = Enter)
//   deskhand-uia click <x> <y>     left click at absolute virtual-desktop pixels
//   deskhand-uia type <text>       type unicode text

static class P
{
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint t);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr t, int cls, out uint info, uint len, out uint ret);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr h, int i, byte[]? info, uint len, out uint need);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(IntPtr h);
    // DESKTOP_ATTACH_ACCESS: the minimal rights to attach + inject (what the SYSTEM helper used to attach).
    const uint DESKTOP_ATTACH_ACCESS = 0x0001 | 0x0002 | 0x0080 | 0x0040 | 0x0100;

    // Attach THIS thread to whatever desktop currently owns input (the secure desktop during a UAC prompt),
    // so SendInput targets it. A uiAccess process is permitted to do this and to inject there.
    static void AttachInputDesktop()
    {
        var h = OpenInputDesktop(0, false, DESKTOP_ATTACH_ACCESS);
        if (h == IntPtr.Zero) throw new Exception($"OpenInputDesktop failed, Win32 {Marshal.GetLastWin32Error()}");
        if (!SetThreadDesktop(h)) throw new Exception($"SetThreadDesktop failed, Win32 {Marshal.GetLastWin32Error()}");
    }

    const int INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOVE = 0x0001, ABS = 0x8000, VIRT = 0x4000, LDOWN = 0x0002, LUP = 0x0004;
    const uint KEYUP = 0x0002, UNICODE = 0x0004;
    const int SM_XVIRT = 76, SM_YVIRT = 77, SM_CXVIRT = 78, SM_CYVIRT = 79;

    [StructLayout(LayoutKind.Sequential)] struct INPUT { public int type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }

    static void Send(params INPUT[] a)
    {
        uint sent = SendInput((uint)a.Length, a, Marshal.SizeOf<INPUT>());
        if (sent != a.Length) throw new Exception($"SendInput injected {sent}/{a.Length}, Win32 {Marshal.GetLastWin32Error()}");
    }
    static INPUT Key(ushort vk, bool up) => new() { type = INPUT_KEYBOARD, u = new() { ki = new() { vk = vk, flags = up ? KEYUP : 0 } } };
    static INPUT Uni(char c, bool up) => new() { type = INPUT_KEYBOARD, u = new() { ki = new() { scan = c, flags = UNICODE | (up ? KEYUP : 0) } } };
    static INPUT Mouse(uint flags, int nx = 0, int ny = 0) => new() { type = INPUT_MOUSE, u = new() { mi = new() { dx = nx, dy = ny, flags = flags } } };

    static void Click(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRT), vy = GetSystemMetrics(SM_YVIRT);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRT)), vh = Math.Max(1, GetSystemMetrics(SM_CYVIRT));
        int nx = (int)(((long)(x - vx) * 65536 + vw / 2) / vw), ny = (int)(((long)(y - vy) * 65536 + vh / 2) / vh);
        Send(Mouse(MOVE | ABS | VIRT, nx, ny), Mouse(LDOWN), Mouse(LUP));
    }

    static string DesktopName()
    {
        var h = GetThreadDesktop(GetCurrentThreadId());
        GetUserObjectInformation(h, 2, null, 0, out uint need);
        var b = new byte[need];
        return GetUserObjectInformation(h, 2, b, need, out _) ? System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0') : "?";
    }

    static bool UiAccess()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /*QUERY*/, out IntPtr t)) return false;
        try { return GetTokenInformation(t, 26 /*TokenUIAccess*/, out uint v, 4, out _) && v != 0; }
        finally { }
    }

    static void Log(string line)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Deskhand");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "uia.log"), $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { }
    }

    static int Main(string[] a)
    {
        string cmd = string.Join(' ', a);
        try
        {
            if (a.Length == 0 || a[0] == "whoami")
            {
                var w = $"whoami: uiAccess={UiAccess()} desktop={DesktopName()} user={Environment.UserName}";
                Console.WriteLine(w); Log(w);
                return 0;
            }
            if (a[0] is "key" or "click" or "type") AttachInputDesktop();   // land on the input (secure) desktop first
            switch (a[0])
            {
                case "key":
                    var downs = new List<INPUT>(); var ups = new List<INPUT>();
                    foreach (var s in a.Skip(1)) { ushort vk = (ushort)Convert.ToInt32(s, 16); downs.Add(Key(vk, false)); ups.Insert(0, Key(vk, true)); }
                    Send(downs.Concat(ups).ToArray()); break;
                case "click": Click(int.Parse(a[1]), int.Parse(a[2])); break;
                case "type":
                    var ins = new List<INPUT>(); foreach (char c in string.Join(' ', a.Skip(1))) { ins.Add(Uni(c, false)); ins.Add(Uni(c, true)); }
                    Send(ins.ToArray()); break;
                default: Console.Error.WriteLine("unknown command"); Log($"'{cmd}' unknown command"); return 2;
            }
            Log($"'{cmd}' -> OK (uiAccess={UiAccess()} desktop={DesktopName()})");
            return 0;
        }
        catch (Exception ex) { Log($"'{cmd}' -> FAIL {ex.Message} (uiAccess={UiAccess()} desktop={DesktopName()})"); Console.Error.WriteLine("FAIL: " + ex.Message); return 1; }
    }
}
