// BrightnessMirror - https://github.com/kstawi/brightness-mirror
// Copyright (C) 2026 kstawi
// Licensed under the GNU General Public License v3.0; see the LICENSE file.
//
// Development aid: draws the app icon (the tray's amber sun) at the sizes Windows uses and
// writes assets\app.ico. Built and run by build.ps1 -Icon; the result is committed.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace BrightnessMirror
{
    static class MakeIconTool
    {
        static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

        static int Main(string[] args)
        {
            string outPath = args.Length > 0 ? args[0] : "app.ico";
            var images = new byte[Sizes.Length][];
            for (int i = 0; i < Sizes.Length; i++)
            {
                using (var bmp = Tray.DrawSun(Sizes[i], Tray.OnColor, false))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    images[i] = ms.ToArray();
                }
            }

            // ICO file: header, one directory entry per size, then the PNG images.
            using (var w = new BinaryWriter(File.Create(outPath)))
            {
                w.Write((ushort)0);            // reserved
                w.Write((ushort)1);            // type: icon
                w.Write((ushort)Sizes.Length);
                int offset = 6 + 16 * Sizes.Length;
                for (int i = 0; i < Sizes.Length; i++)
                {
                    w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i])); // 0 means 256
                    w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
                    w.Write((byte)0);          // palette size
                    w.Write((byte)0);          // reserved
                    w.Write((ushort)1);        // color planes
                    w.Write((ushort)32);       // bits per pixel
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var image in images) w.Write(image);
            }
            Console.WriteLine("Wrote " + outPath);
            return 0;
        }
    }
}
