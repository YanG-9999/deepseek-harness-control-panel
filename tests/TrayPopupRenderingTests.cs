using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

/// <summary>
/// Renders the tray popup exactly as the panel draws it, and saves the bitmap the
/// README shows. Keeping the picture generated means the documented UI cannot drift
/// away from the real one: a layout mistake shows up in the image as well as here.
/// </summary>
public static class TrayPopupRenderingTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, ref FormatRange range);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PrintRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FormatRange
    {
        public IntPtr Hdc, Target;
        public PrintRect Area, Page;
        public int Start, End;
    }

    private static void PrintLog(RichTextBox log, Bitmap image)
    {
        using (var native = new Bitmap(log.Width, log.Height))
        using (Graphics graphics = Graphics.FromImage(native))
        {
            graphics.Clear(log.BackColor);
            float dpi;
            using (Graphics controlGraphics = log.CreateGraphics()) dpi = controlGraphics.DpiY;
            var area = new PrintRect { Right = (int)(log.Width * 1440 / dpi), Bottom = (int)(log.Height * 1440 / dpi) };
            IntPtr hdc = graphics.GetHdc();
            var range = new FormatRange { Hdc = hdc, Target = hdc, Area = area, Page = area, Start = 0, End = -1 };
            try { SendMessage(log.Handle, 0x439, new IntPtr(1), ref range); }
            finally { SendMessage(log.Handle, 0x439, IntPtr.Zero, IntPtr.Zero); graphics.ReleaseHdc(hdc); }
            int ink = 0;
            for (int y = 0; y < native.Height; y++)
                for (int x = 0; x < native.Width; x++)
                    if (native.GetPixel(x, y).R < 180) ink++;
            if (ink < 100) throw new InvalidOperationException("Native log preview is unexpectedly blank.");
            using (Graphics target = Graphics.FromImage(image)) target.DrawImageUnscaled(native, log.Location);
        }
    }
    /// <summary>The popup's design size in 96 DPI pixels.</summary>
    public const int Width = 520;
    public const int Height = 728;

    public static void Run()
    {
        using (var host = new Form())
        using (var log = new RichTextBox())
        using (var view = new TrayPopupView(log))
        {
            host.AutoScaleMode = AutoScaleMode.None;
            host.FormBorderStyle = FormBorderStyle.None;
            host.StartPosition = FormStartPosition.Manual;
            // Far off-screen: the render must not flash over the user's desktop.
            host.Location = new Point(-20000, -20000);
            host.ClientSize = new Size(Width, Height);
            view.Dock = DockStyle.Fill;
            host.Controls.Add(view);
            host.Show();

            view.ApplyScale(1F);
            view.Sync(
                new HarnessStatusSnapshot(true, true, true, true, false, "0.1.6-alpha.2"),
                @"C:\dsh", "停止", true, true, true, true, true, true, true, "");
            view.SetBalance("1,250.80 元", "可用余额 · 更新于 10:48");
            log.Text =
                "10:48:21  Service started successfully." + Environment.NewLine +
                "10:48:21  Listening on 127.0.0.1:3080" + Environment.NewLine +
                "10:48:25  Harness ready." + Environment.NewLine +
                "10:48:33  Open browser: http://127.0.0.1:3080" + Environment.NewLine +
                "10:49:02  Checking for updates..." + Environment.NewLine +
                "10:49:03  Current version is up to date." + Environment.NewLine +
                "10:49:15  Scan completed. No issues found." + Environment.NewLine;
            Application.DoEvents();

            using (var image = new Bitmap(host.ClientSize.Width, host.ClientSize.Height))
            {
                view.DrawToBitmap(image, view.ClientRectangle);
                PrintLog(log, image);
                image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tray-preview.png"));
            }
            host.Hide();
        }
        Console.WriteLine("Tray popup rendering tests passed.");
        using (var log = new RichTextBox())
        using (var view = new TrayPopupView(log))
        {
            view.Size = new Size(650, 910);
            view.ApplyScale(1.25F);
            view.Sync(new HarnessStatusSnapshot(true, true, true, true, false, "0.1.6-alpha.2"),
                @"C:\dsh", "停止", true, true, true, true, true, true, true, "");
            view.SetBalance("1,250.80 元", "可用余额 · 更新于 10:48");
            log.Text = "10:48:21  Service started successfully.\n10:48:21  Listening on 127.0.0.1:3080\n10:48:25  Harness ready.\n10:48:33  Open browser: http://127.0.0.1:3080\n10:49:02  Checking for updates...\n10:49:03  Current version is up to date.\n10:49:15  Scan completed. No issues found.";
            using (var image = new Bitmap(view.Width, view.Height))
            {
                view.DrawToBitmap(image, view.ClientRectangle);
                PrintLog(log, image);
                image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview-125.png"));
            }
        }
        using (var log = new RichTextBox())
        using (var view = new TrayPopupView(log))
        {
            view.Size = new Size(780, 1092);
            view.ApplyScale(1.5F);
            view.Sync(new HarnessStatusSnapshot(true, true, true, true, false, "0.1.6-alpha.2"),
                @"C:\dsh", "启动", true, true, true, true, true, true, true, "");
            view.SetBalance("13.19 元", "可用余额 · 更新于 08:03");
            log.Text = "08:03:55  [INFO]  状态检测完成：已安装，未运行。";
            using (var image = new Bitmap(view.Width, view.Height))
            {
                view.DrawToBitmap(image, view.ClientRectangle);
                PrintLog(log, image);
                image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview-150.png"));
            }
        }
    }
}
