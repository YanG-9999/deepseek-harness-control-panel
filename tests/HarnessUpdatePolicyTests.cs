using System;

/// <summary>
/// Covers the wording and comparison rules behind the automatic update check. The
/// rule that matters most is that an unreadable local commit must never be reported
/// as "an update is available", because that would send users chasing a false alarm.
/// </summary>
public static class HarnessUpdatePolicyTests
{
    private const string Local = "aa8262ec091698bae9a6b04773a6b5b06ad4aef2";
    private const string Remote = "c291e7961a515f6d7af9304e7fd1d257929aef26";

    public static void Run()
    {
        VerifyCommitIdValidation();
        VerifyHasUpdate();
        VerifyShortCommit();
        VerifyDescription();
        Console.WriteLine("Harness update policy tests passed.");
    }

    private static void VerifyCommitIdValidation()
    {
        if (!HarnessUpdatePolicy.IsCommitId(Local))
            throw new InvalidOperationException("A 40-character hex digest must be accepted.");
        if (!HarnessUpdatePolicy.IsCommitId(Remote.ToUpperInvariant()))
            throw new InvalidOperationException("An uppercase digest must be accepted.");
        if (HarnessUpdatePolicy.IsCommitId(Local.Substring(0, 7)))
            throw new InvalidOperationException("A short sha must not be treated as a full commit id.");
        if (HarnessUpdatePolicy.IsCommitId(Local + "0"))
            throw new InvalidOperationException("An over-long digest must be rejected.");
        if (HarnessUpdatePolicy.IsCommitId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaag"))
            throw new InvalidOperationException("A digest with a non-hex character must be rejected.");
        if (HarnessUpdatePolicy.IsCommitId(""))
            throw new InvalidOperationException("An empty value is not a commit id.");
        if (HarnessUpdatePolicy.IsCommitId(null))
            throw new InvalidOperationException("A null value is not a commit id.");
        // A partially written state file can leave a placeholder behind.
        if (HarnessUpdatePolicy.IsCommitId("unknown"))
            throw new InvalidOperationException("A placeholder must not be treated as a commit id.");
    }

    private static void VerifyHasUpdate()
    {
        if (!HarnessUpdatePolicy.HasUpdate(Local, Remote))
            throw new InvalidOperationException("Different commits must report an update.");
        if (HarnessUpdatePolicy.HasUpdate(Local, Local))
            throw new InvalidOperationException("Identical commits must not report an update.");
        if (HarnessUpdatePolicy.HasUpdate(Local, Local.ToUpperInvariant()))
            throw new InvalidOperationException("Commit comparison must be case-insensitive.");

        // The critical rule: without a readable local commit there is nothing to
        // compare, so no update may be claimed.
        if (HarnessUpdatePolicy.HasUpdate("", Remote))
            throw new InvalidOperationException("An unknown local commit must not report an update.");
        if (HarnessUpdatePolicy.HasUpdate(null, Remote))
            throw new InvalidOperationException("A missing local commit must not report an update.");
        if (HarnessUpdatePolicy.HasUpdate(Local, ""))
            throw new InvalidOperationException("An unknown remote commit must not report an update.");
        if (HarnessUpdatePolicy.HasUpdate(Local, "not-a-sha"))
            throw new InvalidOperationException("A malformed remote commit must not report an update.");
    }

    private static void VerifyShortCommit()
    {
        if (HarnessUpdatePolicy.ShortCommit(Local) != "aa8262e")
            throw new InvalidOperationException("A commit must shorten to its first seven characters.");
        if (HarnessUpdatePolicy.ShortCommit("") != "未知")
            throw new InvalidOperationException("An unknown commit must be labelled explicitly.");
        if (HarnessUpdatePolicy.ShortCommit(null) != "未知")
            throw new InvalidOperationException("A null commit must be labelled explicitly.");
    }

    private static void VerifyDescription()
    {
        string same = HarnessUpdatePolicy.DescribeAvailability(Local, Local);
        if (same.IndexOf("已是最新版本", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("Identical commits must be described as up to date.");
        if (same.IndexOf("aa8262e", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The up-to-date line must show which version is installed.");

        string newer = HarnessUpdatePolicy.DescribeAvailability(Local, Remote);
        if (newer.IndexOf("发现新版本", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("Different commits must be described as an available update.");
        if (newer.IndexOf("aa8262e", StringComparison.Ordinal) < 0 ||
            newer.IndexOf("c291e79", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The update line must show both sides of the change.");

        string unknownLocal = HarnessUpdatePolicy.DescribeAvailability("", Remote);
        if (unknownLocal.IndexOf("发现新版本", StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("An unknown local version must not be described as an available update.");
        if (unknownLocal.IndexOf("无法比较", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("An unknown local version must say why no comparison happened.");

        // Nothing usable to say when the remote commit could not be read.
        if (HarnessUpdatePolicy.DescribeAvailability(Local, "") != "")
            throw new InvalidOperationException("An unreadable remote commit must produce no status line.");
        if (HarnessUpdatePolicy.DescribeAvailability(Local, null) != "")
            throw new InvalidOperationException("A null remote commit must produce no status line.");
    }
}
