using System;
using System.Collections.Generic;
using System.Drawing;

namespace GlideKey
{
    // Virtual-key codes (winuser.h).
    static class VK
    {
        public const int Back = 0x08, Tab = 0x09, Clear = 0x0C, Enter = 0x0D, Pause = 0x13, Caps = 0x14,
            Esc = 0x1B, Space = 0x20, PgUp = 0x21, PgDn = 0x22, End = 0x23, Home = 0x24,
            Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, PrtSc = 0x2C, Ins = 0x2D, Del = 0x2E,
            LWin = 0x5B, RWin = 0x5C, Apps = 0x5D,
            NumMul = 0x6A, NumAdd = 0x6B, NumSub = 0x6D, NumDec = 0x6E, NumDiv = 0x6F,
            NumLock = 0x90, ScrLock = 0x91,
            LShift = 0xA0, RShift = 0xA1, LCtrl = 0xA2, RCtrl = 0xA3, LAlt = 0xA4, RAlt = 0xA5,
            Mute = 0xAD, VolDown = 0xAE, VolUp = 0xAF, PlayPause = 0xB3,
            Semicolon = 0xBA, Equal = 0xBB, Comma = 0xBC, Minus = 0xBD, Period = 0xBE, Slash = 0xBF,
            Grave = 0xC0, LBracket = 0xDB, Backslash = 0xDC, RBracket = 0xDD, Quote = 0xDE;
        public static int F(int n) => 0x70 + n - 1;
        public static int Num(int d) => 0x60 + d;
        public static int Digit(int d) => 0x30 + d;
        public static int Letter(char c) => 0x41 + (char.ToLowerInvariant(c) - 'a');

        // Keys that need KEYEVENTF_EXTENDEDKEY (E0-prefixed scan codes).
        public static bool IsExtended(int vk) => vk switch
        {
            Ins or Del or Home or End or PgUp or PgDn or Left or Up or Right or Down => true,
            RCtrl or RAlt or LWin or RWin or Apps or NumDiv or NumLock or PrtSc => true,
            Mute or VolDown or VolUp or PlayPause => true,
            _ => false
        };
    }

    enum KeyCls { Letter, Symbol, Key, Mod, Toggle, Space, Spacer }
    enum ModKind { None, Shift, Ctrl, Alt, Win }

    class KeyDef
    {
        public string id;
        public string label;       // main legend (letters: lowercase)
        public string shiftLabel;  // symbol keys: the shifted character
        public int vk;
        public bool ext;
        public KeyCls cls;
        public ModKind mod;
        public float span = 1f;    // width in key units
        public int rowSpan = 1;    // numpad + and Enter are two rows tall
        public bool repeat;        // auto-repeat while held (like a physical key)
        public int altVk;          // numpad: the navigation key sent while NumLock is off
        public string altLabel;
        public char Letter => cls == KeyCls.Letter ? label[0] : '\0';
        public override string ToString() => id;
    }

    class KRow
    {
        public readonly float H; // height in key units
        public readonly List<KeyDef> Keys = new();
        public KRow(float h) { H = h; }
        public KRow Add(params KeyDef[] ks) { Keys.AddRange(ks); return this; }
    }

    static class Layouts
    {
        public const string CompactName = "compact", FullName = "full";

        public static KRow[] Get(string name) => name == FullName ? Full : Compact;

        // ---------- key factories ----------
        static KeyDef K(string id, string label, int vk, float span = 1f, bool repeat = false)
            => new KeyDef { id = id, label = label, vk = vk, ext = VK.IsExtended(vk), cls = KeyCls.Key, span = span, repeat = repeat };

        static KeyDef L(char c) => new KeyDef { id = c.ToString(), label = c.ToString(), vk = VK.Letter(c), cls = KeyCls.Letter, repeat = true };

        static KeyDef S(string id, string plain, string shifted, int vk)
            => new KeyDef { id = id, label = plain, shiftLabel = shifted, vk = vk, cls = KeyCls.Symbol, repeat = true };

        static KeyDef M(string id, string label, int vk, ModKind kind, float span)
            => new KeyDef { id = id, label = label, vk = vk, ext = VK.IsExtended(vk), cls = KeyCls.Mod, mod = kind, span = span };

        static KeyDef T(string id, string label, int vk, float span = 1f)
            => new KeyDef { id = id, label = label, vk = vk, ext = VK.IsExtended(vk), cls = KeyCls.Toggle, span = span };

        static KeyDef Gap(float span) => new KeyDef { id = "", cls = KeyCls.Spacer, span = span };

        static KeyDef Np(string label, int vk, int altVk = 0, string altLabel = null, float span = 1f)
            => new KeyDef { id = "num" + label, label = label, vk = vk, ext = VK.IsExtended(vk), cls = KeyCls.Key, span = span, repeat = true, altVk = altVk, altLabel = altLabel };

        static KeyDef Space(float span) => new KeyDef { id = "space", label = "", vk = VK.Space, cls = KeyCls.Space, span = span, repeat = true };

        static IEnumerable<KeyDef> Letters(string s) { foreach (var c in s) yield return L(c); }

        static KeyDef[] DigitRow()
        {
            var k = new List<KeyDef> { S("grave", "`", "~", VK.Grave) };
            const string sh = "!@#$%^&*()";
            for (int i = 0; i < 10; i++)
            {
                int d = (i + 1) % 10;
                k.Add(S("d" + d, d.ToString(), sh[i].ToString(), VK.Digit(d)));
            }
            k.Add(S("minus", "-", "_", VK.Minus));
            k.Add(S("equals", "=", "+", VK.Equal));
            return k.ToArray();
        }

        static KeyDef Bksp(float span) => K("bksp", "⌫", VK.Back, span, repeat: true);
        static KeyDef Arrow(string id, string label, int vk) => K(id, label, vk, 1, repeat: true);

        // ---------- Compact: 65%-style, 16u wide, still covers every key a laptop has ----------
        public static readonly KRow[] Compact = BuildCompact();

        static KRow[] BuildCompact()
        {
            var fn = new KRow(0.72f).Add(K("esc", "esc", VK.Esc));
            for (int i = 1; i <= 12; i++) fn.Add(K("f" + i, "F" + i, VK.F(i)));
            fn.Add(K("prtsc", "prt sc", VK.PrtSc), K("ins", "ins", VK.Ins), K("del", "del", VK.Del, 1, true));

            var r1 = new KRow(1).Add(DigitRow()).Add(Bksp(2), K("home", "home", VK.Home, 1, true));

            var r2 = new KRow(1).Add(K("tab", "tab", VK.Tab, 1.5f, true)).Add(Letters("qwertyuiop").ToArray())
                .Add(S("lbr", "[", "{", VK.LBracket), S("rbr", "]", "}", VK.RBracket), S("bsl", "\\", "|", VK.Backslash))
                .Add(K("pgup", "pg up", VK.PgUp, 1, true));
            r2.Keys[r2.Keys.Count - 2].span = 1.5f;

            var r3 = new KRow(1).Add(T("caps", "caps", VK.Caps, 1.75f)).Add(Letters("asdfghjkl").ToArray())
                .Add(S("semi", ";", ":", VK.Semicolon), S("quote", "'", "\"", VK.Quote))
                .Add(K("enter", "enter", VK.Enter, 2.25f, true), K("pgdn", "pg dn", VK.PgDn, 1, true));

            var r4 = new KRow(1).Add(M("lshift", "shift", VK.LShift, ModKind.Shift, 2.25f)).Add(Letters("zxcvbnm").ToArray())
                .Add(S("comma", ",", "<", VK.Comma), S("period", ".", ">", VK.Period), S("slash", "/", "?", VK.Slash))
                .Add(M("rshift", "shift", VK.RShift, ModKind.Shift, 1.75f), Arrow("up", "↑", VK.Up), K("end", "end", VK.End, 1, true));

            var r5 = new KRow(1).Add(M("lctrl", "ctrl", VK.LCtrl, ModKind.Ctrl, 1.25f), M("lwin", "win", VK.LWin, ModKind.Win, 1.25f),
                M("lalt", "alt", VK.LAlt, ModKind.Alt, 1.25f), Space(6.25f), M("ralt", "alt", VK.RAlt, ModKind.Alt, 1f),
                K("apps", "☰", VK.Apps), M("rctrl", "ctrl", VK.RCtrl, ModKind.Ctrl, 1f),
                Arrow("left", "←", VK.Left), Arrow("down", "↓", VK.Down), Arrow("right", "→", VK.Right));

            return new[] { fn, new KRow(0.18f), r1, r2, r3, r4, r5 };
        }

        // ---------- Full: 104-key — main block | nav cluster | numpad (+ media keys) ----------
        public static readonly KRow[] Full = BuildFull();
        const float ClusterGap = 0.4f;

        static KRow[] BuildFull()
        {
            var fn = new KRow(0.8f).Add(K("esc", "esc", VK.Esc), Gap(1));
            for (int i = 1; i <= 12; i++)
            {
                fn.Add(K("f" + i, "F" + i, VK.F(i)));
                if (i == 4 || i == 8) fn.Add(Gap(0.5f));
            }
            fn.Add(Gap(ClusterGap), K("prtsc", "prt sc", VK.PrtSc), T("scrlk", "scr lk", VK.ScrLock), K("pause", "pause", VK.Pause));
            fn.Add(Gap(ClusterGap), K("mute", "mute", VK.Mute), K("voldn", "vol −", VK.VolDown, 1, true),
                K("volup", "vol +", VK.VolUp, 1, true), K("play", "play", VK.PlayPause));

            var r1 = new KRow(1).Add(DigitRow()).Add(Bksp(2), Gap(ClusterGap),
                K("ins", "ins", VK.Ins), K("home", "home", VK.Home, 1, true), K("pgup", "pg up", VK.PgUp, 1, true), Gap(ClusterGap),
                T("numlk", "num", VK.NumLock), Np("/", VK.NumDiv), Np("*", VK.NumMul), Np("-", VK.NumSub));

            var r2 = new KRow(1).Add(K("tab", "tab", VK.Tab, 1.5f, true)).Add(Letters("qwertyuiop").ToArray())
                .Add(S("lbr", "[", "{", VK.LBracket), S("rbr", "]", "}", VK.RBracket), S("bsl", "\\", "|", VK.Backslash));
            r2.Keys[r2.Keys.Count - 1].span = 1.5f;
            var nadd = Np("+", VK.NumAdd); nadd.rowSpan = 2;
            r2.Add(Gap(ClusterGap), K("del", "del", VK.Del, 1, true), K("end", "end", VK.End, 1, true), K("pgdn", "pg dn", VK.PgDn, 1, true),
                Gap(ClusterGap), Np("7", VK.Num(7), VK.Home, "home"), Np("8", VK.Num(8), VK.Up, "↑"), Np("9", VK.Num(9), VK.PgUp, "pg up"), nadd);

            var r3 = new KRow(1).Add(T("caps", "caps", VK.Caps, 1.75f)).Add(Letters("asdfghjkl").ToArray())
                .Add(S("semi", ";", ":", VK.Semicolon), S("quote", "'", "\"", VK.Quote), K("enter", "enter", VK.Enter, 2.25f, true))
                .Add(Gap(ClusterGap), Gap(3), Gap(ClusterGap),
                    Np("4", VK.Num(4), VK.Left, "←"), Np("5", VK.Num(5), VK.Clear, "clr"), Np("6", VK.Num(6), VK.Right, "→"), Gap(1));

            var nent = new KeyDef { id = "numenter", label = "enter", vk = VK.Enter, ext = true, cls = KeyCls.Key, rowSpan = 2, repeat = true };
            var r4 = new KRow(1).Add(M("lshift", "shift", VK.LShift, ModKind.Shift, 2.25f)).Add(Letters("zxcvbnm").ToArray())
                .Add(S("comma", ",", "<", VK.Comma), S("period", ".", ">", VK.Period), S("slash", "/", "?", VK.Slash),
                    M("rshift", "shift", VK.RShift, ModKind.Shift, 2.75f))
                .Add(Gap(ClusterGap), Gap(1), Arrow("up", "↑", VK.Up), Gap(1), Gap(ClusterGap),
                    Np("1", VK.Num(1), VK.End, "end"), Np("2", VK.Num(2), VK.Down, "↓"), Np("3", VK.Num(3), VK.PgDn, "pg dn"), nent);

            var r5 = new KRow(1).Add(M("lctrl", "ctrl", VK.LCtrl, ModKind.Ctrl, 1.25f), M("lwin", "win", VK.LWin, ModKind.Win, 1.25f),
                M("lalt", "alt", VK.LAlt, ModKind.Alt, 1.25f), Space(6.25f), M("ralt", "alt", VK.RAlt, ModKind.Alt, 1.25f),
                M("rwin", "win", VK.RWin, ModKind.Win, 1.25f), K("apps", "☰", VK.Apps, 1.25f), M("rctrl", "ctrl", VK.RCtrl, ModKind.Ctrl, 1.25f))
                .Add(Gap(ClusterGap), Arrow("left", "←", VK.Left), Arrow("down", "↓", VK.Down), Arrow("right", "→", VK.Right), Gap(ClusterGap),
                    Np("0", VK.Num(0), VK.Ins, "ins", 2), Np(".", VK.NumDec, VK.Del, "del"), Gap(1));

            return new[] { fn, new KRow(0.22f), r1, r2, r3, r4, r5 };
        }

        public static float RowUnits(KRow r) { float s = 0; foreach (var k in r.Keys) s += k.span; return s; }

        public static float WidthUnits(KRow[] rows)
        {
            float w = 0;
            foreach (var r in rows) w = Math.Max(w, RowUnits(r));
            return w;
        }

        public static float HeightUnits(KRow[] rows) { float h = 0; foreach (var r in rows) h += r.H; return h; }
    }

    class KeyRect
    {
        public KeyDef def;
        public RectangleF rect;  // visual key (inset by the gap)
        public RectangleF cell;  // full cell, used for hit testing so gaps aren't dead zones
        public PointF Center => new(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
    }

    static class Geometry
    {
        // Every row of a layout sums to the same number of units, so a single unit size keeps
        // columns aligned across rows (the nav cluster and numpad line up; tall keys fit).
        public static List<KeyRect> Compute(KRow[] rows, float unit, float gap, float originX, float originY)
        {
            var list = new List<KeyRect>();
            float y = originY;
            for (int ri = 0; ri < rows.Length; ri++)
            {
                float x = originX;
                foreach (var k in rows[ri].Keys)
                {
                    float w = k.span * unit;
                    if (k.cls != KeyCls.Spacer)
                    {
                        float h = 0;
                        for (int rj = ri, n = 0; n < k.rowSpan && rj < rows.Length; rj++)
                        {
                            h += rows[rj].H * unit;
                            if (rows[rj].Keys.Count > 0) n++;
                        }
                        var cell = new RectangleF(x, y, w, h);
                        var rect = RectangleF.Inflate(cell, -gap / 2f, -gap / 2f);
                        list.Add(new KeyRect { def = k, rect = rect, cell = cell });
                    }
                    x += w;
                }
                y += rows[ri].H * unit;
            }
            return list;
        }
    }
}
