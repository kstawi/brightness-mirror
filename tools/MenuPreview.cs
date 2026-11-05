// BrightnessMirror - https://github.com/kstawi/brightness-mirror
// Copyright (C) 2026 kstawi
// Licensed under the GNU General Public License v3.0; see the LICENSE file.
//
// Development aid: shows the tray menu over a plain backdrop, screenshots it, and exits.
// Used to make the screenshots in docs/. Build from the repo root together with the app source:
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /main:BrightnessMirror.MenuPreview
//       /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll
//       /out:build\MenuPreview.exe src\BrightnessMirror.cs tools\MenuPreview.cs
// Usage: MenuPreview.exe <out.png> <light|dark> <on|paused> [index of item to highlight]

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BrightnessMirror
{
    static class MenuPreview
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();

            string outPath = args[0];
            bool dark = args[1] == "dark";
            bool paused = args[2] == "paused";
            int hover = args.Length > 3 ? int.Parse(args[3]) : -1;

            Action none = () => { };
            var menu = new TrayMenu(none, none, none, none, none) { ForceDark = dark };
            menu.SetStatus(
                paused ? "Paused – click the icon to turn on" : "On – laptop 40% → external 40%",
                paused ? Tray.PausedColor : Tray.OnColor,
                paused);
            menu.Strip.AutoClose = false;

            var origin = new Point(2900, 900);
            var backdrop = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                TopMost = true,
                BackColor = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243),
                Bounds = new Rectangle(origin.X - 40, origin.Y - 40, 440, 440),
            };

            backdrop.Shown += (s, e) =>
            {
                menu.Strip.Show(origin);
                if (hover >= 0) menu.Strip.Items[hover].Select();
                var timer = new Timer { Interval = 700 };
                timer.Tick += (s2, e2) =>
                {
                    timer.Stop();
                    var b = menu.Strip.Bounds;
                    b.Inflate(24, 24);
                    using (var bmp = new Bitmap(b.Width, b.Height))
                    {
                        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                        bmp.Save(outPath, ImageFormat.Png);
                    }
                    menu.Strip.Close();
                    backdrop.Close();
                };
                timer.Start();
            };

            Application.Run(backdrop);
        }
    }
}
