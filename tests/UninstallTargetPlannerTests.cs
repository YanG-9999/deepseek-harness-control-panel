using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static class UninstallTargetPlannerTests
{
    public static void Run()
    {
        VerifyTargetPlanning();
        VerifyKindsAndDefaults();
        VerifySizeMeasurement();
        VerifyJunctionsAreNotFollowed();
        VerifySizeWording();
        VerifySelectionText();
        VerifyWarning();
        Console.WriteLine("Uninstall target planner tests passed.");
    }

    private static void VerifyTargetPlanning()
    {
        var targets = UninstallTargetPlanner.BuildTargets(
            "D:\\DeepSeekHarness",
            "C:\\Users\\TestUser\\.dsh",
            "C:\\Users\\TestUser\\AppData\\Local\\DeepSeekHarnessManager");

        if (targets.Count != 3)
            throw new InvalidOperationException("Uninstall target count was " + targets.Count + ".");
        if (!targets.Any(target => target.Path.Equals("D:\\DeepSeekHarness", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Harness installation directory was not selected.");
        if (!targets.Any(target => target.Path.EndsWith("\\.dsh", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Harness user data directory was not selected.");
        if (!targets.Any(target => target.Path.EndsWith("\\DeepSeekHarnessManager", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Manager settings directory was not selected.");

        // A duplicated path must not appear twice, or it would be deleted twice and
        // reported as a failure the second time.
        var duplicated = UninstallTargetPlanner.BuildTargets(
            "D:\\Same", "D:\\Same", "D:\\Same");
        if (duplicated.Count != 1)
            throw new InvalidOperationException("A repeated path must appear once, got " + duplicated.Count + ".");

        // Blank paths are skipped rather than producing a target rooted at nothing.
        var blank = UninstallTargetPlanner.BuildTargets("", "  ", null);
        if (blank.Count != 0)
            throw new InvalidOperationException("Blank paths must not become targets.");
    }

    /// <summary>
    /// The safety property of this phase: the target holding credentials and session
    /// history must never start selected. Removing it cannot be undone by reinstalling.
    /// </summary>
    private static void VerifyKindsAndDefaults()
    {
        var targets = UninstallTargetPlanner.BuildTargets(
            "D:\\DeepSeekHarness",
            "C:\\Users\\TestUser\\.dsh",
            "C:\\Users\\TestUser\\AppData\\Local\\DeepSeekHarnessManager");

        UninstallTarget program = targets.First(target => target.Kind == UninstallTargetKind.ProgramFiles);
        UninstallTarget userData = targets.First(target => target.Kind == UninstallTargetKind.UserData);
        UninstallTarget panel = targets.First(target => target.Kind == UninstallTargetKind.PanelSettings);

        if (!program.SelectedByDefault)
            throw new InvalidOperationException("The program tree must start selected; it is the reason to uninstall.");
        if (!panel.SelectedByDefault)
            throw new InvalidOperationException("The panel's own settings may start selected.");
        if (userData.SelectedByDefault)
            throw new InvalidOperationException(
                "User data must NOT start selected: it holds API keys and history that no reinstall can restore.");

        // Every target must carry a kind, so the default rule can never fall through.
        foreach (UninstallTarget target in targets)
        {
            if (target.Kind != UninstallTargetKind.ProgramFiles &&
                target.Kind != UninstallTargetKind.UserData &&
                target.Kind != UninstallTargetKind.PanelSettings)
                throw new InvalidOperationException("Unexpected target kind: " + target.Kind);
        }
    }

    private static void VerifySizeMeasurement()
    {
        // A missing path is distinct from an empty one.
        string missing = Path.Combine(Path.GetTempPath(), "dsh-size-" + Guid.NewGuid().ToString("N"));
        if (UninstallSelectionPolicy.MeasureSizeBytes(missing) != UninstallSelectionPolicy.Missing)
            throw new InvalidOperationException("A missing path must report Missing.");
        if (UninstallSelectionPolicy.MeasureSizeBytes("") != UninstallSelectionPolicy.Missing)
            throw new InvalidOperationException("A blank path must report Missing.");
        if (UninstallSelectionPolicy.MeasureSizeBytes(null) != UninstallSelectionPolicy.Missing)
            throw new InvalidOperationException("A null path must report Missing.");

        string directory = Path.Combine(Path.GetTempPath(), "dsh-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // An existing but empty directory is 0 bytes, not Missing.
            if (UninstallSelectionPolicy.MeasureSizeBytes(directory) != 0)
                throw new InvalidOperationException("An empty directory must measure as zero, not missing.");

            byte[] payload = new byte[2048];
            File.WriteAllBytes(Path.Combine(directory, "a.bin"), payload);
            Directory.CreateDirectory(Path.Combine(directory, "nested"));
            File.WriteAllBytes(Path.Combine(directory, "nested", "b.bin"), payload);

            long measured = UninstallSelectionPolicy.MeasureSizeBytes(directory);
            if (measured != 4096)
                throw new InvalidOperationException("Nested files must be counted, got " + measured + ".");

            // A single file is measurable too, since the panel's settings may be one.
            string single = Path.Combine(directory, "a.bin");
            if (UninstallSelectionPolicy.MeasureSizeBytes(single) != 2048)
                throw new InvalidOperationException("A single file must measure its own length.");
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    /// <summary>
    /// A directory holding a junction must be measured without going through it.
    ///
    /// The Harness install is built from junctions: pnpm points thousands of package entries
    /// back into the same tree, so following them re-walks the same files over and over.
    /// Directory.GetFiles with AllDirectories follows them, and on the machine this was
    /// written for that walk had not finished after two and a half minutes - on the UI
    /// thread, which is what made the uninstall button look dead when it was pressed.
    /// </summary>
    private static void VerifyJunctionsAreNotFollowed()
    {
        string root = Path.Combine(Path.GetTempPath(), "dsh-junction-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(Path.GetTempPath(), "dsh-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "real"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "real", "a.bin"), new byte[2048]);
            File.WriteAllBytes(Path.Combine(outside, "b.bin"), new byte[4096]);

            long direct = UninstallSelectionPolicy.MeasureSizeBytes(root);
            if (direct != 2048)
                throw new InvalidOperationException("The real file must be the only size counted, got " + direct + ".");

            string link = Path.Combine(root, "linked");
            if (!TryCreateJunction(link, outside))
                return; // No junction could be made here; the rule below is simply not exercised.

            long withJunction = UninstallSelectionPolicy.MeasureSizeBytes(root);
            if (withJunction != direct)
                throw new InvalidOperationException(
                    "A junction must not be measured through: without it " + direct +
                    ", with it " + withJunction + ".");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    /// <summary>
    /// Creates a directory junction. Junctions, unlike symbolic links, need no elevation,
    /// which is why the test uses one; .NET has no managed API for either.
    /// </summary>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            using (var process = System.Diagnostics.Process.Start(info))
            {
                process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0 && Directory.Exists(link);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void VerifySizeWording()
    {
        if (UninstallSelectionPolicy.DescribeSize(UninstallSelectionPolicy.Missing) != "不存在")
            throw new InvalidOperationException("A missing path needs its own wording.");
        if (UninstallSelectionPolicy.DescribeSize(UninstallSelectionPolicy.Unmeasurable) != "无法测量")
            throw new InvalidOperationException("An unmeasurable path needs its own wording, not 0 B.");
        if (UninstallSelectionPolicy.DescribeSize(0) != "0 B")
            throw new InvalidOperationException("Zero bytes must read as 0 B.");
        if (UninstallSelectionPolicy.DescribeSize(512) != "512 B")
            throw new InvalidOperationException("Bytes must be shown as bytes.");
        if (UninstallSelectionPolicy.DescribeSize(2048) != "2.0 KB")
            throw new InvalidOperationException("Kilobytes were mis-scaled: " + UninstallSelectionPolicy.DescribeSize(2048));
        if (UninstallSelectionPolicy.DescribeSize(5 * 1024 * 1024) != "5.0 MB")
            throw new InvalidOperationException("Megabytes were mis-scaled: " + UninstallSelectionPolicy.DescribeSize(5 * 1024 * 1024));
        if (UninstallSelectionPolicy.DescribeSize(3L * 1024 * 1024 * 1024) != "3.00 GB")
            throw new InvalidOperationException("Gigabytes were mis-scaled: " + UninstallSelectionPolicy.DescribeSize(3L * 1024 * 1024 * 1024));
    }

    /// <summary>
    /// The summary must agree with the checkboxes. If it said "delete" for something
    /// the user left unchecked, the confirmation would misrepresent the action.
    /// </summary>
    private static void VerifySelectionText()
    {
        var targets = UninstallTargetPlanner.BuildTargets(
            "D:\\DeepSeekHarness",
            "C:\\Users\\TestUser\\.dsh",
            "C:\\Users\\TestUser\\AppData\\Local\\DeepSeekHarnessManager");

        // Only the program tree selected: the default case.
        string programOnly = UninstallSelectionPolicy.DescribeSelection(
            targets,
            delegate(UninstallTarget target) { return target.Kind == UninstallTargetKind.ProgramFiles; });
        if (programOnly.IndexOf("[删除] Harness 安装目录", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The selected program tree must be marked for deletion.");
        if (programOnly.IndexOf("[保留] Harness 用户数据", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("Unselected user data must be marked as kept.");
        if (programOnly.IndexOf("未勾选的项目会完整保留", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The summary must say that unselected items are kept.");
        // The size must appear next to each path, since that is what the choice costs.
        if (programOnly.IndexOf("(不存在)", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("Each target must show its measured size.");

        // Nothing selected must be called out rather than silently producing an empty list.
        string nothing = UninstallSelectionPolicy.DescribeSelection(
            targets,
            delegate(UninstallTarget target) { return false; });
        if (nothing.IndexOf("没有勾选任何要删除的项目", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("An empty selection must be stated explicitly.");

        // A null selector must be treated as nothing selected, never as everything.
        string nullSelector = UninstallSelectionPolicy.DescribeSelection(targets, null);
        if (nullSelector.IndexOf("[删除]", StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("A missing selector must not imply deletion.");
    }

    private static void VerifyWarning()
    {
        var targets = UninstallTargetPlanner.BuildTargets(
            "D:\\DeepSeekHarness",
            "C:\\Users\\TestUser\\.dsh",
            "C:\\Users\\TestUser\\AppData\\Local\\DeepSeekHarnessManager");

        string withoutUserData = UninstallSelectionPolicy.BuildWarning(
            targets,
            delegate(UninstallTarget target) { return target.Kind == UninstallTargetKind.ProgramFiles; });
        if (withoutUserData.IndexOf("API Key", StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The warning must not cry wolf when user data is kept.");

        string withUserData = UninstallSelectionPolicy.BuildWarning(
            targets,
            delegate(UninstallTarget target) { return true; });
        if (withUserData.IndexOf("API Key", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("Removing user data must name what is lost.");
        if (withUserData.IndexOf("无法找回", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("Removing user data must state that it cannot be recovered.");

        // A null selector must not produce the alarming variant.
        string nullSelector = UninstallSelectionPolicy.BuildWarning(targets, null);
        if (nullSelector.IndexOf("API Key", StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("A missing selector must not imply user data is removed.");
    }
}
