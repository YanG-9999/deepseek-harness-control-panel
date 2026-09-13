using System;
using System.Collections.Generic;

/// <summary>
/// Covers log search matching, hit navigation, and the export layout.
///
/// The matching rules matter because a search box that reports hits is trusted: an
/// off-by-one in the index or a double-counted overlap sends the user to the wrong
/// line, and an empty needle that "matches everything" reports a meaningless total.
/// </summary>
public static class LogSearchPolicyTests
{
    public static void Run()
    {
        VerifyMatching();
        VerifyEmptyNeedle();
        VerifyHitNavigation();
        VerifyHitNumber();
        VerifyExportFileName();
        VerifyExportText();
        Console.WriteLine("Log search policy tests passed.");
    }

    private static void VerifyMatching()
    {
        List<int> matches = LogSearchPolicy.FindMatches("abcABCabc", "abc");
        if (matches.Count != 3)
            throw new InvalidOperationException("Three occurrences must be found, got " + matches.Count + ".");
        if (matches[0] != 0 || matches[1] != 3 || matches[2] != 6)
            throw new InvalidOperationException("Match offsets must be exact, got " + String.Join(",", matches.ConvertAll(i => i.ToString()).ToArray()) + ".");
        if (!LogSearchPolicy.FindMatches("abcABC", "abc").Count.Equals(2))
            throw new InvalidOperationException("Matching must be case-insensitive.");
        if (LogSearchPolicy.CountMatches("nothing here", "absent") != 0)
            throw new InvalidOperationException("A missing needle must report zero hits.");
        if (LogSearchPolicy.CountMatches(null, "x") != 0)
            throw new InvalidOperationException("A null haystack must report zero hits.");
        if (LogSearchPolicy.CountMatches("text", null) != 0)
            throw new InvalidOperationException("A null needle must report zero hits.");

        // Hits are non-overlapping: "aaaa" holds two disjoint "aa" runs at 0 and 2.
        // Counting the overlapping third start would report a hit the user cannot see
        // as a separate occurrence.
        List<int> disjoint = LogSearchPolicy.FindMatches("aaaa", "aa");
        if (disjoint.Count != 2)
            throw new InvalidOperationException("Matches must be non-overlapping, got " + disjoint.Count + ".");
        if (disjoint[0] != 0 || disjoint[1] != 2)
            throw new InvalidOperationException("Disjoint match offsets must be 0 and 2.");

        // A needle spanning a newline still matches, which matters for multi-line errors.
        if (LogSearchPolicy.CountMatches("line one\r\nline two", "one\r\nline") != 1)
            throw new InvalidOperationException("A needle crossing a line break must match.");
    }

    /// <summary>
    /// An empty search box must not claim to have found everything; that would show a
    /// meaningless "1/9999" and paint the whole log yellow.
    /// </summary>
    private static void VerifyEmptyNeedle()
    {
        if (LogSearchPolicy.CountMatches("some log text", "") != 0)
            throw new InvalidOperationException("An empty needle must match nothing.");
        if (LogSearchPolicy.CountMatches("some log text", null) != 0)
            throw new InvalidOperationException("A null needle must match nothing.");
        if (LogSearchPolicy.CountMatches("some log text", "   ") != 0)
            throw new InvalidOperationException("A whitespace-only needle must match nothing.");
        if (LogSearchPolicy.FindMatches("some log text", "").Count != 0)
            throw new InvalidOperationException("An empty needle must produce no matches.");
    }

    private static void VerifyHitNavigation()
    {
        // Forward from nothing goes to the first hit.
        if (LogSearchPolicy.NextMatchIndex(-1, 5) != 0)
            throw new InvalidOperationException("Forward from no position must land on the first hit.");
        if (LogSearchPolicy.NextMatchIndex(0, 5) != 1)
            throw new InvalidOperationException("Forward must advance by one.");
        // Wrapping keeps the button usable instead of dead-ending.
        if (LogSearchPolicy.NextMatchIndex(4, 5) != 0)
            throw new InvalidOperationException("Forward from the last hit must wrap to the first.");

        if (LogSearchPolicy.PreviousMatchIndex(-1, 5) != 4)
            throw new InvalidOperationException("Backward from no position must land on the last hit.");
        if (LogSearchPolicy.PreviousMatchIndex(2, 5) != 1)
            throw new InvalidOperationException("Backward must step by one.");
        if (LogSearchPolicy.PreviousMatchIndex(0, 5) != 4)
            throw new InvalidOperationException("Backward from the first hit must wrap to the last.");

        // No hits means no position at all.
        if (LogSearchPolicy.NextMatchIndex(0, 0) != -1)
            throw new InvalidOperationException("Navigating with no hits must report no position.");
        if (LogSearchPolicy.PreviousMatchIndex(0, 0) != -1)
            throw new InvalidOperationException("Navigating with no hits must report no position.");
    }

    private static void VerifyHitNumber()
    {
        if (LogSearchPolicy.CurrentHitNumber(0, 3) != 1)
            throw new InvalidOperationException("The first hit is number one.");
        if (LogSearchPolicy.CurrentHitNumber(2, 3) != 3)
            throw new InvalidOperationException("The last hit must report its one-based number.");
        // Before any navigation the display shows the first hit, so 0 is handled by the caller.
        if (LogSearchPolicy.CurrentHitNumber(-1, 3) != 0)
            throw new InvalidOperationException("An unset position must report zero.");
        if (LogSearchPolicy.CurrentHitNumber(5, 3) != 0)
            throw new InvalidOperationException("An out-of-range position must report zero.");
        if (LogSearchPolicy.CurrentHitNumber(0, 0) != 0)
            throw new InvalidOperationException("No hits means no number.");
    }

    private static void VerifyExportFileName()
    {
        string name = LogSearchPolicy.BuildDefaultFileName(new DateTime(2026, 9, 13, 11, 25, 30));
        if (name != "dsh-control-panel-20260913-112530.log")
            throw new InvalidOperationException("Unexpected export file name: " + name);
        // The timestamp is what keeps repeated exports from overwriting each other.
        string later = LogSearchPolicy.BuildDefaultFileName(new DateTime(2026, 9, 13, 11, 25, 31));
        if (name == later)
            throw new InvalidOperationException("Exports taken a second apart must not share a name.");
    }

    private static void VerifyExportText()
    {
        string text = LogSearchPolicy.BuildExportText(
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
        string noRoot = LogSearchPolicy.BuildExportText("body", "", new DateTime(2026, 1, 1));
        if (noRoot.IndexOf("(未设置)", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A missing install root must be labelled.");

        // An empty log still produces a usable file rather than throwing.
        string empty = LogSearchPolicy.BuildExportText("", @"C:\dsh", new DateTime(2026, 1, 1));
        if (empty.IndexOf("DeepSeek Harness 控制面板日志", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("An empty log must still export a header.");

        // A body that does not end in a newline must not merge with the next line.
        string noTrailing = LogSearchPolicy.BuildExportText("last line", @"C:\dsh", new DateTime(2026, 1, 1));
        if (!noTrailing.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            throw new InvalidOperationException("The export must end with a newline.");
    }
}
