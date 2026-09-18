using System;
using System.Runtime.InteropServices;

namespace GlideKey
{
    // All P/Invoke in one place.
    static class Native
    {
        public const uint KEYEVENTF_EXTENDEDKEY = 0x1;
        public const uint KEYEVENTF_KEYUP = 0x2;
        public const uint KEYEVENTF_UNICODE = 0x4;

        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        // INPUT must be declared with the full union (MOUSEINPUT is the largest member), otherwise
        // Marshal.SizeOf<INPUT>() is 32 instead of 40 on x64 and SendInput rejects every call with
        // ERROR_INVALID_PARAMETER. That was the reason the first draft concluded "SendInput fails".
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        static extern uint MapVirtualKey(uint uCode, uint uMapType);

        // Marks our own injections (dwExtraInfo) so they are recognisable in hooks/logs.
        static readonly IntPtr Tag = new IntPtr(0x474B4559); // "GKEY"

        public static int InputSize => Marshal.SizeOf<INPUT>();

        // Sends a batch atomically (no real input can interleave). Returns the number of events
        // the OS accepted; on a short count the caller falls back to keybd_event for the rest.
        public static int Send(KeyEv[] evs)
        {
            var inputs = new INPUT[evs.Length];
            for (int i = 0; i < evs.Length; i++)
            {
                var e = evs[i];
                inputs[i].type = 1; // INPUT_KEYBOARD
                if (e.unicode != 0)
                {
                    inputs[i].u.ki.wScan = e.unicode;
                    inputs[i].u.ki.dwFlags = KEYEVENTF_UNICODE | (e.up ? KEYEVENTF_KEYUP : 0);
                }
                else
                {
                    inputs[i].u.ki.wVk = (ushort)e.vk;
                    inputs[i].u.ki.wScan = (ushort)MapVirtualKey((uint)e.vk, 0);
                    inputs[i].u.ki.dwFlags = (e.ext ? KEYEVENTF_EXTENDEDKEY : 0) | (e.up ? KEYEVENTF_KEYUP : 0);
                }
                inputs[i].u.ki.dwExtraInfo = Tag;
            }
            return (int)SendInput((uint)inputs.Length, inputs, InputSize);
        }

        public static void KeybdEvent(KeyEv e)
        {
            if (e.unicode != 0) return; // keybd_event cannot carry Unicode
            byte scan = (byte)MapVirtualKey((uint)e.vk, 0);
            keybd_event((byte)e.vk, scan, (e.ext ? KEYEVENTF_EXTENDEDKEY : 0) | (e.up ? KEYEVENTF_KEYUP : 0), (UIntPtr)0x474B4559u);
        }

        // ---------- keyboard layout ----------
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScanEx(char ch, IntPtr hkl);

        public static IntPtr Foreground => GetForegroundWindow();

        public static IntPtr ForegroundLayout()
            => GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero));

        // char -> (vk, needs shift) in the foreground window's keyboard layout. False when the
        // char needs Ctrl/Alt (AltGr) or isn't on the layout — caller then uses Unicode input.
        public static bool CharToVk(char c, IntPtr hkl, out int vk, out bool shift)
        {
            short r = VkKeyScanEx(c, hkl);
            vk = r & 0xFF; shift = (r & 0x100) != 0;
            if (r == -1 || (r & 0x600) != 0) return false;
            return true;
        }

        // ---------- key state ----------
        [DllImport("user32.dll")] public static extern short GetKeyState(int nVirtKey);
        public static bool ToggleOn(int vk) => (GetKeyState(vk) & 0x01) != 0;

        // ---------- window ----------
        public const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_MINIMIZEBOX = 0x20000, WS_SYSMENU = 0x80000;        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int X, int Y, int cx, int cy, uint flags);

        public static void MoveNoActivate(IntPtr h, int x, int y)
            => SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | 0x4 /*NOZORDER*/);

        public static void SetTopmost(IntPtr h, bool on)
            => SetWindowPos(h, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);

        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        // Disable the touch press-and-hold => right-click gesture (it delays touch key repeat).
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetProp(IntPtr h, string name, IntPtr data);
        public static void DisablePressAndHold(IntPtr h) => SetProp(h, "MicrosoftTabletPenServiceProperty", (IntPtr)0x1);

        // Windows 11 rounded corners + shadow for a borderless window. Harmless no-op on Win10.
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        public static void RoundCorners(IntPtr h)
        {
            int round = 2; // DWMWCP_ROUND
            try { DwmSetWindowAttribute(h, 33, ref round, 4); } catch { }
            int dark = 1;
            try { DwmSetWindowAttribute(h, 20, ref dark, 4); } catch { }
        }
    }
}
