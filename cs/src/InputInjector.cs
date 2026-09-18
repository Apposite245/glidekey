using System;
using System.Collections.Generic;
using System.Text;

namespace GlideKey
{
    struct KeyEv
    {
        public int vk;
        public bool up;
        public bool ext;
        public ushort unicode; // non-zero: KEYEVENTF_UNICODE event for this char (vk ignored)

        public static KeyEv Down(int vk) => new() { vk = vk, ext = VK.IsExtended(vk) };
        public static KeyEv Up(int vk) => new() { vk = vk, up = true, ext = VK.IsExtended(vk) };

        public override string ToString()
            => unicode != 0 ? $"U+{unicode:X4}{(up ? "^" : "v")}" : $"{vk:X2}{(up ? "^" : "v")}";
    }

    // Sends key events to whatever window has focus. SendInput (one atomic batch) is the primary
    // path; if the OS accepts fewer events than sent (e.g. UIPI blocks an elevated target) the rest
    // go through keybd_event. In SelfTest mode nothing reaches the OS — events are only logged.
    class Injector
    {
        public bool SelfTest;
        public readonly List<KeyEv> Log = new();

        // Keyboard layout used to turn chars into keys. Defaults to the foreground window's layout
        // so swiped words type correctly on non-US layouts too.
        public Func<IntPtr> Layout = Native.ForegroundLayout;

        public void Inject(List<KeyEv> evs)
        {
            if (evs.Count == 0) return;
            if (SelfTest) { Log.AddRange(evs); return; }
            var arr = evs.ToArray();
            int sent = 0;
            try { sent = Native.Send(arr); } catch (Exception ex) { Debug.Log("SendInput threw " + ex.Message); }
            if (sent < arr.Length)
            {
                Debug.Log($"SendInput accepted {sent}/{arr.Length} (err {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}), keybd_event fallback");
                for (int i = sent; i < arr.Length; i++) Native.KeybdEvent(arr[i]);
            }
            if (Debug.On) Debug.Log("inject " + Describe(evs) + " -> fg " + Native.Foreground);
        }

        public static string Describe(IEnumerable<KeyEv> evs)
        {
            var sb = new StringBuilder();
            foreach (var e in evs) sb.Append(e).Append(' ');
            return sb.ToString().TrimEnd();
        }

        // Events that type `text` exactly, independent of modifier state (the caller lifts any
        // physically-held modifiers first). Uses real key strokes where the layout has the char,
        // Unicode input otherwise.
        public void AppendText(List<KeyEv> evs, string text)
        {
            IntPtr hkl = Layout();
            foreach (char c in text)
            {
                if (Native.CharToVk(c, hkl, out int vk, out bool shift))
                {
                    if (shift) evs.Add(KeyEv.Down(VK.LShift));
                    evs.Add(KeyEv.Down(vk));
                    evs.Add(KeyEv.Up(vk));
                    if (shift) evs.Add(KeyEv.Up(VK.LShift));
                }
                else
                {
                    evs.Add(new KeyEv { unicode = c });
                    evs.Add(new KeyEv { unicode = c, up = true });
                }
            }
        }

        public static void AppendTap(List<KeyEv> evs, int vk, int count = 1)
        {
            for (int i = 0; i < count; i++) { evs.Add(KeyEv.Down(vk)); evs.Add(KeyEv.Up(vk)); }
        }
    }

    // GK_DEBUG=1 -> %LOCALAPPDATA%\GlideKey\debug.log
    static class Debug
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("GK_DEBUG") == "1";
        static readonly string PathName = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlideKey", "debug.log");

        public static void Log(string msg)
        {
            if (!On) return;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName));
                System.IO.File.AppendAllText(PathName, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
