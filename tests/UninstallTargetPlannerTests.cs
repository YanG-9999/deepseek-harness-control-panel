using System;
using System.Linq;

public static class UninstallTargetPlannerTests
{
    public static void Run()
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
    }
}
