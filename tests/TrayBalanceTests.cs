using System;
using System.Drawing;
using System.Windows.Forms;

public static class TrayBalanceTests
{
    public static void Run()
    {
        BalanceResult result = DeepSeekBalanceClient.Parse("{\"is_available\":true,\"balance_infos\":[{\"currency\":\"CNY\",\"total_balance\":\"1250.80\"},{\"currency\":\"USD\",\"total_balance\":\"5.31\"}]}");
        Require(result.Amount == "1,250.80 元 / 5.31 USD" && result.Available, "Keep currency balances separate.");
        result = DeepSeekBalanceClient.Parse("{\"is_available\":false,\"balance_infos\":[{\"currency\":\"CNY\",\"total_balance\":\"0\"}]}");
        Require(result.Amount == "0.00 元" && !result.Available, "A real zero is distinct from query failure.");
        foreach (string bad in new[] { "{}", "{\"is_available\":true,\"balance_infos\":[]}",
            "{\"is_available\":true,\"balance_infos\":[{\"currency\":\"CNY\",\"total_balance\":\"oops\"}]}" })
        {
            bool rejected = false;
            try { DeepSeekBalanceClient.Parse(bad); } catch (BalanceQueryException) { rejected = true; }
            Require(rejected, "Malformed balances must not become a false zero.");
        }
        string encrypted = StateSecretProtection.Protect("synthetic-test-key");
        Require(!encrypted.Contains("synthetic-test-key") && StateSecretProtection.Unprotect(encrypted) == "synthetic-test-key", "DPAPI roundtrip");
        foreach (float dpi in new[] { 96F, 120F, 144F, 192F })
        {
            var area = new Rectangle(-1920, 0, 1920, 1040);
            Rectangle bounds = TrayPopupLayout.Bounds(area, dpi, true);
            Require(area.Contains(bounds), "Expanded popup must stay in monitor working area.");
            float expected = TrayPopupLayout.Scale(area, dpi);
            Require(bounds.Width == Math.Round(520 * expected), "Popup width must follow the DPI scale.");
            Point trayPoint = new Point(area.Right - 500, area.Bottom + 20);
            Rectangle anchored = TrayPopupLayout.Bounds(area, dpi, false, trayPoint);
            Require(area.Contains(anchored), "Anchored popup must stay in monitor working area.");
            Require(anchored.Right < area.Right - 20, "A tray popup should follow an overflow icon instead of the screen edge.");
            Require(anchored.Bottom <= area.Bottom, "Anchored popup must end above the taskbar.");
            using (var log = new RichTextBox())
            using (var view = new TrayPopupView(log))
            {
                view.Size = bounds.Size;
                view.ApplyScale(expected);
                view.Sync(new HarnessStatusSnapshot(true, true, true, true, false, "0.1.6-alpha.2"),
                    "C:\\dsh", "停止", true, true, true, true, true, true, true, "");
                var placed = new System.Collections.Generic.List<Control>();
                foreach (Control control in view.Controls)
                {
                    Require(view.ClientRectangle.Contains(control.Bounds), "Outside popup at " + dpi + ": " + control.Name);
                    var button = control as TrayPopupButton;
                    if (button != null)
                    {
                        Size measure = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
                            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                        int iconAndGap = (int)((button.IconSize + button.IconGap) * expected);
                        Require(measure.Width + iconAndGap <= button.Width || button.Text.Length == 0,
                            "Button text clipped at " + dpi + ": " + button.Name);
                        Require(measure.Height <= button.Height, "Button height clipped: " + button.Name);
                    }
                    else if (control is Label && !control.Name.StartsWith("value") && control.Name != "amount")
                    {
                        Size measure = TextRenderer.MeasureText(control.Text, control.Font);
                        Require(measure.Height <= control.Height, "Label height clipped at " + dpi + ": " + control.Name + " " + measure + " bounds=" + control.Bounds);
                        Require(measure.Width <= control.Width, "Label width clipped at " + dpi + ": " + control.Name);
                    }
                    // The reference layout is a stack of full-width rows: two interactive
                    // fields overlapping each other is a clipped mock-up, not a style.
                    if (control is Label || control is TrayPopupButton || control is RichTextBox)
                    {
                        foreach (Control other in placed)
                        {
                            Require(!control.Bounds.IntersectsWith(other.Bounds),
                                "Overlapping controls at " + dpi + ": " + control.Name + " / " + other.Name);
                        }
                        placed.Add(control);
                    }
                }
            }
        }
        VerifyBalanceDialog();
        VerifyReferenceLayout();
        VerifyFontFacesAndIcons();
        Console.WriteLine("Tray layout and DeepSeek balance tests passed (100/125/150/200% DPI).");
    }

    private static void VerifyReferenceLayout()
    {
        foreach (float scale in new[] { 1F, 1.25F, 1.5F, 2F })
        using (var log = new RichTextBox())
        using (var view = new TrayPopupView(log))
        {
            view.Size = new Size((int)(520 * scale), (int)(728 * scale));
            view.ApplyScale(scale);
            view.Sync(new HarnessStatusSnapshot(true, true, true, true, false, "0.1.6-alpha.2"),
                "C:\\dsh", "停止", true, true, true, true, true, true, true, "");
            view.SetBalance("13.19 元", "可用余额");
            string[] actions = { "primary", "restart", "page", "update", "more" };
            int previousRight = 0;
            foreach (string key in actions)
            {
                Control button = view.Controls[key];
                Require(button.Width == Math.Round(88 * scale), "Reference button width: " + key);
                Require(button.Height == Math.Round(44 * scale), "Reference button height: " + key);
                Require(Math.Abs(button.Top - 298 * scale) <= 0.5, "Reference single row: " + key);
                if (previousRight != 0) Require(Math.Abs(button.Left - previousRight - 12 * scale) <= 1, "Reference button gap");
                previousRight = button.Right;
            }
            Require(view.Controls["expand"].Size == new Size((int)(60 * scale), (int)(32 * scale)), "Reference expand control");
            Require(view.Controls["exit"].Bottom == view.Height - (int)(20 * scale), "Reference bottom inset");
            string[] fontKeys = { "title", "tagline", "balanceTitle", "amount", "amountUnit", "balanceHint", "caption0", "caption1", "caption2", "caption3", "value0", "value1", "value2", "value3", "primary", "restart", "page", "update", "more", "logTitle", "expand", "exit", "footer" };
            int[] fontSizes = { 22, 14, 16, 36, 18, 14, 14, 14, 14, 14, 16, 16, 16, 16, 15, 15, 15, 15, 15, 18, 14, 16, 14 };
            int[] fontWeights = { 600, 400, 500, 700, 500, 400, 400, 400, 400, 400, 500, 500, 500, 500, 500, 500, 500, 500, 500, 600, 400, 500, 500 };
            for (int i = 0; i < fontKeys.Length; i++)
            {
                Control control = view.Controls[fontKeys[i]];
                Require(control.Font.Unit == GraphicsUnit.Pixel && Math.Abs(control.Font.Size - fontSizes[i] * scale) < 0.01, "Reference font: " + fontKeys[i]);
                Require(control.Font.Name == "DSH Tray UI " + fontWeights[i], "Real bundled weight: " + fontKeys[i]);
                var button = control as TrayPopupButton;
                if (button != null)
                {
                    int expectedIcon = fontKeys[i] == "primary" || fontKeys[i] == "restart" || fontKeys[i] == "page" || fontKeys[i] == "update" || fontKeys[i] == "more" ? 18 : 20;
                    Require(button.IconSize == expectedIcon, "Reference action icon: " + fontKeys[i]);
                }
            }
            foreach (string key in new[] { "close" })
                Require(((TrayPopupButton)view.Controls[key]).IconSize == 20, "Reference close icon: " + key);
            Require(((TrayPopupButton)view.Controls["balance"]).IconSize == 20, "Reference balance refresh icon");
            Require(!view.Controls["amount"].Bounds.IntersectsWith(view.Controls["amountUnit"].Bounds), "Separate amount and unit");
            Require(log.WordWrap && log.ScrollBars == RichTextBoxScrollBars.Vertical, "Readable wrapped logs");
            log.Text = "First line\nSecond line\nThird line";
            IntPtr handle = log.Handle;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            view.GetType().GetMethod("ApplyLogLineHeight", flags).Invoke(view, null);
            int lineHeight = log.GetPositionFromCharIndex(11).Y - log.GetPositionFromCharIndex(0).Y;
            Require(Math.Abs(lineHeight - 22 * scale) <= 1, "Reference 22px log line height: " + lineHeight);
            var menu = (ContextMenuStrip)view.GetType().GetField("moreMenu", flags).GetValue(view);
            string lastAction = "";
            view.Action += delegate(string action) { lastAction = action; };
            string[] expected = { "folder", "scan", "update", "uninstall", "", "settings" };
            for (int i = 0; i < menu.Items.Count; i++)
            {
                if (menu.Items[i] is ToolStripSeparator) continue;
                menu.Items[i].PerformClick();
                Require(lastAction == expected[i], "Reference menu action order");
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetTextFace(IntPtr hdc, int count, System.Text.StringBuilder face);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr value);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

    private static void VerifyFontFacesAndIcons()
    {
        long previousInk = 0;
        foreach (int weight in new[] { 400, 500, 600, 700 })
        using (Font font = TrayPopupFonts.Create(36, weight))
        using (var bitmap = new Bitmap(480, 80))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = graphics.GetHdc(), handle = font.ToHfont();
            IntPtr old = SelectObject(hdc, handle);
            var face = new System.Text.StringBuilder(128);
            GetTextFace(hdc, face.Capacity, face);
            SelectObject(hdc, old); DeleteObject(handle); graphics.ReleaseHdc(hdc);
            Require(face.ToString() == "DSH Tray UI " + weight, "GDI must not fall back: " + face);
            graphics.Clear(Color.White);
            TextRenderer.DrawText(graphics, "启动 重启 DeepSeek 1250", font, new Point(0, 0), Color.Black,
                Color.White, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            long ink = 0;
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++) ink += 255 - bitmap.GetPixel(x, y).R;
            Require(ink > previousInk, "Each weight must render genuinely heavier outlines: " + weight);
            previousInk = ink;
        }
        foreach (string glyph in new[] { "start", "stop", "refresh", "page", "update", "more", "log", "expand", "exit", "settings", "close" })
        using (var bitmap = new Bitmap(120, 120))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            // Four-times render measures visible artwork, not just its outer box.
            if (glyph == "start" || glyph == "refresh") continue;
            PopupArt.Draw(graphics, glyph, new Rectangle(20, 20, 80, 80), Color.Black);
            int left = 120, top = 120, right = -1, bottom = -1;
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                    if (bitmap.GetPixel(x, y).A > 64)
                    { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
            int extent = Math.Max(right - left + 1, bottom - top + 1);
            // Artwork is now fitted to one uniform square canvas.  Individual
            // glyphs intentionally keep their native interior whitespace, so
            // their visible ink is not expected to fill the entire 80px box.
            Require(extent >= 50 && extent <= 86, "Visible icon extent: " + glyph + " " + extent);
            // Anti-aliased round caps can extend by at most two pixels beyond
            // the mathematical box; that is still safely inside the button.
            Require(left >= 18 && top >= 18 && right <= 101 && bottom <= 101, "Icon ink stays in its box: " + glyph);
        }
    }

    /// <summary>
    /// The balance dialog follows the monitor DPI itself, so the checks below run at
    /// whatever scale this machine reports: every label must fit its box and no two
    /// controls may overlap, which is what a clipped dialog looks like in practice.
    /// </summary>
    public static void VerifyBalanceDialog()
    {
        using (var dialog = new BalanceSettingsForm())
        {
            var placed = new System.Collections.Generic.List<Control>();
            foreach (Control control in dialog.Controls)
            {
                Require(dialog.ClientRectangle.Contains(control.Bounds),
                    "Dialog control outside the client area: " + control.Text);
                var label = control as Label;
                if (label != null)
                {
                    Size measure = TextRenderer.MeasureText(label.Text, label.Font,
                        new Size(label.Width, Int32.MaxValue), TextFormatFlags.WordBreak);
                    Require(measure.Height <= label.Height,
                        "Dialog label needs " + measure.Height + "px but has " + label.Height + ": " + label.Text);
                }
                var button = control as Button;
                if (button != null && button.Text.Length > 0)
                {
                    Size measure = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    int padded = button.FlatStyle == FlatStyle.Flat && button.TextAlign == ContentAlignment.MiddleLeft ? 8 : 14;
                    Require(measure.Width + padded <= button.Width,
                        "Dialog button text clipped: '" + button.Text + "' needs " + (measure.Width + padded) + "px, has " + button.Width);
                }
                foreach (Control other in placed)
                    Require(!control.Bounds.IntersectsWith(other.Bounds),
                        "Overlapping dialog controls: '" + control.Text + "' and '" + other.Text + "'");
                placed.Add(control);
            }
            Console.WriteLine("Balance dialog checks passed at scale for this monitor.");
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
