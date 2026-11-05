// BrightnessMirror - https://github.com/kstawi/brightness-mirror
// Copyright (C) 2026 kstawi
// Licensed under the GNU General Public License v3.0; see the LICENSE file.
//
// Copies the laptop screen's brightness to external monitors over DDC/CI, so the
// Windows brightness slider (Quick Settings, Fn keys, Settings) controls both.
//
// How it works:
//   - Windows sends GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS to registered windows every
//     time the built-in panel's brightness changes. A hidden window listens for it.
//   - A worker thread sends the new level (VCP code 0x10) to every monitor that answers
//     DDC/CI. Rapid changes (dragging the slider) are merged so the monitor is never
//     flooded, and the final value is always applied.
//   - Monitors are re-scanned at start, after a display change, after resume, or when a
//     command fails.
//   - A tray icon shows the state. Left-click pauses/resumes; right-click opens a
//     Windows 11-style menu that follows the system light/dark mode.
//     The paused state is remembered (HKCU\Software\BrightnessMirror\Paused).
//
// Settings: BrightnessMirror.ini next to the exe (re-read automatically when it changes).
// Log:      BrightnessMirror.log next to the exe.
//
// Build (no SDK needed, uses the C# compiler that ships with Windows):
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+
//       /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll
//       /out:BrightnessMirror.exe BrightnessMirror.cs

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BrightnessMirror
{
    static class Program
    {
        public static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\BrightnessMirror-single-instance", out createdNew))
            {
                if (!createdNew) return 0; // already running

                SetProcessDPIAware(); // crisp tray icon and menu on scaled displays
                Application.EnableVisualStyles();
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Crash: " + e.ExceptionObject);
                Application.ThreadException += (s, e) => Log.Write("UI error: " + e.Exception);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

                Log.Write("Started (pid " + System.Diagnostics.Process.GetCurrentProcess().Id + ")");
                var mirror = new Mirror();
                using (var tray = new Tray(mirror))
                {
                    var window = new EventWindow(mirror);
                    mirror.Start();
                    Application.Run(); // message loop; returns after Exit is chosen in the tray menu
                    GC.KeepAlive(window);
                }
                Log.Write("Stopped");
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }

    // Hidden top-level window that receives brightness, resume and display-change notifications.
    sealed class EventWindow : NativeWindow
    {
        const int WM_POWERBROADCAST = 0x0218;
        const int WM_DISPLAYCHANGE = 0x007E;
        const int PBT_APMRESUMESUSPEND = 0x0007;
        const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        const int PBT_POWERSETTINGCHANGE = 0x8013;
        const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

        static Guid GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS = new Guid("8ffee2c6-2d01-46be-adb9-398addc5b4ff");

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

        readonly Mirror mirror;
        readonly DateTime registeredAt;

        public EventWindow(Mirror mirror)
        {
            this.mirror = mirror;
            CreateHandle(new CreateParams { Caption = "BrightnessMirror" });

            // Windows sends the current value right after registering. That one is only
            // recorded, so starting the helper never changes the external monitor on its own.
            registeredAt = DateTime.UtcNow;
            IntPtr reg = RegisterPowerSettingNotification(Handle, ref GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS, DEVICE_NOTIFY_WINDOW_HANDLE);
            if (reg == IntPtr.Zero)
                Log.Write("ERROR: could not subscribe to brightness changes (Win32 error " + Marshal.GetLastWin32Error() + ")");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST)
            {
                long ev = m.WParam.ToInt64();
                if (ev == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
                {
                    // POWERBROADCAST_SETTING: GUID (16 bytes), DWORD DataLength, then the data (a DWORD percent).
                    var setting = (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid));
                    if (setting == GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS)
                    {
                        int percent = Marshal.ReadInt32(m.LParam, 20);
                        if ((DateTime.UtcNow - registeredAt).TotalMilliseconds < 1500)
                        {
                            Log.Write("Laptop brightness is " + percent + "% (initial value, not copied)");
                            mirror.RecordInitial(percent);
                        }
                        else
                        {
                            mirror.SetTarget(percent);
                        }
                    }
                }
                else if (ev == PBT_APMRESUMESUSPEND || ev == PBT_APMRESUMEAUTOMATIC)
                {
                    mirror.Invalidate("resumed from sleep");
                }
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == WM_DISPLAYCHANGE)
                mirror.Invalidate("display configuration changed");

            base.WndProc(ref m);
        }
    }

    // Applies brightness to external monitors on a background thread, merging rapid changes.
    sealed class Mirror
    {
        public enum State { Starting, Ready, Applied, NoMonitor, Failed }

        public sealed class Snapshot
        {
            public State State;
            public int Laptop = -1;
            public int External = -1;
            public int MonitorCount;
        }

        public event Action<Snapshot> StatusChanged; // raised on the worker thread

        const int SettleMs = 2000; // wait after a display change before talking to monitors

        readonly object sync = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly List<Ddc.Monitor> monitors = new List<Ddc.Monitor>(); // worker thread only
        int pending = -1;       // latest laptop brightness not yet applied, or -1
        int lastLaptop = -1;    // latest laptop brightness seen, applied or not
        bool rescanNeeded = true;
        bool paused;
        Snapshot current = new Snapshot { State = State.Starting };
        Config config = Config.Load();

        public Mirror()
        {
            paused = PausedSetting.Load();
            if (paused) Log.Write("Starting paused");
        }

        public void Start()
        {
            var worker = new Thread(Worker) { IsBackground = true, Name = "ddc-worker" };
            worker.Start();
            wake.Set(); // initial scan
        }

        public Snapshot Current
        {
            get { lock (sync) return current; }
        }

        public bool Paused
        {
            get { lock (sync) return paused; }
            set
            {
                lock (sync)
                {
                    if (paused == value) return;
                    paused = value;
                }
                PausedSetting.Save(value);
                Log.Write(value ? "Paused" : "Resumed");
                if (!value) SyncNow();
            }
        }

        public void RecordInitial(int laptopPercent)
        {
            lock (sync) lastLaptop = laptopPercent;
        }

        public void SetTarget(int laptopPercent)
        {
            lock (sync)
            {
                lastLaptop = laptopPercent;
                if (paused) return;
                pending = laptopPercent;
            }
            wake.Set();
        }

        // Copies the last known laptop brightness to the monitors right away.
        public void SyncNow()
        {
            lock (sync)
            {
                if (paused) return;
                if (lastLaptop < 0)
                {
                    Log.Write("Sync skipped: laptop brightness not known yet");
                    return;
                }
                pending = lastLaptop;
            }
            wake.Set();
        }

        public void Invalidate(string reason)
        {
            lock (sync)
            {
                if (!rescanNeeded) Log.Write("Monitors will be re-scanned (" + reason + ")");
                rescanNeeded = true;
            }
            wake.Set();
        }

        void Worker()
        {
            while (true)
            {
                wake.WaitOne();
                while (true)
                {
                    int laptop;
                    bool rescan;
                    lock (sync)
                    {
                        laptop = pending;
                        rescan = rescanNeeded;
                        if (laptop < 0 && !rescan) break;
                        pending = -1;
                        rescanNeeded = false;
                    }

                    try
                    {
                        config = config.ReloadIfChanged();
                        if (rescan)
                        {
                            if (laptop < 0) Thread.Sleep(SettleMs);
                            Scan();
                        }
                        if (laptop >= 0)
                        {
                            if (monitors.Count == 0) Scan();
                            Apply(laptop, config.Map(laptop));
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Write("ERROR: " + e);
                        Report(State.Failed, laptop, -1);
                    }

                    // Give the monitor time to settle; changes arriving meanwhile are merged.
                    Thread.Sleep(config.IntervalMs);
                }
            }
        }

        void Scan()
        {
            foreach (var mon in monitors) mon.Dispose();
            monitors.Clear();
            monitors.AddRange(Ddc.FindMonitors());

            Snapshot prev = Current;
            if (monitors.Count == 0)
            {
                Log.Write("No DDC/CI monitors found");
                Report(State.NoMonitor, prev.Laptop, prev.External);
            }
            else
            {
                Log.Write("DDC/CI monitors: " + string.Join(", ", monitors.Select(x => x.Description + " (max " + x.Max + ")")));
                Report(prev.State == State.Applied ? State.Applied : State.Ready, prev.Laptop, prev.External);
            }
        }

        void Apply(int laptop, int percent)
        {
            if (monitors.Count == 0) return; // Scan already reported NoMonitor

            bool allOk = true;
            foreach (var mon in monitors)
                if (!mon.SetPercent(percent)) allOk = false;

            if (!allOk)
            {
                Log.Write("A monitor did not accept the command; re-scanning and retrying");
                Scan();
                allOk = monitors.Count > 0;
                foreach (var mon in monitors)
                    if (!mon.SetPercent(percent)) allOk = false;
            }

            Log.Write("Laptop " + laptop + "% -> external " + percent + "%" + (allOk ? "" : " (FAILED on at least one monitor)"));
            Report(allOk ? State.Applied : State.Failed, laptop, percent);
        }

        void Report(State state, int laptop, int external)
        {
            Snapshot snap;
            lock (sync)
            {
                snap = new Snapshot { State = state, Laptop = laptop, External = external, MonitorCount = monitors.Count };
                current = snap;
            }
            var handler = StatusChanged;
            if (handler != null) handler(snap);
        }
    }

    // Tray icon: shows the state, left-click toggles pause, right-click opens the menu.
    sealed class Tray : IDisposable
    {
        public static readonly Color OnColor = Color.FromArgb(245, 166, 35);
        public static readonly Color PausedColor = Color.FromArgb(150, 150, 150);
        public static readonly Color ProblemColor = Color.FromArgb(229, 72, 77);

        readonly Mirror mirror;
        readonly NotifyIcon notifyIcon = new NotifyIcon();
        readonly Control invoker = new Control(); // marshals worker-thread updates to the UI thread
        readonly TrayMenu menu;
        readonly Icon iconOn;
        readonly Icon iconPaused;
        readonly Icon iconProblem;
        Mirror.Snapshot shown;

        public Tray(Mirror mirror)
        {
            this.mirror = mirror;
            GC.KeepAlive(invoker.Handle); // create the window handle so BeginInvoke works

            int size = SystemInformation.SmallIconSize.Width;
            iconOn = MakeIcon(size, OnColor, false);
            iconPaused = MakeIcon(size, PausedColor, true);
            iconProblem = MakeIcon(size, ProblemColor, false);

            menu = new TrayMenu(
                TogglePause,
                mirror.SyncNow,
                () => OpenInNotepad(Config.FilePath),
                () => OpenInNotepad(Log.LogPath),
                Exit);

            notifyIcon.ContextMenuStrip = menu.Strip;
            notifyIcon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) TogglePause(); };

            mirror.StatusChanged += snap =>
            {
                try { invoker.BeginInvoke((Action)(() => Show(snap))); }
                catch (InvalidOperationException) { } // shutting down
            };

            Show(mirror.Current);
            notifyIcon.Visible = true;
            Log.Write("Tray icon shown");
        }

        void TogglePause()
        {
            mirror.Paused = !mirror.Paused;
            Show(shown);
        }

        void Show(Mirror.Snapshot s)
        {
            shown = s;
            bool paused = mirror.Paused;
            string line;
            Color color;

            if (paused)
            {
                line = "Paused – click the icon to turn on";
                color = PausedColor;
            }
            else
            {
                switch (s.State)
                {
                    case Mirror.State.Starting:
                        line = "On – looking for external monitors";
                        color = OnColor;
                        break;
                    case Mirror.State.Ready:
                        line = "On – " + s.MonitorCount + " external monitor" + (s.MonitorCount == 1 ? "" : "s") + " ready";
                        color = OnColor;
                        break;
                    case Mirror.State.NoMonitor:
                        line = "No external monitor answers (DDC/CI)";
                        color = ProblemColor;
                        break;
                    case Mirror.State.Failed:
                        line = "Last change failed – see the log";
                        color = ProblemColor;
                        break;
                    default:
                        line = "On – laptop " + s.Laptop + "% → external " + s.External + "%";
                        color = OnColor;
                        break;
                }
            }

            menu.SetStatus(line, color, paused);
            notifyIcon.Icon = paused ? iconPaused : (color == ProblemColor ? iconProblem : iconOn);
            string tip = "Brightness Mirror\n" + line;
            notifyIcon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; // tooltip limit
        }

        void Exit()
        {
            Log.Write("Exit chosen from the tray menu");
            notifyIcon.Visible = false;
            Application.Exit();
        }

        static void OpenInNotepad(string path)
        {
            try { System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\""); }
            catch (Exception e) { Log.Write("Could not open " + path + ": " + e.Message); }
        }

        // A small sun: filled when on, hollow when paused.
        static Icon MakeIcon(int size, Color color, bool hollow)
        {
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            using (var pen = new Pen(color, Math.Max(1.5f, size / 10f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var brush = new SolidBrush(color))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float c = size / 2f;
                float r = size * 0.2f;
                if (hollow) g.DrawEllipse(pen, c - r, c - r, 2 * r, 2 * r);
                else g.FillEllipse(brush, c - r, c - r, 2 * r, 2 * r);

                float r1 = size * 0.33f, r2 = size * 0.46f;
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
                    g.DrawLine(pen, c + cos * r1, c + sin * r1, c + cos * r2, c + sin * r2);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        public void Dispose()
        {
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            invoker.Dispose();
        }
    }

    // The tray's right-click menu, drawn to look like a Windows 11 menu.
    sealed class TrayMenu
    {
        public readonly ContextMenuStrip Strip = new ContextMenuStrip();
        public bool? ForceDark; // null = follow the system setting

        readonly Win11MenuRenderer renderer;
        readonly ToolStripMenuItem statusItem;
        readonly ToolStripMenuItem toggleItem;
        readonly ToolStripMenuItem syncItem;
        readonly float scale;

        public TrayMenu(Action toggle, Action sync, Action editSettings, Action openLog, Action exit)
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            renderer = new Win11MenuRenderer(scale);

            Strip.Renderer = renderer;
            Strip.Font = Fonts.Create(new[] { "Segoe UI Variable Text", "Segoe UI" }, 10f, GraphicsUnit.Point);
            Strip.ShowImageMargin = true;
            Strip.ShowCheckMargin = false;
            Strip.Padding = new Padding(S(4), S(4), S(4), S(4));
            Strip.HandleCreated += (s, e) => { Dwm.RoundCorners(Strip.Handle); ApplyTheme(); };
            Strip.Opening += (s, e) => ApplyTheme();

            statusItem = Item("", null, null);
            statusItem.Enabled = false;
            toggleItem = Item("Pause mirroring", Glyphs.Pause, toggle);
            syncItem = Item("Copy laptop brightness now", Glyphs.Sync, sync);

            Strip.Items.AddRange(new ToolStripItem[]
            {
                statusItem,
                Separator(),
                toggleItem,
                syncItem,
                Separator(),
                Item("Edit settings", Glyphs.Settings, editSettings),
                Item("Open log", Glyphs.Document, openLog),
                Separator(),
                Item("Exit", Glyphs.Close, exit),
            });
            ApplyTheme();
        }

        public void SetStatus(string text, Color dot, bool paused)
        {
            statusItem.Text = text;
            ((MenuGlyph)statusItem.Tag).Dot = dot;
            toggleItem.Text = paused ? "Resume mirroring" : "Pause mirroring";
            ((MenuGlyph)toggleItem.Tag).Char = paused ? Glyphs.Play : Glyphs.Pause;
            syncItem.Enabled = !paused;
            Strip.Invalidate();
        }

        void ApplyTheme()
        {
            renderer.Colors = Palette.Current(ForceDark);
            Strip.BackColor = renderer.Colors.Back;
            if (Strip.IsHandleCreated) Dwm.BorderColor(Strip.Handle, renderer.Colors.Border);
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        ToolStripMenuItem Item(string text, string glyph, Action onClick)
        {
            var item = new ToolStripMenuItem(text)
            {
                Tag = new MenuGlyph { Char = glyph },
                Padding = new Padding(0, S(6), S(4), S(6)),
            };
            if (onClick != null) item.Click += (s, e) => onClick();
            return item;
        }

        ToolStripSeparator Separator()
        {
            return new ToolStripSeparator { AutoSize = false, Height = S(9) };
        }
    }

    sealed class MenuGlyph
    {
        public string Char;  // icon from Segoe Fluent Icons
        public Color? Dot;   // or a colored status dot
    }

    static class Glyphs
    {
        public const string Pause = "";
        public const string Play = "";
        public const string Sync = "";
        public const string Settings = "";
        public const string Document = "";
        public const string Close = "";
    }

    // Windows 11 menu colors for light and dark mode.
    sealed class Palette
    {
        public Color Back, Text, TextSecondary, Hover, Divider, Border;

        static readonly Palette Light = new Palette
        {
            Back = Color.FromArgb(249, 249, 249),
            Text = Color.FromArgb(26, 26, 26),
            TextSecondary = Color.FromArgb(96, 96, 96),
            Hover = Color.FromArgb(234, 234, 234),
            Divider = Color.FromArgb(229, 229, 229),
            Border = Color.FromArgb(224, 224, 224),
        };

        static readonly Palette Dark = new Palette
        {
            Back = Color.FromArgb(44, 44, 44),
            Text = Color.FromArgb(255, 255, 255),
            TextSecondary = Color.FromArgb(190, 190, 190),
            Hover = Color.FromArgb(58, 58, 58),
            Divider = Color.FromArgb(62, 62, 62),
            Border = Color.FromArgb(70, 70, 70),
        };

        public static Palette Current(bool? forceDark)
        {
            bool dark = forceDark.HasValue ? forceDark.Value : !SystemUsesLightTheme();
            return dark ? Dark : Light;
        }

        // Tray menus follow the "Windows mode" setting (taskbar, Start), not the app mode.
        static bool SystemUsesLightTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("SystemUsesLightTheme");
                    return v == null || Convert.ToInt32(v) != 0;
                }
            }
            catch
            {
                return true;
            }
        }
    }

    sealed class Win11MenuRenderer : ToolStripRenderer
    {
        public Palette Colors = Palette.Current(null);
        readonly float scale;
        readonly Font glyphFont;

        public Win11MenuRenderer(float scale)
        {
            this.scale = scale;
            glyphFont = Fonts.Create(new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }, S(16), GraphicsUnit.Pixel);
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Colors.Back))
                e.Graphics.FillRectangle(b, 0, 0, e.ToolStrip.Width, e.ToolStrip.Height);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // Windows draws the rounded border (see Dwm.RoundCorners / BorderColor).
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // No separate icon column background.
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Inset from the menu edges like Windows 11 menus.
            using (var path = Rounded(new RectangleF(S(4), 0, e.Item.Width - S(8), e.Item.Height), S(4)))
            using (var brush = new SolidBrush(Colors.Hover))
                g.FillPath(brush, path);
            g.SmoothingMode = oldMode;
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(Colors.Divider))
                e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Color color = e.Item.Enabled ? Colors.Text : Colors.TextSecondary;
            // Windows 11 layout: icon centered 24px in, text 44px in. The text column the
            // ToolStrip reserves is narrower, but there is spare room on the right to absorb it.
            int textX = Math.Max(e.TextRectangle.X, S(44));
            var textRect = new Rectangle(textX, 0, e.Item.Width - textX, e.Item.Height);
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, textRect, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            var glyph = e.Item.Tag as MenuGlyph;
            if (glyph == null) return;
            var box = new Rectangle(S(14), 0, S(20), e.Item.Height); // icon centered 24px from the edge

            if (glyph.Dot.HasValue)
            {
                int d = S(8);
                var g = e.Graphics;
                var oldMode = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(glyph.Dot.Value))
                    g.FillEllipse(brush, box.X + (box.Width - d) / 2f, box.Y + (box.Height - d) / 2f, d, d);
                g.SmoothingMode = oldMode;
            }
            else if (glyph.Char != null)
            {
                TextRenderer.DrawText(e.Graphics, glyph.Char, glyphFont, box, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    static class Fonts
    {
        // First installed family from the list; Windows' menu font if none is.
        public static Font Create(string[] families, float size, GraphicsUnit unit)
        {
            foreach (var name in families)
            {
                var f = new Font(name, size, FontStyle.Regular, unit);
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
                f.Dispose();
            }
            return new Font(SystemFonts.MenuFont.FontFamily, size, FontStyle.Regular, unit);
        }
    }

    // Rounded corners and border color for the menu window (Windows 11 DWM attributes).
    static class Dwm
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWA_BORDER_COLOR = 34;
        const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void RoundCorners(IntPtr hwnd)
        {
            int v = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int));
        }

        public static void BorderColor(IntPtr hwnd, Color c)
        {
            int v = c.R | (c.G << 8) | (c.B << 16); // COLORREF
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref v, sizeof(int));
        }
    }

    // Monitor access through the DDC/CI functions in dxva2.dll.
    static class Ddc
    {
        const byte VCP_BRIGHTNESS = 0x10;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, IntPtr codeType, out uint current, out uint maximum);
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);

        public sealed class Monitor : IDisposable
        {
            public IntPtr Handle;
            public string Description;
            public uint Max;

            public bool SetPercent(int percent)
            {
                uint raw = (uint)Math.Round(percent * Max / 100.0);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (SetVCPFeature(Handle, VCP_BRIGHTNESS, raw)) return true;
                    Thread.Sleep(50);
                }
                Log.Write("Could not set brightness on " + Description + " (Win32 error " + Marshal.GetLastWin32Error() + ")");
                return false;
            }

            public void Dispose()
            {
                DestroyPhysicalMonitor(Handle);
            }
        }

        // Returns every physical monitor that answers a DDC/CI brightness read.
        // Built-in laptop panels don't speak DDC/CI, so they are skipped automatically.
        public static List<Monitor> FindMonitors()
        {
            var hmonitors = new List<IntPtr>();
            MonitorEnumProc callback = (h, dc, rc, data) => { hmonitors.Add(h); return true; };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);

            var found = new List<Monitor>();
            foreach (var hmon in hmonitors)
            {
                uint count;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out count) || count == 0) continue;
                var physical = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hmon, count, physical)) continue;

                foreach (var pm in physical)
                {
                    uint current, max;
                    if (ReadBrightness(pm.hPhysicalMonitor, out current, out max) && max > 0)
                        found.Add(new Monitor { Handle = pm.hPhysicalMonitor, Description = pm.szPhysicalMonitorDescription, Max = max });
                    else
                        DestroyPhysicalMonitor(pm.hPhysicalMonitor);
                }
            }
            return found;
        }

        static bool ReadBrightness(IntPtr handle, out uint current, out uint max)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (GetVCPFeatureAndVCPFeatureReply(handle, VCP_BRIGHTNESS, IntPtr.Zero, out current, out max)) return true;
                Thread.Sleep(40);
            }
            current = 0;
            max = 0;
            return false;
        }
    }

    // BrightnessMirror.ini: simple key=value lines, ';' starts a comment.
    sealed class Config
    {
        public int MinOutput = 0;    // external brightness (%) when the laptop is at 0%
        public int MaxOutput = 100;  // external brightness (%) when the laptop is at 100%
        public int IntervalMs = 150; // minimum gap between commands to the monitor
        DateTime stamp;

        public static string FilePath { get { return Path.Combine(Program.AppDir, "BrightnessMirror.ini"); } }

        static DateTime CurrentStamp()
        {
            return File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
        }

        public static Config Load()
        {
            var c = new Config { stamp = CurrentStamp() };
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (var rawLine in File.ReadAllLines(FilePath))
                    {
                        string line = rawLine.Split(';')[0].Trim();
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                        int value;
                        if (!int.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) continue;
                        if (key == "minoutput") c.MinOutput = Clamp(value, 0, 100);
                        else if (key == "maxoutput") c.MaxOutput = Clamp(value, 0, 100);
                        else if (key == "intervalms") c.IntervalMs = Clamp(value, 50, 2000);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Write("Could not read settings, using defaults: " + e.Message);
            }
            Log.Write("Settings: MinOutput=" + c.MinOutput + " MaxOutput=" + c.MaxOutput + " IntervalMs=" + c.IntervalMs);
            return c;
        }

        public Config ReloadIfChanged()
        {
            return CurrentStamp() == stamp ? this : Load();
        }

        public int Map(int laptopPercent)
        {
            double v = MinOutput + laptopPercent * (MaxOutput - MinOutput) / 100.0;
            return Clamp((int)Math.Round(v), 0, 100);
        }

        static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }

    // Remembers the paused state between runs.
    static class PausedSetting
    {
        const string KeyPath = @"Software\BrightnessMirror";

        public static bool Load()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                    return key != null && Convert.ToInt32(key.GetValue("Paused", 0)) != 0;
            }
            catch
            {
                return false;
            }
        }

        public static void Save(bool paused)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                    key.SetValue("Paused", paused ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (Exception e)
            {
                Log.Write("Could not save paused state: " + e.Message);
            }
        }
    }

    static class Log
    {
        static readonly object gate = new object();
        public static readonly string LogPath = Path.Combine(Program.AppDir, "BrightnessMirror.log");
        static readonly string OldPath = Path.Combine(Program.AppDir, "BrightnessMirror.old.log");
        const long MaxBytes = 256 * 1024;

        public static void Write(string message)
        {
            lock (gate)
            {
                try
                {
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                    {
                        if (File.Exists(OldPath)) File.Delete(OldPath);
                        File.Move(LogPath, OldPath);
                    }
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine);
                }
                catch
                {
                    // Logging must never take the helper down.
                }
            }
        }
    }
}
