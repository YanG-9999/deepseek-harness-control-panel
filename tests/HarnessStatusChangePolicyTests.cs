using System;

/// <summary>
/// Covers the status snapshot's label rules and the change detector that decides
/// when a poll is worth a log line. The detector matters as much as the labels: a
/// three second poll that logged every tick would bury the transitions.
/// </summary>
public static class HarnessStatusChangePolicyTests
{
    public static void Run()
    {
        VerifyStatusLabels();
        VerifyRunningLabels();
        VerifyDifferenceDetection();
        VerifyRunningTransitions();
        VerifyPortTransitions();
        VerifyInstallTransitions();
        VerifyQuietCases();
        Console.WriteLine("Harness status change policy tests passed.");
    }

    /// <summary>Builds a snapshot with everything false but the given fields.</summary>
    private static HarnessStatusSnapshot Snapshot(
        bool installed,
        bool ready,
        bool portBusy,
        bool running,
        bool multiple,
        string version)
    {
        return new HarnessStatusSnapshot(installed, ready, portBusy, running, multiple, version);
    }

    private static void VerifyStatusLabels()
    {
        if (Snapshot(true, true, false, false, false, "1.0").StatusText != "已安装")
            throw new InvalidOperationException("An installed, complete tree reads as 已安装.");
        if (Snapshot(false, false, false, false, false, "").StatusText != "未安装")
            throw new InvalidOperationException("A missing tree reads as 未安装.");
        if (Snapshot(true, false, false, false, false, "1.0").StatusText != "安装不完整（需要修复）")
            throw new InvalidOperationException("An incomplete tree must be reported as needing repair.");
        // Multiple installs take precedence over the installed flag.
        if (Snapshot(true, true, false, true, true, "1.0").StatusText != "发现多个安装")
            throw new InvalidOperationException("Multiple installs must take precedence.");
        // Ready is only meaningful with an install.
        if (Snapshot(false, false, false, false, false, "").StatusText != "未安装")
            throw new InvalidOperationException("An uninstalled tree must never read as needing repair.");
    }

    private static void VerifyRunningLabels()
    {
        if (Snapshot(true, true, false, true, false, "1.0").RunningText != "正在运行")
            throw new InvalidOperationException("A verified Harness owner reads as 正在运行.");
        if (Snapshot(true, true, false, false, false, "1.0").RunningText != "未运行")
            throw new InvalidOperationException("A free port reads as 未运行.");
        if (Snapshot(true, true, true, false, false, "1.0").RunningText != "端口被其他程序占用")
            throw new InvalidOperationException("A foreign listener must be named as such.");
        // A busy port whose owner is Harness is running, not foreign.
        if (Snapshot(true, true, true, true, false, "1.0").RunningText != "正在运行")
            throw new InvalidOperationException("A Harness-owned port must not be reported as foreign.");
    }

    private static void VerifyDifferenceDetection()
    {
        HarnessStatusSnapshot baseline = Snapshot(true, true, true, true, false, "1.0");
        if (baseline.DiffersFrom(Snapshot(true, true, true, true, false, "1.0")))
            throw new InvalidOperationException("Identical snapshots must not differ.");
        if (!baseline.DiffersFrom(Snapshot(true, true, true, false, false, "1.0")))
            throw new InvalidOperationException("A running change must be detected.");
        if (!baseline.DiffersFrom(Snapshot(true, true, false, true, false, "1.0")))
            throw new InvalidOperationException("A port change must be detected.");
        if (!baseline.DiffersFrom(Snapshot(true, false, true, true, false, "1.0")))
            throw new InvalidOperationException("A readiness change must be detected.");
        if (!baseline.DiffersFrom(Snapshot(false, false, true, true, false, "1.0")))
            throw new InvalidOperationException("An install change must be detected.");
        if (!baseline.DiffersFrom(Snapshot(true, true, true, true, true, "1.0")))
            throw new InvalidOperationException("A multi-install change must be detected.");
        if (!baseline.DiffersFrom(Snapshot(true, true, true, true, false, "2.0")))
            throw new InvalidOperationException("A version change must be detected.");
        if (!baseline.DiffersFrom(null))
            throw new InvalidOperationException("The first snapshot must count as a change.");
    }

    private static void VerifyRunningTransitions()
    {
        string stopped = HarnessStatusChangePolicy.DescribeChange(3080, 
            Snapshot(true, true, true, true, false, "1.0"),
            Snapshot(true, true, false, false, false, "1.0"));
        if (stopped.IndexOf("已退出", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A stop must be reported as an exit, got: " + stopped);
        if (stopped.IndexOf("3080", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The exit line must name the port that stopped listening.");

        string started = HarnessStatusChangePolicy.DescribeChange(3080, 
            Snapshot(true, true, false, false, false, "1.0"),
            Snapshot(true, true, true, true, false, "1.0"));
        if (started.IndexOf("已开始运行", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A start must be reported, got: " + started);
    }

    private static void VerifyPortTransitions()
    {
        string taken = HarnessStatusChangePolicy.DescribeChange(3080, 
            Snapshot(true, true, false, false, false, "1.0"),
            Snapshot(true, true, true, false, false, "1.0"));
        if (taken.IndexOf("被其他程序占用", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A foreign listener must be reported, got: " + taken);

        string freed = HarnessStatusChangePolicy.DescribeChange(3080, 
            Snapshot(true, true, true, false, false, "1.0"),
            Snapshot(true, true, false, false, false, "1.0"));
        if (freed.IndexOf("已被释放", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A released port must be reported, got: " + freed);
    }

    private static void VerifyInstallTransitions()
    {
        string added = HarnessStatusChangePolicy.DescribeChange(3080, 
            Snapshot(false, false, false, false, false, ""),
            Snapshot(true, true, false, false, false, "1.0"));
        if (added.IndexOf("检测到 Harness 安装", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("An install appearing must be reported, got: " + added);

        string removed = HarnessStatusChangePolicy.DescribeChange(3080, 
            Snapshot(true, true, false, false, false, "1.0"),
            Snapshot(false, false, false, false, false, ""));
        if (removed.IndexOf("不可用", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("An install disappearing must be reported, got: " + removed);

        // Isolate the readiness change from the running change: DescribeChange
        // reports a process exit first, because that is the more urgent event.
        string broken = HarnessStatusChangePolicy.DescribeChange(3080, 
            Snapshot(true, true, false, false, false, "1.0"),
            Snapshot(true, false, false, false, false, "1.0"));
        if (broken.IndexOf("不再完整", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("An install degrading must be reported, got: " + broken);

        // When both change at once, the process lifecycle wins.
        string exitWins = HarnessStatusChangePolicy.DescribeChange(3080, 
            Snapshot(true, true, true, true, false, "1.0"),
            Snapshot(true, false, false, false, false, "1.0"));
        if (exitWins.IndexOf("已退出", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A process exit must take priority over a readiness change, got: " + exitWins);

        string repaired = HarnessStatusChangePolicy.DescribeChange(3080, 
            Snapshot(true, false, false, false, false, "1.0"),
            Snapshot(true, true, false, false, false, "1.0"));
        if (repaired.IndexOf("已恢复完整", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A repair must be reported, got: " + repaired);

        string many = HarnessStatusChangePolicy.DescribeChange(3080, 
            Snapshot(true, true, false, false, false, "1.0"),
            Snapshot(true, true, false, false, true, "1.0"));
        if (many.IndexOf("多个 Harness 安装", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("Multiple installs must be reported, got: " + many);
    }

    /// <summary>
    /// The rule that keeps the log usable: an unchanged poll says nothing, and a
    /// version-only change does not produce a second line on top of the transition
    /// that caused it.
    /// </summary>
    private static void VerifyQuietCases()
    {
        HarnessStatusSnapshot steady = Snapshot(true, true, true, true, false, "1.0");
        string none = HarnessStatusChangePolicy.DescribeChange(3080, steady, Snapshot(true, true, true, true, false, "1.0"));
        if (none != "")
            throw new InvalidOperationException("An unchanged poll must produce no log line, got: " + none);

        string versionOnly = HarnessStatusChangePolicy.DescribeChange(3080, 
            Snapshot(true, true, true, true, false, "1.0"),
            Snapshot(true, true, true, true, false, "2.0"));
        if (versionOnly != "")
            throw new InvalidOperationException("A version-only change must not log on its own, got: " + versionOnly);

        if (HarnessStatusChangePolicy.DescribeChange(3080, null, steady) != "")
            throw new InvalidOperationException("A missing previous snapshot must not log.");
        if (HarnessStatusChangePolicy.DescribeChange(3080, steady, null) != "")
            throw new InvalidOperationException("A missing current snapshot must not log.");
    }
}
