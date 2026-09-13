using System;

/// <summary>
/// Covers the log export layout.
///
/// The export replaces the search box as the way a failure leaves the panel, so the
/// header it writes has to explain the file on its own, and a log that does not end in
/// a newline must not merge into whatever a reader appends next.
/// </summary>
public static class LogExportPolicyTests
{
    public static void Run()
    {
        VerifyExportFileName();
        VerifyExportText();
        Console.WriteLine("Log export policy tests passed.");
    }

    private static void VerifyExportFileName()
    {
        string name = LogExportPolicy.BuildDefaultFileName(new DateTime(2026, 9, 13, 11, 25, 30));
        if (name != "dsh-control-panel-20260913-112530.log")
            throw new InvalidOperationException("Unexpected export file name: " + name);
        // The timestamp is what keeps repeated exports from overwriting each other.
        string later = LogExportPolicy.BuildDefaultFileName(new DateTime(2026, 9, 13, 11, 25, 31));
        if (name == later)
            throw new InvalidOperationException("Exports taken a second apart must not share a name.");
    }

    private static void VerifyExportText()
    {
        string text = LogExportPolicy.BuildExportText(
            "11:00:00  开始\r\n11:00:01  完成\r\n",
            @"C:\dsh",
            new DateTime(2026, 9, 13, 11, 25, 30));

        if (text.IndexOf("DeepSeek Harness 控制面板日志", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The export needs a title.");
        if (text.IndexOf("2026-09-13 11:25:30", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The export must record when it was taken.");
        if (text.IndexOf(@"C:\dsh", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The export must record which install the log came from.");
        if (text.IndexOf("11:00:01  完成", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The export must contain the log body.");

        // A missing install root is labelled rather than left blank.
        string noRoot = LogExportPolicy.BuildExportText("body", "", new DateTime(2026, 1, 1));
        if (noRoot.IndexOf("(未设置)", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A missing install root must be labelled.");

        // An empty log still produces a usable file rather than throwing.
        string empty = LogExportPolicy.BuildExportText("", @"C:\dsh", new DateTime(2026, 1, 1));
        if (empty.IndexOf("DeepSeek Harness 控制面板日志", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("An empty log must still export a header.");

        // A body that does not end in a newline must not merge with the next line.
        string noTrailing = LogExportPolicy.BuildExportText("last line", @"C:\dsh", new DateTime(2026, 1, 1));
        if (!noTrailing.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            throw new InvalidOperationException("The export must end with a newline.");
    }
}
