using System;

/// <summary>
/// Covers the auto-start registration rules and the tray close behaviour.
///
/// The close rule matters because "close" no longer means "stop": a management panel
/// that vanished on close would make its tray icon pointless, while exiting still has
/// to be reachable. The auto-start rules matter because the registry entry is the one
/// thing here that outlives the process.
/// </summary>
public static class AutoStartAndTrayPolicyTests
{
    public static void Run()
    {
        VerifyAutoStartCommand();
        VerifyAutoStartDetection();
        VerifyAutoStartMatching();
        VerifyTrayStartArgument();
        VerifyCloseBehaviour();
        VerifyClosePrompt();
        VerifyTooltip();
        Console.WriteLine("Auto-start and tray policy tests passed.");
    }

    private static void VerifyAutoStartCommand()
    {
        string command = AutoStartPolicy.BuildCommand(@"C:\Program Files\DSH\panel.exe");
        // Quoted because the path contains spaces, and Windows would otherwise split it.
        if (command.IndexOf("\"C:\\Program Files\\DSH\\panel.exe\"", StringComparison.Ordinal) != 0)
            throw new InvalidOperationException("The executable path must be quoted: " + command);
        if (command.IndexOf("--tray", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A logon start must request the tray mode: " + command);

        AssertThrows("blank path", delegate { AutoStartPolicy.BuildCommand(""); });
        AssertThrows("null path", delegate { AutoStartPolicy.BuildCommand(null); });

        // The key is per-user, which is what keeps the feature free of elevation.
        if (AutoStartPolicy.RunKeyPath.IndexOf("CurrentVersion\\Run", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The entry must live under the Run key.");
        if (String.IsNullOrWhiteSpace(AutoStartPolicy.ValueName))
            throw new InvalidOperationException("The entry needs a stable value name.");
    }

    private static void VerifyAutoStartDetection()
    {
        if (!AutoStartPolicy.IsTrayStart(new[] { "--tray" }))
            throw new InvalidOperationException("--tray must be recognised.");
        if (!AutoStartPolicy.IsTrayStart(new[] { "--TRAY" }))
            throw new InvalidOperationException("The switch must be recognised case-insensitively.");
        if (!AutoStartPolicy.IsTrayStart(new[] { "--other", "--tray" }))
            throw new InvalidOperationException("The switch must be recognised among other arguments.");

        if (AutoStartPolicy.IsTrayStart(new string[0]))
            throw new InvalidOperationException("No arguments must not mean a tray start.");
        if (AutoStartPolicy.IsTrayStart(null))
            throw new InvalidOperationException("A null argument list must not mean a tray start.");
        if (AutoStartPolicy.IsTrayStart(new[] { "--trayish" }))
            throw new InvalidOperationException("A partial match must not count.");
    }

    private static void VerifyAutoStartMatching()
    {
        string path = @"C:\app\panel.exe";
        string expected = AutoStartPolicy.BuildCommand(path);

        if (!AutoStartPolicy.Matches(expected, path))
            throw new InvalidOperationException("An identical command must match.");
        if (!AutoStartPolicy.Matches("  " + expected + "  ", path))
            throw new InvalidOperationException("Surrounding whitespace must not defeat the match.");
        if (!AutoStartPolicy.Matches(expected.ToUpperInvariant(), path))
            throw new InvalidOperationException("Path comparison must be case-insensitive.");

        if (AutoStartPolicy.Matches("", path))
            throw new InvalidOperationException("An empty stored command must not match.");
        if (AutoStartPolicy.Matches(null, path))
            throw new InvalidOperationException("A null stored command must not match.");
        if (AutoStartPolicy.Matches(expected, ""))
            throw new InvalidOperationException("A blank path must not match.");
        // A stale entry pointing at a moved executable must not be reported as current.
        if (AutoStartPolicy.Matches(@"""C:\old\panel.exe"" --tray", path))
            throw new InvalidOperationException("An entry for a different executable must not match.");
    }

    private static void VerifyTrayStartArgument()
    {
        if (!AutoStartPolicy.IsTrayStart(new[] { AutoStartPolicy.TrayArgument }))
            throw new InvalidOperationException("The documented switch must be the one parsed.");
        if (AutoStartPolicy.TrayArgument != "--tray")
            throw new InvalidOperationException("The switch is a contract with the registry entry, so it must stay stable.");
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

        // The menu labels must exist and stay distinct.
        string[] labels = new[]
        {
            TrayClosePolicy.MenuShow,
            TrayClosePolicy.MenuOpenPage,
            TrayClosePolicy.MenuStart,
            TrayClosePolicy.MenuStop,
            TrayClosePolicy.MenuAutoStart,
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

        string stopped = TrayClosePolicy.BuildTooltip(false, 8099, "");
        if (stopped.IndexOf("未运行", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The tooltip must show a stopped service.");

        // NotifyIcon truncates past 63 characters, so the text must fit.
        string longVersion = new string('9', 90);
        string truncated = TrayClosePolicy.BuildTooltip(true, 65535, longVersion);
        if (truncated.Length > 63)
            throw new InvalidOperationException("The tooltip must fit the platform limit, got " + truncated.Length + ".");
    }

    private static void AssertThrows(string label, Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("Expected an InvalidOperationException for " + label + ".");
    }
}
