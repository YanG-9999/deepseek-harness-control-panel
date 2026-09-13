using System;
using System.IO;

/// <summary>
/// Covers the crash-report formatter that backs the panel's global exception
/// handlers. The formatter is the only part of the crash path with logic, so it is
/// the only part worth asserting.
/// </summary>
public static class UnexpectedErrorReportTests
{
    public static void Run()
    {
        VerifyBasicReport();
        VerifyInnerChain();
        VerifyChainIsBounded();
        VerifyNullException();
        VerifyMissingStackIsTolerated();
        VerifyReportIsWrittenAndAppended();
        VerifyWriteFailureIsTolerated();
        Console.WriteLine("Unexpected error report tests passed.");
    }

    /// <summary>
    /// The crash path writes a log so a fault that closes the process still leaves
    /// evidence. It also appends, so a repeated fault does not erase the first one.
    /// </summary>
    private static void VerifyReportIsWrittenAndAppended()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dsh-report-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Program.WriteReport(directory, "UI 线程", new InvalidOperationException("first fault"));
            if (String.IsNullOrEmpty(path) || !File.Exists(path))
                throw new InvalidOperationException("A written report must exist on disk.");
            if (Path.GetFileName(path) != Program.ErrorLogFileName)
                throw new InvalidOperationException("The report must use the documented file name, got " + Path.GetFileName(path) + ".");

            string first = File.ReadAllText(path);
            if (first.IndexOf("first fault", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("The report file must contain the fault message.");
            if (first.IndexOf("UI 线程", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("The report file must record where the fault happened.");

            Program.WriteReport(directory, "后台线程", new InvalidOperationException("second fault"));
            string both = File.ReadAllText(path);
            if (both.IndexOf("first fault", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("A later fault must not erase the earlier report.");
            if (both.IndexOf("second fault", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("The later fault must be appended.");
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    /// <summary>
    /// A report that cannot be written must be reported as "not written" rather than
    /// throwing from inside the crash handler.
    /// </summary>
    private static void VerifyWriteFailureIsTolerated()
    {
        if (Program.WriteReport("", "测试", new InvalidOperationException("x")) != "")
            throw new InvalidOperationException("A blank directory must report that nothing was written.");

        // A directory that does not exist and must not be created: AppendAllText
        // raises DirectoryNotFoundException, which the helper has to absorb.
        string missing = Path.Combine(
            Path.GetTempPath(),
            "dsh-report-test-" + Guid.NewGuid().ToString("N"),
            "missing-subdirectory");
        if (Directory.Exists(missing))
            throw new InvalidOperationException("The probe directory must not exist for this test to be meaningful.");
        string result = Program.WriteReport(missing, "测试", new InvalidOperationException("x"));
        if (result != "")
            throw new InvalidOperationException("An unwritable location must report that nothing was written, got: " + result);
        if (Directory.Exists(missing))
            throw new InvalidOperationException("The crash path must not create directories as a side effect.");
    }

    private static void VerifyBasicReport()
    {
        var error = new InvalidOperationException("something broke");
        string report = UnexpectedErrorReport.Format("UI 线程", error);
        if (report.IndexOf("控制面板遇到未预期的错误", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The report must state plainly that something failed.");
        if (report.IndexOf("UI 线程", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The report must name where the fault happened.");
        if (report.IndexOf("System.InvalidOperationException", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The report must name the exception type.");
        if (report.IndexOf("something broke", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The report must carry the exception message.");
    }

    private static void VerifyInnerChain()
    {
        var inner = new ArgumentException("the real cause");
        var outer = new InvalidOperationException("wrapper", inner);
        string report = UnexpectedErrorReport.Format("后台线程", outer);
        if (report.IndexOf("wrapper", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The outer message must appear.");
        if (report.IndexOf("the real cause", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The inner message must appear; a wrapper alone hides the cause.");
        if (report.IndexOf("System.ArgumentException", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The inner exception type must appear.");
        if (report.IndexOf("内部异常", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("Inner exceptions must be labelled as such.");
    }

    private static void VerifyChainIsBounded()
    {
        // Build a chain deeper than the cap; the formatter must stop rather than
        // recurse without bound.
        Exception chain = new InvalidOperationException("depth-0");
        for (int depth = 1; depth <= 20; depth++)
            chain = new InvalidOperationException("depth-" + depth, chain);
        string report = UnexpectedErrorReport.Format("测试", chain);
        if (report.IndexOf("已截断", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("An over-long inner chain must be truncated with a notice.");
        if (report.Length > 20000)
            throw new InvalidOperationException("A truncated report must stay a reasonable size, got " + report.Length + " chars.");
    }

    private static void VerifyNullException()
    {
        // UnhandledExceptionEventArgs can carry an exception object that is not an
        // Exception; the formatter must still produce something usable.
        string report = UnexpectedErrorReport.Format("后台线程", null);
        if (String.IsNullOrWhiteSpace(report))
            throw new InvalidOperationException("A null exception must still produce a report.");
        if (report.IndexOf("(无)", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A null exception must be reported explicitly, not silently.");
    }

    private static void VerifyMissingStackIsTolerated()
    {
        // A freshly constructed exception has no stack trace; the report must not
        // fail or leave a dangling label.
        var error = new InvalidOperationException("no stack yet");
        string report = UnexpectedErrorReport.Format("测试", error);
        if (report.IndexOf("消息: no stack yet", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A message must be reported even without a stack trace.");
    }
}
