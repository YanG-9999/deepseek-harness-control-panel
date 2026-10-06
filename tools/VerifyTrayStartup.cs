using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

public static class VerifyTrayStartup
{
    [STAThread]
    public static int Main()
    {
        Application.EnableVisualStyles();
        int result = 0;
        using (var context = new TrayPanelContext())
        using (var timer = new Timer())
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var panel = (ManagerForm)typeof(TrayPanelContext).GetField("panel", flags).GetValue(context);
            var icon = (NotifyIcon)typeof(TrayPanelContext).GetField("tray", flags).GetValue(context);
            timer.Interval = 300;
            timer.Tick += delegate
            {
                timer.Stop();
                try
                {
                    if (panel.Visible) throw new Exception("Cold startup displayed the panel");
                    Console.WriteLine("PASS: real Application.Run startup stays hidden");
                    Point? shellAnchor = TrayIconLocation.GetAnchor(icon);
                    Console.WriteLine("Shell icon anchor: " + (shellAnchor.HasValue ? shellAnchor.Value.ToString() : "unavailable; fallback used"));
                    var provider = panel.TrayAnchorProvider;
                    panel.TrayAnchorProvider = delegate { return null; };
                    panel.ShowTrayPopup();
                    Rectangle area = Screen.PrimaryScreen.WorkingArea;
                    if (!panel.Visible || Math.Abs(panel.Right - area.Right) > 30 || Math.Abs(panel.Bottom - area.Bottom) > 30)
                        throw new Exception("Shortcut fallback is not bottom-right: " + panel.Bounds);
                    Console.WriteLine("PASS: shortcut fallback bottom-right " + panel.Bounds);
                    panel.Hide();
                    panel.TrayAnchorProvider = provider;
                    panel.ShowTrayPopup();
                    if (shellAnchor.HasValue)
                    {
                        Point anchor = shellAnchor.Value;
                        Rectangle expected = TrayPopupLayout.Bounds(Screen.FromPoint(anchor).WorkingArea,
                            TrayPopupLayout.DpiAt(anchor), false, anchor);
                        if (panel.Bounds != expected) throw new Exception("Shell anchor was ignored");
                    }
                    Console.WriteLine("PASS: shortcut activation uses shell anchor/fallback");
                    Point click = new Point(area.Right - 250, area.Bottom - 40);
                    panel.ShowTrayPopup(click);
                    Rectangle clicked = TrayPopupLayout.Bounds(Screen.FromPoint(click).WorkingArea,
                        TrayPopupLayout.DpiAt(click), false, click);
                    if (panel.Bounds != clicked) throw new Exception("Tray click anchor was ignored");
                    Console.WriteLine("PASS: explicit tray click anchor retained");
                }
                catch (Exception error) { Console.WriteLine(error); result = 1; }
                finally { panel.ExitTray(); }
            };
            timer.Start();
            Application.Run(context);
        }
        return result;
    }
}
