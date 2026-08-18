using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

public static class LogViewRenderingTests
{
    public static void Run()
    {
        using (var form = new ManagerForm())
        {
            form.Show();
            Application.DoEvents();

            MethodInfo log = typeof(ManagerForm).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic);
            if (log == null)
                throw new InvalidOperationException("Log method was not found.");
            log.Invoke(form, new object[] { "(!) Some chunks are larger than 500 kB after minification." });
            log.Invoke(form, new object[] { "- use dynamic import() to code-split the application" });
            log.Invoke(form, new object[] { "- Adjust chunk size limit for this warning via build.chunkSizeWarningLimit." });
            log.Invoke(form, new object[] { "https://example.test/docs#section" });
            Application.DoEvents();

            RichTextBox logBox = FindLogBox(form);
            if (logBox == null || !logBox.WordWrap || logBox.ScrollBars != RichTextBoxScrollBars.Vertical)
                throw new InvalidOperationException("Log view must wrap long text and retain a vertical scrollbar.");
            if (logBox.Text.Contains("(!)") || logBox.Text.Contains(Environment.NewLine + "- "))
                throw new InvalidOperationException("Raw build warning prefixes were displayed.");
            if (!logBox.Text.Contains("警告：Some chunks") || !logBox.Text.Contains("    · use dynamic import()") || !logBox.Text.Contains("docs#section"))
                throw new InvalidOperationException("Formatted log content was not displayed.");

            using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log-preview.png"));
            }
            form.Hide();
        }
    }

    private static RichTextBox FindLogBox(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            var logBox = child as RichTextBox;
            if (logBox != null)
                return logBox;
            logBox = FindLogBox(child);
            if (logBox != null)
                return logBox;
        }
        return null;
    }
}
