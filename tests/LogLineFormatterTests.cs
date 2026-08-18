using System;

public static class LogLineFormatterTests
{
    public static void Run()
    {
        AssertLine("(!) Some chunks are larger than 500 kB after minification.", "警告：Some chunks are larger than 500 kB after minification.", LogMessageKind.Warning);
        AssertLine("- use dynamic import() to code-split the application", "    · use dynamic import() to code-split the application", LogMessageKind.Detail);
        AssertLine("# build details", "信息：build details", LogMessageKind.Normal);
        AssertLine("https://example.test/docs#section", "https://example.test/docs#section", LogMessageKind.Normal);
        AssertLine("> pnpm run build", "执行：pnpm run build", LogMessageKind.Command);
    }

    private static void AssertLine(string source, string expectedText, LogMessageKind expectedKind)
    {
        FormattedLogLine actual = LogLineFormatter.Format(source);
        if (!String.Equals(actual.Text, expectedText, StringComparison.Ordinal) || actual.Kind != expectedKind)
            throw new InvalidOperationException("Unexpected formatted log line: " + source);
    }
}
