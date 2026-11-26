using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace GlideKey
{
    class Settings
    {
        public string layout { get; set; } = Layouts.CompactName;
        public float scale { get; set; } = 1.0f;
        public int? x { get; set; }
        public int? y { get; set; }
        public bool topmost { get; set; } = true;
        public double opacity { get; set; } = 1.0;
        public bool autoSpace { get; set; } = true;
        public bool doubleSpacePeriod { get; set; } = true;
        public bool autoCapitalize { get; set; } = true;
        public bool smartBackspace { get; set; } = true;
    }

    static class SettingsStore
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlideKey");
        static readonly string File = Path.Combine(Dir, "settings.json");
        public static bool Disabled; // tests never touch the user's settings

        public static Settings Load()
        {
            try { if (System.IO.File.Exists(File)) return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new Settings(); }
            catch { }
            return new Settings();
        }

        public static void Save(Settings s)
        {
            if (Disabled) return;
            try
            {
                Directory.CreateDirectory(Dir);
                System.IO.File.WriteAllText(File, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 0 && args[0] == "--unittest") return Tests.RunUnit(Console.Out);
            if (args.Length > 0 && args[0] == "--smoke") return Tests.RunSmoke(Console.Out);
            if (args.Length > 0 && args[0] == "--selfcheck") return Tests.RunSelfCheck(Console.Out);
            if (args.Length > 1 && args[0] == "--render") return Tests.Render(args[1], args.Length > 2 ? args[2] : Layouts.CompactName);

            using var mutex = new Mutex(true, "GlideKey.SingleInstance.v1", out bool first);
            if (!first)
            {
                // Already running, possibly hidden in the tray with no taskbar button — which is
                // what a pinned-shortcut click lands on. Ask the running copy to show itself.
                Native.PostMessage(Native.HWND_BROADCAST, Native.WM_GLIDEKEY_SHOW, IntPtr.Zero, IntPtr.Zero);
                return 0;
            }

            var settings = SettingsStore.Load();
            var dict = WordDict.FromResource();
            var form = new KeyboardForm(settings, new Injector(), dict);
            var app = new TrayApp(form);

            // never leave a modifier stuck down, whatever happens
            AppDomain.CurrentDomain.UnhandledException += (_, _) => form.Ctl.ReleaseAll();
            Application.ThreadException += (_, e) => { Debug.Log("ui exception " + e.Exception); form.Ctl.ReleaseAll(); };
            Application.ApplicationExit += (_, _) => form.Ctl.ReleaseAll();

            app.ShowKeyboard();
            Application.Run();
            return 0;
        }
    }

    class TrayApp
    {
        readonly KeyboardForm form;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu = new();
        ToolStripMenuItem showItem, compactItem, fullItem, topItem, spaceItem, periodItem, capItem, bkspItem;

        public TrayApp(KeyboardForm form)
        {
            this.form = form;
            BuildMenu();
            form.OptionsMenu = menu;
            form.HideRequested += HideKeyboard;
            form.ShowRequested += ShowKeyboard; // another launch (e.g. the taskbar shortcut)
            form.Icon = LoadIcon();                                        // taskbar button, Alt-Tab
            var trayIcon = LoadIcon(SystemInformation.SmallIconSize.Width); // crisp at the tray's own size
            tray = new NotifyIcon { Icon = trayIcon, Text = "GlideKey — click to show/hide", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Toggle(); };
        }

        void BuildMenu()
        {
            var s = form.S;
            menu.ShowImageMargin = false;
            showItem = new ToolStripMenuItem("Hide keyboard", null, (_, _) => Toggle());
            compactItem = new ToolStripMenuItem("Compact layout", null, (_, _) => form.SetLayout(Layouts.CompactName));
            fullItem = new ToolStripMenuItem("Full layout (numpad)", null, (_, _) => form.SetLayout(Layouts.FullName));

            var size = new ToolStripMenuItem("Size");
            foreach (var (label, v) in new[] { ("Small", 0.8f), ("Medium", 1.0f), ("Large", 1.25f), ("Extra large", 1.5f) })
                size.DropDownItems.Add(label, null, (_, _) => { form.S.scale = v; form.ChangeSize(0); });

            var opacity = new ToolStripMenuItem("Opacity");
            foreach (var v in new[] { 1.0, 0.9, 0.8, 0.7 })
                opacity.DropDownItems.Add($"{v * 100:0}%", null, (_, _) => { form.S.opacity = v; form.Opacity = v; SettingsStore.Save(form.S); });

            topItem = Check("Always on top", () => s.topmost, v => form.SetTopmost(v));
            spaceItem = Check("Space after swiped words", () => s.autoSpace, v => s.autoSpace = v);
            periodItem = Check("Double-tap space types \". \"", () => s.doubleSpacePeriod, v => s.doubleSpacePeriod = v);
            capItem = Check("Capitalize sentence starts", () => s.autoCapitalize, v => s.autoCapitalize = v);
            bkspItem = Check("Backspace deletes whole swiped word", () => s.smartBackspace, v => s.smartBackspace = v);

            menu.Items.AddRange(new ToolStripItem[]
            {
                showItem, new ToolStripSeparator(), compactItem, fullItem, size, opacity, topItem,
                new ToolStripSeparator(), spaceItem, periodItem, capItem, bkspItem,
                new ToolStripSeparator(), new ToolStripMenuItem("Exit GlideKey", null, (_, _) => Exit())
            });
            menu.Opening += (_, _) =>
            {
                showItem.Text = form.Visible && form.WindowState != FormWindowState.Minimized ? "Hide keyboard" : "Show keyboard";
                compactItem.Checked = s.layout == Layouts.CompactName;
                fullItem.Checked = s.layout == Layouts.FullName;
                foreach (var it in new[] { topItem, spaceItem, periodItem, capItem, bkspItem }) ((Action)it.Tag)();
            };
        }

        ToolStripMenuItem Check(string text, Func<bool> get, Action<bool> set)
        {
            var it = new ToolStripMenuItem(text) { CheckOnClick = true };
            it.Tag = (Action)(() => it.Checked = get());
            it.Click += (_, _) => { set(it.Checked); form.ApplySettingsToController(); SettingsStore.Save(form.S); };
            return it;
        }

        public void ShowKeyboard()
        {
            bool first = !form.IsHandleCreated;
            if (first) form.PlaceInitially();
            form.Show();
            form.Restore();
            if (first) form.PlaceInitially(); // again, now that the real DPI (and so the size) is known
            form.SurfaceNoActivate();
        }

        void HideKeyboard()
        {
            form.Ctl.ReleaseAll();
            form.Hide();
        }

        void Toggle()
        {
            if (form.Visible && form.WindowState != FormWindowState.Minimized) HideKeyboard();
            else ShowKeyboard();
        }

        void Exit()
        {
            form.Ctl.ReleaseAll();
            SettingsStore.Save(form.S);
            tray.Visible = false;
            tray.Dispose();
            Application.Exit();
        }

        // assets\glidekey.ico, the same file the exe carries as its Win32 icon. size = 0 keeps every
        // frame (for the window, where Windows picks per use); otherwise the nearest frame is used.
        public static Icon LoadIcon(int size = 0)
        {
            try
            {
                using var s = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("GlideKey.assets.glidekey.ico");
                if (s != null) return size > 0 ? new Icon(s, size, size) : new Icon(s);
            }
            catch (Exception ex) { Debug.Log("icon load failed: " + ex.Message); }
            return SystemIcons.Application;
        }
    }
}
