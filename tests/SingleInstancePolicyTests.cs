using System;

/// <summary>
/// Covers the rules a second launch uses to recognise the first instance's window.
/// Getting this wrong is not harmless: an over-eager match would focus an unrelated
/// window, and a miss would leave the user staring at a dialog with nothing
/// happening. The detection is by window class and title because the two processes
/// share no IPC channel.
/// </summary>
public static class SingleInstancePolicyTests
{
    // The real class name WinForms assigns; captured from the running panel.
    private const string PanelClass = "WindowsForms10.Window.8.app.0.34f5582_r6_ad1";
    private const string PanelTitle = "DeepSeek Harness 控制面板";

    public static void Run()
    {
        VerifyWindowClass();
        VerifyMainWindowMatching();
        VerifyMessageMentionsTheConsequence();
        Console.WriteLine("Single instance policy tests passed.");
    }

    private static void VerifyWindowClass()
    {
        if (!SingleInstancePolicy.IsPanelWindowClass(PanelClass))
            throw new InvalidOperationException("A real WinForms window class must be recognised.");
        if (SingleInstancePolicy.IsPanelWindowClass(""))
            throw new InvalidOperationException("An empty class name is not a panel window.");
        if (SingleInstancePolicy.IsPanelWindowClass(null))
            throw new InvalidOperationException("A null class name is not a panel window.");
        if (SingleInstancePolicy.IsPanelWindowClass("WindowsForms10.BUTTON.app.0.34f5582_r6_ad1"))
            throw new InvalidOperationException("A child control class must not match the top-level window class.");
        if (SingleInstancePolicy.IsPanelWindowClass("Notepad"))
            throw new InvalidOperationException("An unrelated window class must not match.");
    }

    private static void VerifyMainWindowMatching()
    {
        if (!SingleInstancePolicy.IsMainPanelWindow(PanelClass, PanelTitle, 760, 560))
            throw new InvalidOperationException("The real panel window must be recognised.");

        // The .NET broadcast window is the same process and class family but is
        // hidden and titled differently; it must never be focused.
        if (SingleInstancePolicy.IsMainPanelWindow(PanelClass, ".NET-BroadcastEventWindow.4.0.0.0.34f5582.0", 760, 560))
            throw new InvalidOperationException("A hidden .NET helper window must not be treated as the panel.");
        if (SingleInstancePolicy.IsMainPanelWindow(PanelClass, PanelTitle, 0, 0))
            throw new InvalidOperationException("A zero-sized window must not be treated as the panel.");
        if (SingleInstancePolicy.IsMainPanelWindow(PanelClass, PanelTitle, 1, 1))
            throw new InvalidOperationException("A 1x1 helper window must not be treated as the panel.");
        if (SingleInstancePolicy.IsMainPanelWindow(PanelClass, "某个别的窗口", 760, 560))
            throw new InvalidOperationException("A different title must not be treated as the panel.");
        if (SingleInstancePolicy.IsMainPanelWindow(PanelClass, "", 760, 560))
            throw new InvalidOperationException("An untitled window must not be treated as the panel.");
        if (SingleInstancePolicy.IsMainPanelWindow("Notepad", PanelTitle, 760, 560))
            throw new InvalidOperationException("A foreign window with a similar title must not match.");

        // The title comparison must tolerate a version or profile suffix.
        if (!SingleInstancePolicy.IsMainPanelWindow(PanelClass, PanelTitle + " — 插件市场", 980, 680))
            throw new InvalidOperationException("A titled variant of the panel must still be recognised.");
    }

    private static void VerifyMessageMentionsTheConsequence()
    {
        // The message must explain why a second launch is refused, not just that it is.
        string message = SingleInstancePolicy.AlreadyRunningMessage;
        if (message.IndexOf("3080", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The message must name the shared resource at stake.");
        if (message.IndexOf("覆盖", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The message must explain that state would be overwritten.");
        if (message.IndexOf("已经在运行", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The message must state plainly that the panel is already running.");
    }
}
