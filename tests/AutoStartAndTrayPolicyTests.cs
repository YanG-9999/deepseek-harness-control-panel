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
        VerifyRememberedAnswer();
        VerifyTooltip();
        Console.WriteLine("Tray close policy tests passed.");
    }

    private static void VerifyCloseBehaviour()
    {
        // Closing the window never stops Harness, and the panel only goes away if the user
        // says so. Which of the two the window's X does is the user's answer, remembered
        // across launches, so the only rule left in the policy is that it is asked once.
        if (!TrayClosePolicy.ShouldAskOnClose(false))
            throw new InvalidOperationException("The first close must explain what happens.");
        if (TrayClosePolicy.ShouldAskOnClose(true))
            throw new InvalidOperationException("The close prompt must not repeat once answered.");
    }

    /// <summary>
    /// The remembered answer has to survive a restart, which is the whole point: the old
    /// in-memory flag made "asked once" mean "asked once per launch", so anyone who closes
    /// the panel and opens it again was asked again.
    /// </summary>
    private static void VerifyRememberedAnswer()
    {
        TrayCloseAction action;
        if (!TrayClosePolicy.TryParseSettingValue(
            TrayClosePolicy.ToSettingValue(TrayCloseAction.MinimizeToTray), out action) ||
            action != TrayCloseAction.MinimizeToTray)
            throw new InvalidOperationException("A remembered 'minimize to tray' must be read back as itself.");
        if (!TrayClosePolicy.TryParseSettingValue(
            TrayClosePolicy.ToSettingValue(TrayCloseAction.Exit), out action) ||
            action != TrayCloseAction.Exit)
            throw new InvalidOperationException("A remembered exit must be read back as an exit.");
        if (!TrayClosePolicy.TryParseSettingValue("  EXIT  ", out action) || action != TrayCloseAction.Exit)
            throw new InvalidOperationException("The stored value must be read without regard to case or padding.");

        // Anything unrecognised means "not answered yet": asking is safer than guessing.
        foreach (string stored in new[] { null, "", "   ", "sometimes", "trayy" })
        {
            if (TrayClosePolicy.TryParseSettingValue(stored, out action))
                throw new InvalidOperationException("'" + stored + "' must not count as an answer.");
        }
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

        // The answers the text names have to be the answers the dialog shows. The old
        // prompt described two buttons that did not exist, next to 是 and 否.
        if (prompt.IndexOf(TrayClosePolicy.ClosePromptTrayAnswer, StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The prompt must name the tray answer the dialog offers.");
        if (prompt.IndexOf(TrayClosePolicy.ClosePromptExitAnswer, StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The prompt must name the exit answer the dialog offers.");
        if (TrayClosePolicy.ClosePromptTrayAnswer == TrayClosePolicy.ClosePromptExitAnswer)
            throw new InvalidOperationException("The two answers need distinct labels.");
        if (String.IsNullOrWhiteSpace(TrayClosePolicy.ClosePromptTitle))
            throw new InvalidOperationException("The close prompt needs a title.");

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
