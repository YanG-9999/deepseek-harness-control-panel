using System;

/// <summary>
/// Covers the tray close behaviour.
///
/// The close rule matters because "close" no longer means "stop": a management panel
/// that vanished on close would make its tray icon pointless, while exiting still has
/// to be reachable.
///
/// The auto-start registration this file used to cover is gone. It wrote an HKCU Run
/// entry, was the only feature that persisted a change outside the Harness directories,
/// and was never used: the panel is opened on demand to start and inspect Harness, not
/// left running for Windows to relaunch.
/// </summary>
public static class AutoStartAndTrayPolicyTests
{
    public static void Run()
    {
        VerifyCloseBehaviour();
        VerifyClosePrompt();
        VerifyTooltip();
        Console.WriteLine("Tray close policy tests passed.");
    }

    private static void VerifyCloseBehaviour()
    {
        // Closing the window keeps the panel alive; only an explicit exit ends it.
        if (TrayClosePolicy.Resolve(false) != TrayCloseAction.MinimizeToTray)
            throw new InvalidOperationException("Closing the window must minimize to the tray.");
        if (TrayClosePolicy.Resolve(true) != TrayCloseAction.Exit)
            throw new InvalidOperationException("An explicit exit must actually exit.");

        // The prompt is asked once and then remembered, so it is not noise.
        if (!TrayClosePolicy.ShouldAskOnClose(false))
            throw new InvalidOperationException("The first close must explain what happens.");
        if (TrayClosePolicy.ShouldAskOnClose(true))
            throw new InvalidOperationException("The close prompt must not repeat once answered.");
    }

    private static void VerifyClosePrompt()
    {
        string prompt = TrayClosePolicy.BuildClosePrompt(3080);
        if (prompt.IndexOf("3080", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The prompt must name the port the service uses.");
        if (prompt.IndexOf("托盘", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The prompt must say the panel keeps running in the tray.");
        if (prompt.IndexOf("退出", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The prompt must offer the real exit.");

        // The menu labels must exist. There is deliberately no auto-start entry.
        string[] labels = new[]
        {
            TrayClosePolicy.MenuShow,
            TrayClosePolicy.MenuOpenPage,
            TrayClosePolicy.MenuStart,
            TrayClosePolicy.MenuStop,
            TrayClosePolicy.MenuExit
        };
        foreach (string label in labels)
        {
            if (String.IsNullOrWhiteSpace(label))
                throw new InvalidOperationException("Every tray menu entry needs a label.");
        }
    }

    private static void VerifyTooltip()
    {
        string running = TrayClosePolicy.BuildTooltip(true, 3080, "0.1.5-rc.2");
        if (running.IndexOf("正在运行", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The tooltip must show the running state: " + running);
        if (running.IndexOf("3080", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The tooltip must show the port.");
        if (running.IndexOf("0.1.5-rc.2", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The tooltip must show the version.");

        string stopped = TrayClosePolicy.BuildTooltip(false, 3099, "");
        if (stopped.IndexOf("未运行", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The tooltip must show a stopped service.");

        // NotifyIcon truncates past 63 characters, so the text must fit.
        string longVersion = new string('9', 90);
        string truncated = TrayClosePolicy.BuildTooltip(true, 65535, longVersion);
        if (truncated.Length > 63)
            throw new InvalidOperationException("The tooltip must fit the platform limit, got " + truncated.Length + ".");
    }
}
