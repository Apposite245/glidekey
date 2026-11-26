using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace GlideKey
{
    class KeyboardForm : Form
    {
        // ---------- theme ----------
        static readonly Color BgTop = Color.FromArgb(24, 26, 32), BgBot = Color.FromArgb(15, 16, 20);
        static readonly Color KeyFill = Color.FromArgb(44, 48, 58), KeyFillBot = Color.FromArgb(37, 40, 49);
        static readonly Color FnFill = Color.FromArgb(32, 35, 43), FnFillBot = Color.FromArgb(27, 29, 36);
        static readonly Color KeyEdge = Color.FromArgb(22, 255, 255, 255);
        static readonly Color CText = Color.FromArgb(236, 239, 245), CDim = Color.FromArgb(150, 158, 174), CFaint = Color.FromArgb(98, 106, 122);
        static readonly Color Accent = Color.FromArgb(96, 140, 255), Accent2 = Color.FromArgb(150, 110, 255);
        static readonly Color ChipFill = Color.FromArgb(36, 40, 50);

        const float BaseUnit = 46f, BaseGap = 5f, BasePad = 10f, BaseHeader = 40f;

        public readonly Settings S;
        public readonly Controller Ctl;
        readonly WordDict dict;

        KRow[] rows;
        List<KeyRect> keys = new();
        float scale, unit, pad, headerH;

        RectangleF btnLayout, btnMinus, btnPlus, btnMenu, btnMin, btnHide, gripRect;
        readonly List<RectangleF> chipRects = new();
        public ContextMenuStrip OptionsMenu;
        public event Action HideRequested;
        public event Action ShowRequested;

        Bitmap baseLayer; string baseKey;
        Font fLetter, fSym, fSmall, fTiny, fHeader, fChip;
        readonly System.Windows.Forms.Timer tick = new() { Interval = 15 };
        int pollCounter; string lockState = "";

        bool dragging; Point dragOffset;
        bool pointerOnKeys;

        public KeyboardForm(Settings s, Injector inj, WordDict dict)
        {
            S = s;
            this.dict = dict;
            Ctl = new Controller(inj);
            ApplySettingsToController();
            Ctl.Changed += () => Invalidate();

            Text = "GlideKey";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = BgBot;
            TopMost = s.topmost;
            Opacity = Math.Clamp(s.opacity, 0.4, 1.0);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            tick.Tick += (_, _) => OnTick();
            tick.Start();
            Rebuild();
        }

        public void ApplySettingsToController()
        {
            Ctl.AutoSpace = S.autoSpace;
            Ctl.DoubleSpacePeriod = S.doubleSpacePeriod;
            Ctl.AutoCapitalize = S.autoCapitalize;
            Ctl.SmartBackspace = S.smartBackspace;
        }

        // ---------------------------------------------------------------- window behaviour
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // APPWINDOW + minimize box: a taskbar button that minimizes/restores the keyboard
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_APPWINDOW;
                cp.ExStyle &= ~Native.WS_EX_TOOLWINDOW;
                cp.Style |= Native.WS_MINIMIZEBOX | Native.WS_SYSMENU;
                if (S == null || S.topmost) cp.ExStyle |= Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            if (m.Msg == Native.WM_GLIDEKEY_SHOW)
            {
                Debug.Log("show requested by another instance");
                ShowRequested?.Invoke();
                return;
            }
            base.WndProc(ref m);
        }

        // Never moves focus itself: after restoring from the taskbar the user clicks back into their app.
        public void Minimize()
        {
            Ctl.ReleaseAll();
            WindowState = FormWindowState.Minimized;
        }

        public void Restore()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        }

        // Make sure the keyboard is on screen and on top, without taking focus from the app
        // the user is typing into.
        public void SurfaceNoActivate()
        {
            if (IsHandleCreated) Native.BringToFrontNoActivate(Handle, S.topmost);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized) Ctl?.ReleaseAll();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
            Native.DisablePressAndHold(Handle);
            Native.AllowMessage(Handle, Native.WM_GLIDEKEY_SHOW);
            if (DeviceDpi != 96) Rebuild(); // sizes were computed at 96 dpi before the handle existed
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            Rebuild();
        }

        public void PlaceInitially()
        {
            var wa = Screen.FromPoint(new Point(S.x ?? 0, S.y ?? 0)).WorkingArea;
            if (S.x == null || S.y == null) wa = Screen.PrimaryScreen.WorkingArea;
            int x = S.x ?? wa.Left + (wa.Width - Width) / 2;
            int y = S.y ?? wa.Bottom - Height - 12;
            x = Math.Clamp(x, wa.Left - Width + 80, wa.Right - 80);
            y = Math.Clamp(y, wa.Top, wa.Bottom - 40);
            Location = new Point(x, y);
        }

        public void SetTopmost(bool on)
        {
            S.topmost = on;
            if (IsHandleCreated) Native.SetTopmost(Handle, on);
        }

        // ---------------------------------------------------------------- layout
        public void Rebuild()
        {
            float dpi = IsHandleCreated ? DeviceDpi / 96f : 1f;
            scale = dpi * Math.Clamp(S.scale, 0.6f, 2.2f);
            unit = BaseUnit * scale; pad = BasePad * scale; headerH = BaseHeader * scale;
            rows = Layouts.Get(S.layout);
            keys = Geometry.Compute(rows, unit, BaseGap * scale, pad, headerH);

            int w = (int)Math.Ceiling(pad * 2 + Layouts.WidthUnits(rows) * unit);
            int h = (int)Math.Ceiling(headerH + Layouts.HeightUnits(rows) * unit + pad);
            MinimumSize = Size.Empty;
            Size = new Size(w, h); // borderless: window size == client size

            // header controls, right-aligned
            float bh = 26 * scale, by = (headerH - bh) / 2f + 1 * scale, x = w - pad;
            btnHide = new RectangleF(x -= 30 * scale, by, 30 * scale, bh);
            btnMin = new RectangleF(x -= 34 * scale, by, 30 * scale, bh);
            btnMenu = new RectangleF(x -= 34 * scale, by, 30 * scale, bh);
            btnPlus = new RectangleF(x -= 34 * scale, by, 28 * scale, bh);
            btnMinus = new RectangleF(x -= 28 * scale, by, 28 * scale, bh);
            btnLayout = new RectangleF(x -= 88 * scale, by, 80 * scale, bh);
            gripRect = new RectangleF(pad, 0, 110 * scale, headerH);

            var centers = new Dictionary<char, PointF>();
            foreach (var k in keys) if (k.def.cls == KeyCls.Letter) centers[k.def.Letter] = k.Center;
            Ctl.Engine = new SwipeEngine(centers, dict);

            DisposeFonts();
            string face = FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
            fLetter = new Font(face, unit * 0.36f, FontStyle.Regular, GraphicsUnit.Pixel);
            fSym = new Font("Segoe UI Symbol", unit * 0.32f, FontStyle.Regular, GraphicsUnit.Pixel);
            fSmall = new Font(face, unit * 0.235f, FontStyle.Regular, GraphicsUnit.Pixel);
            fTiny = new Font(face, unit * 0.2f, FontStyle.Regular, GraphicsUnit.Pixel);
            fHeader = new Font(face, 12.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            fChip = new Font(face, 14.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            baseKey = null;
            Invalidate();
        }

        static bool FontExists(string name)
        {
            using var f = new Font(name, 10f);
            return f.Name == name;
        }

        void DisposeFonts()
        {
            foreach (var f in new[] { fLetter, fSym, fSmall, fTiny, fHeader, fChip }) f?.Dispose();
        }

        public void SetLayout(string name) { S.layout = name; Ctl.ReleaseAll(); Rebuild(); SettingsStore.Save(S); }

        public void ChangeSize(float delta)
        {
            S.scale = MathF.Round(Math.Clamp(S.scale + delta, 0.6f, 2.2f) * 10f) / 10f;
            var oldBottomCenter = new Point(Left + Width / 2, Bottom);
            Rebuild();
            Location = new Point(oldBottomCenter.X - Width / 2, oldBottomCenter.Y - Height);
            SettingsStore.Save(S);
        }

        // ---------------------------------------------------------------- input
        KeyRect HitKey(PointF p)
        {
            foreach (var k in keys) if (k.cell.Contains(p)) return k;
            return null;
        }

        public KeyRect FindKey(string id) => keys.Find(k => k.def.id == id);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { if (e.Button == MouseButtons.Right) ShowMenu(e.Location); return; }
            Capture = true;
            var p = new PointF(e.X, e.Y);
            if (p.Y < headerH)
            {
                if (btnHide.Contains(p)) { HideRequested?.Invoke(); return; }
                if (btnMin.Contains(p)) { Capture = false; Minimize(); return; }
                if (btnMenu.Contains(p)) { ShowMenu(new Point((int)btnMenu.Left, (int)btnMenu.Bottom)); return; }
                if (btnPlus.Contains(p)) { ChangeSize(+0.1f); return; }
                if (btnMinus.Contains(p)) { ChangeSize(-0.1f); return; }
                if (btnLayout.Contains(p)) { SetLayout(S.layout == Layouts.FullName ? Layouts.CompactName : Layouts.FullName); return; }
                for (int i = 0; i < chipRects.Count; i++)
                    if (chipRects[i].Contains(p)) { Ctl.ChooseSuggestion(i); return; }
                dragging = true;
                dragOffset = new Point(Cursor.Position.X - Left, Cursor.Position.Y - Top);
                return;
            }
            var k = HitKey(p);
            if (k == null) return;
            pointerOnKeys = true;
            Ctl.Down(0, k.def, p, Environment.TickCount64);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                var c = Cursor.Position;
                Native.MoveNoActivate(Handle, c.X - dragOffset.X, c.Y - dragOffset.Y);
                return;
            }
            if (pointerOnKeys) Ctl.Move(0, new PointF(e.X, e.Y), Environment.TickCount64);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            // finish the press before releasing capture: losing capture ends a press at the cursor
            if (dragging)
            {
                dragging = false;
                S.x = Left; S.y = Top;
                SettingsStore.Save(S);
            }
            if (pointerOnKeys)
            {
                pointerOnKeys = false;
                Ctl.Up(0, new PointF(e.X, e.Y), Environment.TickCount64);
            }
            Capture = false;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            // capture stolen (e.g. a system dialog): don't leave a key held down
            if (pointerOnKeys && !Capture)
            {
                pointerOnKeys = false;
                var c = PointToClient(Cursor.Position);
                Ctl.Up(0, new PointF(c.X, c.Y), Environment.TickCount64);
            }
            dragging = false;
        }

        void ShowMenu(Point client) { OptionsMenu?.Show(this, client); }

        void OnTick()
        {
            Ctl.Tick(Environment.TickCount64);
            if (++pollCounter % 12 == 0 && Visible)
            {
                // reflect the real OS lock state (the physical keyboard can change it too)
                string st = $"{Native.ToggleOn(VK.Caps)}{Native.ToggleOn(VK.NumLock)}{Native.ToggleOn(VK.ScrLock)}";
                if (st != lockState) { lockState = st; Invalidate(); }
            }
        }

        // ---------------------------------------------------------------- painting
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            bool caps = Native.ToggleOn(VK.Caps), num = Native.ToggleOn(VK.NumLock), scr = Native.ToggleOn(VK.ScrLock);
            bool shift = Ctl.Mods.ShiftActive;
            string modsKey = $"{Ctl.Mods.State(ModKind.Shift)}{Ctl.Mods.Held(ModKind.Shift)}{Ctl.Mods.State(ModKind.Ctrl)}{Ctl.Mods.Held(ModKind.Ctrl)}{Ctl.Mods.State(ModKind.Alt)}{Ctl.Mods.Held(ModKind.Alt)}{Ctl.Mods.State(ModKind.Win)}{Ctl.Mods.Held(ModKind.Win)}";
            string key = $"{ClientSize}{S.layout}{caps}{num}{scr}{modsKey}";
            if (baseLayer == null || key != baseKey || baseLayer.Size != ClientSize)
            {
                baseLayer?.Dispose();
                baseLayer = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var bg = Graphics.FromImage(baseLayer))
                {
                    Setup(bg);
                    DrawBackground(bg);
                    foreach (var k in keys) DrawKey(bg, k, false, shift, caps, num, scr);
                }
                baseKey = key;
            }
            g.DrawImageUnscaled(baseLayer, 0, 0);
            Setup(g);
            foreach (var k in keys) if (Ctl.IsPressed(k.def)) DrawKey(g, k, true, shift, caps, num, scr);
            DrawHeader(g);
            if (Ctl.Swiping) DrawTrail(g);
        }

        static void Setup(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        void DrawBackground(Graphics g)
        {
            var r = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            using var b = new LinearGradientBrush(r, BgTop, BgBot, 90f);
            g.FillRectangle(b, r);
            using var line = new Pen(Color.FromArgb(14, 255, 255, 255), 1);
            g.DrawLine(line, pad, headerH - 2 * scale, ClientSize.Width - pad, headerH - 2 * scale);
        }

        void DrawKey(Graphics g, KeyRect kr, bool pressed, bool shift, bool caps, bool num, bool scr)
        {
            var k = kr.def;
            var r = kr.rect;
            if (pressed) r = RectangleF.Inflate(r, -1.2f * scale, -1.2f * scale);
            float rad = 7 * scale;
            bool fn = k.cls is KeyCls.Mod or KeyCls.Toggle || (k.cls == KeyCls.Key && k.altVk == 0);
            using var path = Round(r, rad);

            ModifierState.St mst = ModifierState.St.Off;
            bool held = false;
            if (k.cls == KeyCls.Mod) { mst = Ctl.Mods.State(k.mod); held = Ctl.Mods.Held(k.mod); }
            bool locked = mst == ModifierState.St.Locked;
            bool lit = k.cls == KeyCls.Toggle && (k.vk == VK.Caps ? caps : k.vk == VK.NumLock ? num : scr);

            // body
            if (locked)
            {
                using var b = new LinearGradientBrush(r, Accent, Accent2, 35f);
                g.FillPath(b, path);
            }
            else
            {
                using var b = new LinearGradientBrush(r, fn ? FnFill : KeyFill, fn ? FnFillBot : KeyFillBot, 90f);
                g.FillPath(b, path);
            }
            if (pressed || held)
            {
                using var pb = new SolidBrush(Color.FromArgb(pressed ? 120 : 70, Accent));
                g.FillPath(pb, path);
            }
            // top sheen + edge
            using (var edge = new Pen(pressed ? Color.FromArgb(200, Accent) : mst == ModifierState.St.OneShot ? Color.FromArgb(230, Accent) : KeyEdge,
                       mst == ModifierState.St.OneShot || pressed ? 1.6f * scale : 1f))
                g.DrawPath(edge, path);
            if (!locked && !pressed)
            {
                using var sheen = new Pen(Color.FromArgb(18, 255, 255, 255), 1f);
                g.DrawLine(sheen, r.Left + rad, r.Top + 1, r.Right - rad, r.Top + 1);
            }

            // legends
            Color main = locked ? Color.White : CText;
            switch (k.cls)
            {
                case KeyCls.Letter:
                {
                    bool upper = shift ^ caps;
                    DrawText(g, upper ? k.label.ToUpperInvariant() : k.label, fLetter, main, r);
                    break;
                }
                case KeyCls.Symbol:
                {
                    string big = shift ? k.shiftLabel : k.label, small = shift ? k.label : k.shiftLabel;
                    var bigR = new RectangleF(r.X, r.Y + r.Height * 0.14f, r.Width, r.Height * 0.86f);
                    DrawText(g, big, fLetter, main, bigR);
                    var sr = new RectangleF(r.X + 6 * scale, r.Y + 3 * scale, r.Width * 0.5f, r.Height * 0.4f);
                    DrawText(g, small, fTiny, CFaint, sr, StringAlignment.Near);
                    break;
                }
                case KeyCls.Space:
                {
                    using var bar = new Pen(Color.FromArgb(40, 255, 255, 255), 2f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    float cy = r.Bottom - r.Height * 0.3f, cx = r.X + r.Width / 2;
                    g.DrawLine(bar, cx - r.Width * 0.12f, cy, cx + r.Width * 0.12f, cy);
                    break;
                }
                default:
                {
                    if (k.altVk != 0)
                    {
                        DrawText(g, k.label, fLetter, num ? main : CFaint, new RectangleF(r.X, r.Y - r.Height * 0.08f, r.Width, r.Height));
                        DrawText(g, k.altLabel, fTiny, num ? CFaint : CDim, new RectangleF(r.X, r.Y + r.Height * 0.52f, r.Width, r.Height * 0.44f));
                        break;
                    }
                    bool symbolic = k.label.Length > 0 && k.label[0] >= 0x2190;
                    bool isF = k.id.Length >= 2 && k.id[0] == 'f' && char.IsDigit(k.id[1]);
                    var font = symbolic ? fSym : isF ? fSmall : fSmall;
                    DrawText(g, k.label, font, locked ? Color.White : (k.cls == KeyCls.Mod || k.cls == KeyCls.Toggle || !symbolic) ? CDim : CText, r);
                    if (k.cls == KeyCls.Toggle || k.cls == KeyCls.Mod)
                    {
                        bool dot = k.cls == KeyCls.Toggle ? lit : mst != ModifierState.St.Off;
                        float d = 5 * scale;
                        using var db = new SolidBrush(dot ? (locked ? Color.White : Accent) : Color.FromArgb(40, 255, 255, 255));
                        g.FillEllipse(db, r.Right - d - 6 * scale, r.Top + 6 * scale, d, d);
                    }
                    break;
                }
            }
        }

        void DrawHeader(Graphics g)
        {
            // grip + title
            using (var dot = new SolidBrush(Color.FromArgb(90, 160, 170, 190)))
                for (int rr = 0; rr < 2; rr++)
                    for (int c = 0; c < 3; c++)
                        g.FillEllipse(dot, pad + 2 * scale + c * 6 * scale, headerH / 2 - 5 * scale + rr * 6 * scale, 3 * scale, 3 * scale);
            using (var tb = new SolidBrush(CFaint))
                g.DrawString("GlideKey", fHeader, tb, pad + 24 * scale, headerH / 2 - fHeader.Height / 2f);

            // header buttons
            Pill(g, btnLayout, S.layout == Layouts.FullName ? "full" : "compact", false);
            Pill(g, btnMinus, "−", false);
            Pill(g, btnPlus, "+", false);
            Pill(g, btnMenu, "⚙", false, fSym);
            Pill(g, btnMin, "–", false);
            Pill(g, btnHide, "✕", false, fSym);

            // suggestion chips, centred in the free space
            chipRects.Clear();
            var words = Ctl.Strip;
            float left = pad + 110 * scale, right = btnLayout.Left - 10 * scale;
            if (words.Count == 0)
            {
                if (Ctl.Mods.ShortcutActive) DrawText(g, "shortcut mode — letters go to the app as key combos", fTiny, CFaint, new RectangleF(left, 0, right - left, headerH));
                return;
            }
            float h = 28 * scale, y = (headerH - h) / 2f;
            var widths = new List<float>();
            float total = 0;
            int shown = 0;
            foreach (var w in words)
            {
                float cw = g.MeasureString(w, fChip).Width + 22 * scale;
                if (total + cw > right - left) break;
                widths.Add(cw); total += cw + 8 * scale; shown++;
            }
            float x = left + ((right - left) - (total - 8 * scale)) / 2f;
            for (int i = 0; i < shown; i++)
            {
                var r = new RectangleF(x, y, widths[i], h);
                chipRects.Add(r);
                bool first = i == 0;
                using var path = Round(r, h / 2);
                if (first)
                {
                    using var b = new LinearGradientBrush(r, Color.FromArgb(Ctl.StripIsLive ? 90 : 150, Accent), Color.FromArgb(Ctl.StripIsLive ? 90 : 150, Accent2), 0f);
                    g.FillPath(b, path);
                }
                else
                {
                    using var b = new SolidBrush(ChipFill);
                    g.FillPath(b, path);
                }
                DrawText(g, words[i], fChip, first ? Color.White : CText, r);
                x += widths[i] + 8 * scale;
            }
            if (Ctl.StripIsLive) chipRects.Clear(); // live guesses aren't tappable
        }

        void Pill(Graphics g, RectangleF r, string text, bool on, Font f = null)
        {
            using var path = Round(r, 7 * scale);
            using var b = new SolidBrush(on ? Color.FromArgb(120, Accent) : Color.FromArgb(34, 37, 45));
            g.FillPath(b, path);
            using var p = new Pen(Color.FromArgb(18, 255, 255, 255));
            g.DrawPath(p, path);
            DrawText(g, text, f ?? fSmall, CDim, r);
        }

        void DrawTrail(Graphics g)
        {
            var t = Ctl.Engine.Trace;
            int n = t.Count;
            if (n < 2) return;
            int start = Math.Max(1, n - 400);
            for (int i = start; i < n; i++)
            {
                float f = (float)(i - start) / Math.Max(1, n - start);
                int a = (int)(40 + 200 * f);
                var c = Blend(Accent2, Accent, f);
                using var glow = new Pen(Color.FromArgb(a / 5, c), 12 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var core = new Pen(Color.FromArgb(a, c), (2.5f + 2.5f * f) * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(glow, t[i - 1], t[i]);
                g.DrawLine(core, t[i - 1], t[i]);
            }
            using var head = new SolidBrush(Color.FromArgb(230, Accent));
            float hr = 5 * scale;
            g.FillEllipse(head, t[n - 1].X - hr, t[n - 1].Y - hr, 2 * hr, 2 * hr);
        }

        static Color Blend(Color a, Color b, float t)
            => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        static readonly StringFormat CenterFmt = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.None };
        static readonly StringFormat NearFmt = new() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near, FormatFlags = StringFormatFlags.NoWrap };

        static void DrawText(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment align = StringAlignment.Center)
        {
            if (string.IsNullOrEmpty(s)) return;
            using var b = new SolidBrush(c);
            g.DrawString(s, f, b, r, align == StringAlignment.Center ? CenterFmt : NearFmt);
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Renders the current state to a bitmap (used by --render for visual checks).
        public Bitmap Snapshot()
        {
            var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
            DrawToBitmap(bmp, new Rectangle(Point.Empty, ClientSize));
            return bmp;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tick.Dispose(); baseLayer?.Dispose(); DisposeFonts(); }
            base.Dispose(disposing);
        }
    }
}
