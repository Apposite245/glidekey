using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace GlideKey
{
    // --unittest : layout geometry, VK table, modifier/typing state machine, swipe accuracy benchmark
    // --smoke    : real KeyboardForm + SelfTest injector, driven by WM_LBUTTON*/WM_MOUSEMOVE messages
    // --render   : paints the keyboard to PNG files for visual review
    static class Tests
    {
        static int fails, passes;
        static TextWriter o;

        static void Check(bool ok, string what)
        {
            if (ok) passes++; else { fails++; o.WriteLine("  FAIL " + what); }
        }

        static void Eq<T>(T got, T want, string what)
            => Check(EqualityComparer<T>.Default.Equals(got, want), $"{what}: got [{got}] want [{want}]");

        // ------------------------------------------------------------------ unit
        public static int RunUnit(TextWriter w)
        {
            o = w; fails = passes = 0;
            SettingsStore.Disabled = true;
            var sw = Stopwatch.StartNew();
            o.WriteLine("== layout"); Layout();
            o.WriteLine("== vk table"); VkTable();
            o.WriteLine("== icon"); IconTests();
            o.WriteLine("== controller"); ControllerTests();
            o.WriteLine("== swipe"); SwipeTests();
            o.WriteLine($"unit: {passes} passed, {fails} failed ({sw.ElapsedMilliseconds} ms)");
            o.Flush();
            return fails == 0 ? 0 : 1;
        }

        static void Layout()
        {
            foreach (var name in new[] { Layouts.CompactName, Layouts.FullName })
            {
                var rows = Layouts.Get(name);
                float width = Layouts.WidthUnits(rows);
                foreach (var r in rows)
                    if (r.Keys.Count > 0) Check(Math.Abs(Layouts.RowUnits(r) - width) < 0.001f, $"{name}: row sums {Layouts.RowUnits(r)} != {width}");

                var keys = Geometry.Compute(rows, 46, 5, 10, 40);
                for (int i = 0; i < keys.Count; i++)
                    for (int j = i + 1; j < keys.Count; j++)
                        if (keys[i].cell.IntersectsWith(keys[j].cell))
                        {
                            var a = RectangleF.Intersect(keys[i].cell, keys[j].cell);
                            Check(a.Width < 0.01f || a.Height < 0.01f, $"{name}: {keys[i].def.id} overlaps {keys[j].def.id}");
                        }
                var ids = keys.Select(k => k.def.id).ToList();
                Check(ids.Distinct().Count() == ids.Count, $"{name}: duplicate key ids " + string.Join(",", ids.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key)));

                var vks = new HashSet<int>(keys.Select(k => k.def.vk));
                var need = new List<int> { VK.Esc, VK.Tab, VK.Caps, VK.Back, VK.Enter, VK.Space, VK.LShift, VK.RShift, VK.LCtrl, VK.RCtrl,
                    VK.LAlt, VK.RAlt, VK.LWin, VK.Apps, VK.Left, VK.Right, VK.Up, VK.Down, VK.Home, VK.End, VK.PgUp, VK.PgDn,
                    VK.Ins, VK.Del, VK.PrtSc, VK.Grave, VK.Minus, VK.Equal, VK.LBracket, VK.RBracket, VK.Backslash,
                    VK.Semicolon, VK.Quote, VK.Comma, VK.Period, VK.Slash };
                for (int i = 1; i <= 12; i++) need.Add(VK.F(i));
                for (int d = 0; d <= 9; d++) need.Add(VK.Digit(d));
                for (char c = 'a'; c <= 'z'; c++) need.Add(VK.Letter(c));
                if (name == Layouts.FullName)
                {
                    for (int d = 0; d <= 9; d++) need.Add(VK.Num(d));
                    need.AddRange(new[] { VK.NumAdd, VK.NumSub, VK.NumMul, VK.NumDiv, VK.NumDec, VK.NumLock, VK.ScrLock, VK.Pause, VK.RWin,
                        VK.Mute, VK.VolUp, VK.VolDown, VK.PlayPause });
                }
                foreach (var vk in need) Check(vks.Contains(vk), $"{name}: missing vk 0x{vk:X2}");
                var ne = keys.Find(k => k.def.id == "numenter");
                if (name == Layouts.FullName) Check(ne != null && ne.def.vk == VK.Enter && ne.def.ext, "full: numpad enter = extended VK_RETURN");
                o.WriteLine($"  {name}: {keys.Count} keys, {width}u wide");
            }
        }

        static void VkTable()
        {
            Eq(VK.Comma, 0xBC, "VK_OEM_COMMA"); Eq(VK.Period, 0xBE, "VK_OEM_PERIOD"); Eq(VK.Slash, 0xBF, "VK_OEM_2");
            Eq(VK.Semicolon, 0xBA, "VK_OEM_1"); Eq(VK.Quote, 0xDE, "VK_OEM_7"); Eq(VK.Minus, 0xBD, "VK_OEM_MINUS");
            Eq(VK.Equal, 0xBB, "VK_OEM_PLUS"); Eq(VK.Grave, 0xC0, "VK_OEM_3"); Eq(VK.LBracket, 0xDB, "VK_OEM_4");
            Eq(VK.NumDec, 0x6E, "VK_DECIMAL"); Eq(VK.NumDiv, 0x6F, "VK_DIVIDE"); Eq(VK.NumMul, 0x6A, "VK_MULTIPLY");
            Eq(VK.NumSub, 0x6D, "VK_SUBTRACT"); Eq(VK.NumAdd, 0x6B, "VK_ADD"); Eq(VK.Num(7), 0x67, "VK_NUMPAD7");
            Eq(VK.F(12), 0x7B, "VK_F12"); Eq(VK.Letter('z'), 0x5A, "VK_Z");
            Check(VK.IsExtended(VK.Right) && VK.IsExtended(VK.RCtrl) && !VK.IsExtended(VK.LCtrl) && !VK.IsExtended(VK.Num(7)), "extended flags");
            Eq(Native.InputSize, IntPtr.Size == 8 ? 40 : 28, "sizeof(INPUT)");
        }

        // The icon must be embedded AND present as the exe's Win32 resource — the taskbar and a
        // pinned shortcut read the resource, not anything drawn at runtime.
        static void IconTests()
        {
            using var s = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("GlideKey.assets.glidekey.ico");
            Check(s != null, "glidekey.ico is embedded");
            if (s == null) return;
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            Check(raw.Length > 2000, $"icon is {raw.Length} bytes");
            Check(raw[0] == 0 && raw[1] == 0 && raw[2] == 1 && raw[3] == 0, "icon has an ICONDIR header");
            int frames = raw[4] | (raw[5] << 8);
            Check(frames >= 6, $"icon has {frames} sizes");
            for (int i = 0; i < frames; i++) // every frame's data must lie inside the file
            {
                int e = 6 + 16 * i;
                int len = BitConverter.ToInt32(raw, e + 8), off = BitConverter.ToInt32(raw, e + 12);
                Check(off > 0 && len > 0 && off + len <= raw.Length, $"icon frame {i} ({raw[e]}px) fits in the file");
            }
            foreach (int px in new[] { 16, 32, 48 })
            {
                using var ms = new MemoryStream(raw);
                using var ico = new Icon(ms, px, px);
                Check(ico.Width == px, $"icon has a {px}px frame (got {ico.Width})");
            }
            using (var ms = new MemoryStream(raw))
            using (var ico = new Icon(ms, 32, 32))
            using (var bmp = ico.ToBitmap())
            {
                // corners transparent: catches stray pixels from the generator
                Check(bmp.GetPixel(0, 0).A == 0 && bmp.GetPixel(31, 0).A == 0 && bmp.GetPixel(0, 31).A == 0,
                    "icon corners are transparent");
                Check(bmp.GetPixel(16, 16).A > 200, "icon centre is opaque");
            }
            // the Win32 resource in our own exe (what a pinned shortcut shows)
            string exe = Environment.ProcessPath;
            var own = Icon.ExtractAssociatedIcon(exe);
            Check(own != null && own.Width > 0, "exe carries a Win32 icon resource");
            own?.Dispose();
        }

        // A US-layout decoder for event logs: what text would these events produce?
        static string Decode(IEnumerable<KeyEv> evs)
        {
            var sb = new StringBuilder();
            bool shift = false, ctrl = false, alt = false, win = false;
            const string plainOem = ";=,-./`[\\]'", shiftOem = ":+<_>?~{|}\"";
            int[] oem = { 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE };
            foreach (var e in evs)
            {
                if (e.unicode != 0) { if (!e.up) sb.Append((char)e.unicode); continue; }
                switch (e.vk)
                {
                    case VK.LShift or VK.RShift: shift = !e.up; continue;
                    case VK.LCtrl or VK.RCtrl: ctrl = !e.up; continue;
                    case VK.LAlt or VK.RAlt: alt = !e.up; continue;
                    case VK.LWin or VK.RWin: win = !e.up; continue;
                }
                if (e.up) continue;
                if (ctrl || alt || win) { sb.Append($"<{(ctrl ? "C" : "")}{(alt ? "A" : "")}{(win ? "W" : "")}-{e.vk:X2}>"); continue; }
                if (e.vk >= 0x41 && e.vk <= 0x5A) sb.Append(shift ? (char)e.vk : char.ToLowerInvariant((char)e.vk));
                else if (e.vk >= 0x30 && e.vk <= 0x39) sb.Append(shift ? ")!@#$%^&*("[e.vk - 0x30] : (char)e.vk);
                else if (e.vk == VK.Space) sb.Append(' ');
                else if (e.vk == VK.Enter) sb.Append('\n');
                else if (e.vk == VK.Back) { if (sb.Length > 0) sb.Length--; }
                else if (Array.IndexOf(oem, e.vk) is int i && i >= 0) sb.Append(shift ? shiftOem[i] : plainOem[i]);
                else sb.Append($"<{e.vk:X2}>");
            }
            return sb.ToString();
        }

        class Rig
        {
            public Injector inj = new() { SelfTest = true };
            public Controller c;
            public List<KeyRect> keys;
            public long now = 1_000_000;
            public Rig(string layout = "compact")
            {
                inj.Layout = () => (IntPtr)0x04090409;
                c = new Controller(inj);
                keys = Geometry.Compute(Layouts.Get(layout), 46, 5, 10, 40);
                var centers = keys.Where(k => k.def.cls == KeyCls.Letter).ToDictionary(k => k.def.Letter, k => k.Center);
                c.Engine = new SwipeEngine(centers, Dict);
            }
            public KeyRect K(string id) => keys.Find(k => k.def.id == id);
            public void Tap(string id, int holdMs = 40)
            {
                var k = K(id);
                c.Down(0, k.def, k.Center, now); now += holdMs; c.Tick(now); c.Up(0, k.Center, now); now += 60;
            }
            public void Swipe(string word)
            {
                var pts = SimTrace(word, keys, null);
                var k = keys.Find(x => x.cell.Contains(pts[0]));
                c.Down(0, k.def, pts[0], now);
                for (int i = 1; i < pts.Count; i++) { now += 8; c.Move(0, pts[i], now); }
                c.Up(0, pts[^1], now); now += 300;
            }
            public string Text => Decode(inj.Log);
            public string Log => Injector.Describe(inj.Log);
        }

        static WordDict dict;
        static WordDict Dict => dict ??= WordDict.FromResource();

        static void ControllerTests()
        {
            var r = new Rig();
            r.Tap("b"); r.Tap("c");
            Eq(r.Log, "42v 42^ 43v 43^", "tap b c");

            r = new Rig(); r.Tap("lshift"); r.Tap("b"); r.Tap("c");
            Eq(r.Text, "Bc", "shift one-shot");
            Eq(r.Log, "A0v 42v 42^ A0^ 43v 43^", "shift one-shot events");

            r = new Rig(); r.Tap("lshift"); r.Tap("lshift"); r.Tap("b"); r.Tap("c"); r.Tap("lshift"); r.Tap("d");
            Eq(r.Text, "BCd", "shift double-tap locks");
            Eq(r.c.Mods.State(ModKind.Shift), ModifierState.St.Off, "shift unlocked");

            r = new Rig(); r.Tap("lshift", 600); r.Tap("b"); r.Tap("c");
            Eq(r.Text, "BC", "shift long-press locks");

            r = new Rig(); r.Tap("lctrl"); r.Tap("c"); r.Tap("v");
            Eq(r.Log, "A2v 43v 43^ A2^ 56v 56^", "ctrl one-shot = ctrl+c then plain v");

            r = new Rig(); r.Tap("lwin"); r.Tap("lwin");
            Eq(r.Log, "5Bv 5B^", "win tapped twice = bare win (start menu)");

            r = new Rig(); r.Tap("lalt"); r.Tap("lalt"); r.Tap("tab"); r.Tap("tab"); r.Tap("lalt");
            Eq(r.Log, "A4v 09v 09^ 09v 09^ A4^", "alt locked + tab tab (alt-tab switching)");

            r = new Rig(); r.Tap("lctrl"); r.Tap("lctrl"); r.c.ReleaseAll();
            Eq(r.Log, "A2v A2^", "ReleaseAll releases locked ctrl");

            r = new Rig(); r.Tap("caps");
            Eq(r.Log, "14v 14^", "caps toggle");

            r = new Rig(); r.Tap("lshift"); r.Tap("d1"); r.Tap("slash");
            Eq(r.Text, "!/", "shift+1 = !");

            // chord (touch): press shift, tap b while shift still held, release shift
            r = new Rig();
            var sh = r.K("lshift"); r.c.Down(1, sh.def, sh.Center, r.now);
            r.Tap("b");
            r.c.Up(1, sh.Center, r.now + 100);
            r.Tap("c");
            Eq(r.Text, "Bc", "shift chord");

            // typematic repeat
            r = new Rig();
            var bk = r.K("bksp");
            r.c.Down(0, bk.def, bk.Center, r.now);
            for (int t = 0; t <= 600; t += 15) r.c.Tick(r.now + t);
            r.c.Up(0, bk.Center, r.now + 600);
            int downs = r.inj.Log.Count(e => e.vk == VK.Back && !e.up), ups = r.inj.Log.Count(e => e.vk == VK.Back && e.up);
            Check(downs >= 4 && ups == 1, $"backspace repeat: {downs} downs {ups} ups");

            // letter held still repeats; letter moved becomes a swipe (no repeat)
            r = new Rig();
            var hk = r.K("h");
            r.c.Down(0, hk.def, hk.Center, r.now);
            for (int t = 0; t <= 900; t += 15) r.c.Tick(r.now + t);
            r.c.Up(0, hk.Center, r.now + 900);
            Check(r.Text.Length >= 3 && r.Text.All(ch => ch == 'h'), "letter hold repeats: " + r.Text);

            // double-space period
            r = new Rig(); r.Tap("h"); r.Tap("i"); r.Tap("space"); r.now -= 40; r.Tap("space");
            Eq(r.Text, "hi. ", "double-tap space = period");

            // swiping
            r = new Rig(); r.Swipe("hello");
            Eq(r.Text, "hello ", "swipe hello");
            r.Tap("period");
            Eq(r.Text, "hello. ", "punctuation after swipe moves the space");
            r.Swipe("world");
            Eq(r.Text, "hello. World ", "sentence start is capitalised");

            r = new Rig(); r.Swipe("the"); r.Tap("bksp");
            Eq(r.Text, "", "backspace after swipe deletes the word");

            // holding backspace after a swipe deletes the word, then keeps repeating
            r = new Rig(); r.Swipe("hello"); r.Swipe("this"); r.Swipe("test");
            bk = r.K("bksp");
            r.c.Down(0, bk.def, bk.Center, r.now);
            for (int t = 0; t <= 800; t += 15) r.c.Tick(r.now + t);
            r.c.Up(0, bk.Center, r.now + 800);
            Check(r.Text.Length < "hello this".Length - 3, "held backspace keeps deleting past the swiped word: [" + r.Text + "]");
            Eq(r.inj.Log.Count(e => e.vk == VK.Back && e.up) - r.inj.Log.Count(e => e.vk == VK.Back && !e.up) <= 0, true, "no stray backspace up");

            r = new Rig(); r.Tap("lshift"); r.Swipe("hello"); r.Swipe("there");
            Eq(r.Text, "Hello there ", "shift capitalises a swiped word once");

            r = new Rig(); r.Swipe("hello");
            if (r.c.Strip.Count > 1)
            {
                string alt = r.c.Strip[1];
                r.c.ChooseSuggestion(1);
                Eq(r.Text, alt + " ", "choosing a suggestion replaces the word");
            }
            else Check(false, "expected alternatives for 'hello'");

            r = new Rig(); r.Tap("lctrl"); r.Tap("lctrl"); r.Swipe("word");
            Check(r.Text.StartsWith("<C-"), "ctrl locked: moving over letters is not a swipe but ctrl+letter: " + r.Text);

            r = new Rig(); r.Swipe("dont");
            Eq(r.Text, "don't ", "contraction");

            r = new Rig("full"); r.Tap("num7"); r.Tap("numenter");
            Eq(r.Log, "67v 67^ 0Dv 0D^", "numpad keys");
            Check(r.inj.Log[2].ext, "numpad enter extended");
        }

        // ------------------------------------------------------------------ swipe simulation
        // Catmull-Rom spline through jittered key centres, sampled every ~3 px, like a quick,
        // slightly sloppy finger/mouse gesture.
        static List<PointF> SimTrace(string word, List<KeyRect> keys, Random rnd, float noise = 0.16f)
        {
            string k = WordDict.KeyString(word);
            var c = keys.Where(x => x.def.cls == KeyCls.Letter).ToDictionary(x => x.def.Letter, x => x.Center);
            float pitch = c['g'].X - c['f'].X;
            var v = new List<PointF>();
            foreach (char ch in k)
            {
                var p = c[ch];
                if (rnd != null) p = new PointF(p.X + Gauss(rnd) * noise * pitch, p.Y + Gauss(rnd) * noise * pitch * 0.8f);
                v.Add(p);
            }
            var pts = new List<PointF> { v[0] };
            for (int i = 0; i < v.Count - 1; i++)
            {
                PointF p0 = v[Math.Max(0, i - 1)], p1 = v[i], p2 = v[i + 1], p3 = v[Math.Min(v.Count - 1, i + 2)];
                float len = MathF.Sqrt((p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y));
                int steps = Math.Max(2, (int)(len / 3));
                for (int s = 1; s <= steps; s++)
                {
                    float t = s / (float)steps, t2 = t * t, t3 = t2 * t;
                    float x = 0.5f * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
                    float y = 0.5f * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);
                    if (rnd != null) { x += (float)(rnd.NextDouble() - 0.5); y += (float)(rnd.NextDouble() - 0.5); }
                    pts.Add(new PointF(x, y));
                }
            }
            return pts;
        }

        static float Gauss(Random r)
            => (float)(Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()));

        static void SwipeTests()
        {
            var keys = Geometry.Compute(Layouts.Compact, 46, 5, 10, 40);
            var centers = keys.Where(k => k.def.cls == KeyCls.Letter).ToDictionary(k => k.def.Letter, k => k.Center);
            var eng = new SwipeEngine(centers, Dict);
            Check(Dict.Count > 50000, "dictionary loaded: " + Dict.Count);

            // exact traces through key centres must give the word (or a same-path twin)
            foreach (var w in new[] { "you", "the", "hello", "world", "keyboard", "swipe", "typing", "because", "people", "question", "there", "don't" })
            {
                eng.Start(PointF.Empty); eng.Trace.Clear();
                foreach (var p in SimTrace(w, keys, null)) { if (eng.Trace.Count == 0) eng.Trace.Add(p); else eng.Add(p); }
                var res = eng.Recognize(3);
                Check(res.Words.Count > 0 && res.Words[0] == w, $"clean swipe '{w}' -> {string.Join(",", res.Words)}");
            }
            // "you" must beat "yo" when the trace ends on u
            {
                eng.Trace.Clear();
                foreach (var p in SimTrace("you", keys, null)) { if (eng.Trace.Count == 0) eng.Trace.Add(p); else eng.Add(p); }
                var res = eng.Recognize(5);
                Check(res.Words.IndexOf("you") == 0, "you beats yo: " + string.Join(",", res.Words));
            }

            // noisy benchmark
            var rnd = new Random(1234);
            Bench(eng, keys, rnd, Enumerable.Range(0, 1000).Select(i => Dict.Words[i]), "top-1000 words", 0.9, 0.97);
            var mid = Enumerable.Range(0, 400).Select(_ => Dict.Words[1000 + rnd.Next(9000)]);
            Bench(eng, keys, rnd, mid, "rank 1k-10k", 0.72, 0.88);
            var rare = Enumerable.Range(0, 300).Select(_ => Dict.Words[10000 + rnd.Next(30000)]);
            Bench(eng, keys, rnd, rare, "rank 10k-40k", 0.0, 0.0);
        }

        static void Bench(SwipeEngine eng, List<KeyRect> keys, Random rnd, IEnumerable<string> words, string label, double min1, double min3)
        {
            int n = 0, top1 = 0, top3 = 0; long ticks = 0; double worstMs = 0; string slowest = "";
            var misses = new List<string>();
            foreach (var w in words)
            {
                if (WordDict.KeyString(w).Length < 2) continue;
                n++;
                var pts = SimTrace(w, keys, rnd);
                eng.Trace.Clear();
                foreach (var p in pts) { if (eng.Trace.Count == 0) eng.Trace.Add(p); else eng.Add(p); }
                var sw = Stopwatch.StartNew();
                var res = eng.Recognize(3);
                sw.Stop();
                ticks += sw.ElapsedTicks; if (sw.Elapsed.TotalMilliseconds > worstMs) { worstMs = sw.Elapsed.TotalMilliseconds; slowest = w; }
                // a same-path twin ranked by frequency (e.g. were / we're) counts for top-1
                bool t1 = res.Words.Count > 0 && (res.Words[0] == w || (WordDict.KeyString(res.Words[0]) == WordDict.KeyString(w) && res.Words.Contains(w)));
                bool t3 = res.Words.Contains(w);
                if (t1) top1++;
                if (t3) top3++;
                else if (misses.Count < 12) misses.Add($"{w}->{(res.Words.Count > 0 ? res.Words[0] : "?")}");
            }
            double a1 = (double)top1 / n, a3 = (double)top3 / n;
            double avgMs = ticks * 1000.0 / Stopwatch.Frequency / n;
            o.WriteLine($"  {label}: top1 {a1:P1}  top3 {a3:P1}  ({n} words, avg {avgMs:0.0} ms, worst {worstMs:0} ms: {slowest})");
            o.WriteLine($"    misses: {string.Join(" ", misses)}");
            Check(a1 >= min1, $"{label} top1 {a1:P1} < {min1:P0}");
            Check(a3 >= min3, $"{label} top3 {a3:P1} < {min3:P0}");
        }

        // ------------------------------------------------------------------ smoke
        const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, MK_LBUTTON = 1;
        static IntPtr LP(PointF p) => (IntPtr)(((int)p.Y << 16) | ((int)p.X & 0xFFFF));

        public static int RunSmoke(TextWriter w)
        {
            o = w; fails = passes = 0;
            SettingsStore.Disabled = true;
            var inj = new Injector { SelfTest = true, Layout = () => (IntPtr)0x04090409 };
            using var form = new KeyboardForm(new Settings(), inj, Dict);
            IntPtr h = form.Handle;
            o.WriteLine($"== smoke (hwnd {h}, dpi {form.DeviceDpi})");

            var exStyle = GetWindowLong(h, -20);
            Check((exStyle & Native.WS_EX_NOACTIVATE) != 0, "window has WS_EX_NOACTIVATE");
            Check((exStyle & Native.WS_EX_TOPMOST) != 0, "window is topmost");
            Check((exStyle & Native.WS_EX_APPWINDOW) != 0 && (exStyle & Native.WS_EX_TOOLWINDOW) == 0, "window has a taskbar button");
            Check((GetWindowLong(h, -16) & Native.WS_MINIMIZEBOX) != 0, "window can minimize from the taskbar");
            Eq((int)Native.SendMessage(h, 0x0021, h, IntPtr.Zero), 3, "WM_MOUSEACTIVATE -> MA_NOACTIVATE");

            void Click(string id)
            {
                var c = form.FindKey(id).Center;
                Native.SendMessage(h, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, LP(c));
                Native.SendMessage(h, WM_LBUTTONUP, IntPtr.Zero, LP(c)); // no MK_LBUTTON on up
            }

            Click("b"); Click("c");
            Eq(Injector.Describe(inj.Log), "42v 42^ 43v 43^", "click b c injects");
            inj.Log.Clear();
            Click("lshift"); Click("b");
            Eq(Decode(inj.Log), "B", "click shift b");
            inj.Log.Clear();

            var keys = new List<KeyRect>();
            foreach (var id in "abcdefghijklmnopqrstuvwxyz") keys.Add(form.FindKey(id.ToString()));
            var pts = SimTrace("hello", keys, null);
            Native.SendMessage(h, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, LP(pts[0]));
            foreach (var p in pts.Skip(1)) Native.SendMessage(h, WM_MOUSEMOVE, (IntPtr)MK_LBUTTON, LP(p));
            Native.SendMessage(h, WM_LBUTTONUP, IntPtr.Zero, LP(pts[^1]));
            Eq(Decode(inj.Log), "hello ", "mouse swipe types 'hello '");
            if (fails > 0)
            {
                var eng = form.Ctl.Engine;
                var r = eng.Recognize(5);
                o.WriteLine($"  engine pitch {eng.Pitch} dict {eng.Dict.Count} trace {eng.Trace.Count} pts len {eng.TraceLength():0}; first {eng.Trace[0]} last {eng.Trace[^1]}");
                o.WriteLine($"  words [{string.Join(",", r.Words)}] avg {r.BestAvg:0.00}; sim first {pts[0]} last {pts[^1]}");
            }

            // header buttons: no overlaps, inside the header, clear of the drag grip
            foreach (var layout in new[] { Layouts.CompactName, Layouts.FullName })
            {
                form.SetLayout(layout);
                foreach (var scale in new[] { 0.6f, 1.0f, 1.5f, 2.2f })
                {
                    form.S.scale = scale;
                    form.ChangeSize(0);
                    var btns = form.HeaderButtons();
                    for (int i = 0; i < btns.Length; i++)
                    {
                        var (id, r) = btns[i];
                        Check(r.Top >= 0 && r.Bottom <= form.HeaderHeight + 0.01f,
                            $"{layout}@{scale}: {id} is inside the header ({r.Top:0.#}-{r.Bottom:0.#} vs {form.HeaderHeight:0.#})");
                        Check(r.Left >= form.GripRect.Right, $"{layout}@{scale}: {id} clears the drag grip");
                        Check(r.Right <= form.ClientSize.Width, $"{layout}@{scale}: {id} is inside the window");
                        for (int j = i + 1; j < btns.Length; j++)
                        {
                            var o = btns[j];
                            // a real gap, not merely non-intersecting: rects that just touch still
                            // draw their 1 px borders on top of each other
                            float gap = Math.Max(r.Left - o.r.Right, o.r.Left - r.Right);
                            Check(gap >= 2f * scale, $"{layout}@{scale}: {id} and {o.id} are only {gap:0.##} px apart");
                        }
                    }
                }
            }
            form.S.scale = 1.0f; form.ChangeSize(0); form.SetLayout(Layouts.CompactName);

            // hidden in the tray (no taskbar button): a second launch broadcasts WM_GLIDEKEY_SHOW,
            // which must bring this window back — the pinned-shortcut path
            bool shown = false;
            form.ShowRequested += () => { shown = true; form.Show(); form.Restore(); form.SurfaceNoActivate(); };
            form.Hide();
            Check(!form.Visible, "form hides to the tray");
            Native.SendMessage(h, Native.WM_GLIDEKEY_SHOW, IntPtr.Zero, IntPtr.Zero);
            Check(shown && form.Visible, "WM_GLIDEKEY_SHOW brings the hidden keyboard back");
            Check(Native.WM_GLIDEKEY_SHOW >= 0xC000 && Native.WM_GLIDEKEY_SHOW <= 0xFFFF,
                $"show message is a registered id (0x{Native.WM_GLIDEKEY_SHOW:X4})");
            form.Minimize();
            Check(form.WindowState == FormWindowState.Minimized, "form minimizes");
            Native.SendMessage(h, Native.WM_GLIDEKEY_SHOW, IntPtr.Zero, IntPtr.Zero);
            Check(form.WindowState == FormWindowState.Normal, "WM_GLIDEKEY_SHOW restores a minimized keyboard");

            o.WriteLine($"smoke: {passes} passed, {fails} failed");
            o.Flush();
            return fails == 0 ? 0 : 1;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        // ------------------------------------------------------------------ selfcheck
        // Real injection round trip: our own focused TextBox receives SendInput events. Injects only
        // after verifying the foreground window is ours, so nothing can land in another app.
        public static int RunSelfCheck(TextWriter w)
        {
            o = w; fails = passes = 0;
            using var f = new Form { Text = "GlideKey self-check", Width = 420, Height = 120, StartPosition = FormStartPosition.CenterScreen, TopMost = true };
            var tb = new TextBox { Dock = DockStyle.Fill, Multiline = true, Font = new Font("Segoe UI", 12f) };
            f.Controls.Add(tb);
            f.Show();
            var until = DateTime.Now.AddSeconds(3);
            while (DateTime.Now < until && Native.Foreground != f.Handle)
            {
                SetForegroundWindow(f.Handle); f.Activate(); tb.Focus();
                Application.DoEvents(); System.Threading.Thread.Sleep(50);
            }
            tb.Focus(); Application.DoEvents();
            if (Native.Foreground != f.Handle || !tb.Focused)
            {
                o.WriteLine("selfcheck: could not get focus for the test window (another app kept the foreground) — nothing injected");
                o.Flush();
                return 2;
            }

            var inj = new Injector(); // real SendInput
            var c = new Controller(inj) { AutoCapitalize = false };
            var evs = new List<KeyEv>();
            inj.AppendText(evs, "Hello, world. bc ");
            evs.Add(new KeyEv { unicode = 'é' }); evs.Add(new KeyEv { unicode = 'é', up = true });
            if (Native.Foreground != f.Handle) { o.WriteLine("selfcheck: lost focus — aborted"); return 2; }
            inj.Inject(evs);
            var end = DateTime.Now.AddMilliseconds(700);
            while (DateTime.Now < end) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
            Eq(tb.Text, "Hello, world. bc é", "text typed via SendInput");

            // a VK tap through the controller path (sticky shift + letter), then backspace repeat
            var keys = Geometry.Compute(Layouts.Compact, 46, 5, 10, 40);
            KeyDef K(string id) => keys.Find(k => k.def.id == id).def;
            long t = 0;
            void Tap(string id) { c.Down(0, K(id), PointF.Empty, t); c.Up(0, PointF.Empty, t += 50); t += 50; }
            if (Native.Foreground == f.Handle)
            {
                Tap("bksp"); Tap("lshift"); Tap("x"); Tap("y");
                end = DateTime.Now.AddMilliseconds(500);
                while (DateTime.Now < end) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                Eq(tb.Text, "Hello, world. bc Xy", "controller taps via SendInput");
            }
            o.WriteLine($"selfcheck: {passes} passed, {fails} failed");
            o.Flush();
            return fails == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ render
        public static int Render(string pathBase, string layout)
        {
            SettingsStore.Disabled = true;
            var inj = new Injector { SelfTest = true, Layout = () => (IntPtr)0x04090409 };
            using var form = new KeyboardForm(new Settings { layout = layout }, inj, Dict);
            _ = form.Handle;
            using (var b = form.Snapshot()) b.Save(pathBase + "_idle.png");

            // mid-swipe with live suggestions, shift one-shot, ctrl locked
            var sh = form.FindKey("lshift"); form.Ctl.Down(0, sh.def, sh.Center, 0); form.Ctl.Up(0, sh.Center, 50);
            var keys = new List<KeyRect>();
            foreach (var id in "abcdefghijklmnopqrstuvwxyz") keys.Add(form.FindKey(id.ToString()));
            var pts = SimTrace("keyboard", keys, new Random(3));
            var k0 = keys.Find(k => k.cell.Contains(pts[0]));
            long t = 100;
            form.Ctl.Down(0, k0.def, pts[0], t);
            foreach (var p in pts.Skip(1)) form.Ctl.Move(0, p, t += 8);
            using (var b = form.Snapshot()) b.Save(pathBase + "_swipe.png");
            form.Ctl.Up(0, pts[^1], t + 10);
            var ct = form.FindKey("lctrl"); form.Ctl.Down(0, ct.def, ct.Center, t + 100); form.Ctl.Up(0, ct.Center, t + 700);
            using (var b = form.Snapshot()) b.Save(pathBase + "_after.png");
            return 0;
        }
    }
}
