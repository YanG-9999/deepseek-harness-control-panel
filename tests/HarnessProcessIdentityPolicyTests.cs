using System;
using System.Collections.Generic;

public static class HarnessProcessIdentityPolicyTests
{
    public static int Main()
    {
        Run();
        return 0;
    }

    public static void Run()
    {
        VerifyCommandLineRules();
        VerifyShortCircuit();
        VerifyAncestorFallback();
        VerifyWalkLimits();
        // Printed here, not in Main: the aggregate entry point calls Run() directly.
        Console.WriteLine("Harness process identity policy tests passed.");
    }

    private static void VerifyCommandLineRules()
    {
        if (!HarnessProcessIdentityPolicy.IsHarnessCommandLine(
            "\"C:\\Program Files\\nodejs\\node.exe\" \"D:\\DeepSeekHarness\\apps\\cli\\lib\\bin.js\" web --no-open"))
            throw new InvalidOperationException("The built CLI at a custom Harness directory should be recognized.");
        if (!HarnessProcessIdentityPolicy.IsHarnessCommandLine(
            "node apps/cli/src/bin.ts web"))
            throw new InvalidOperationException("The source CLI should remain recognized for compatibility mode.");
        if (!HarnessProcessIdentityPolicy.IsHarnessCommandLine(
            "node D:\\deepseek-harness\\custom-server.js"))
            throw new InvalidOperationException("Legacy install paths containing deepseek-harness should remain recognized.");
        if (HarnessProcessIdentityPolicy.IsHarnessCommandLine(
            "\"C:\\Program Files\\nodejs\\node.exe\" C:\\work\\server.js --port 3080"))
            throw new InvalidOperationException("An unrelated Node.js server must not be treated as Harness.");
    }

    /// <summary>
    /// The refresh cost fix in one assertion: when the port owner is Harness, the
    /// walk must answer from the first command-line lookup and never touch the parent
    /// chain. Before this, the same answer cost sixteen WMI queries.
    /// </summary>
    private static void VerifyShortCircuit()
    {
        var lookup = new FakeProcesses();
        lookup.Add(4596, 1000, "\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\dsh\\apps\\cli\\lib\\bin.js\" web --no-open");

        HarnessIdentityResult result = HarnessProcessIdentityPolicy.Identify(
            4596, lookup.CommandLine, lookup.ParentPid);

        if (!result.IsHarness)
            throw new InvalidOperationException("The real port owner must be identified as Harness.");
        if (result.MatchedPid != 4596 || result.MatchedDepth != 0)
            throw new InvalidOperationException("The match must be attributed to the owner itself.");
        if (result.CommandLineLookups != 1)
            throw new InvalidOperationException(
                "A matching owner must cost exactly one command-line lookup, got " + result.CommandLineLookups + ".");
        if (result.ParentLookups != 0)
            throw new InvalidOperationException(
                "A matching owner must not query the parent chain, got " + result.ParentLookups + ".");
    }

    /// <summary>
    /// The parent walk must remain available for a wrapper process, because removing
    /// it would change behaviour for installations started through an intermediary.
    /// </summary>
    private static void VerifyAncestorFallback()
    {
        var lookup = new FakeProcesses();
        lookup.Add(500, 400, "C:\\Windows\\System32\\cmd.exe /c start-harness.cmd");
        lookup.Add(400, 300, "\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\dsh\\apps\\cli\\lib\\bin.js\" web");

        HarnessIdentityResult result = HarnessProcessIdentityPolicy.Identify(
            500, lookup.CommandLine, lookup.ParentPid);

        if (!result.IsHarness)
            throw new InvalidOperationException("A wrapper must still resolve to Harness through its parent.");
        if (result.MatchedDepth != 1 || result.MatchedPid != 400)
            throw new InvalidOperationException("The match must be attributed to the ancestor, at depth 1.");
        if (result.CommandLineLookups != 2 || result.ParentLookups != 1)
            throw new InvalidOperationException("The fallback must cost one extra lookup pair.");
    }

    private static void VerifyWalkLimits()
    {
        // A chain that never matches must stop at the cap instead of walking forever.
        var endless = new FakeProcesses();
        for (int pid = 1; pid <= 40; pid++)
            endless.Add(pid, pid + 1, "C:\\Windows\\System32\\svchost.exe -k netsvcs");

        HarnessIdentityResult result = HarnessProcessIdentityPolicy.Identify(
            1, endless.CommandLine, endless.ParentPid);
        if (result.IsHarness)
            throw new InvalidOperationException("A non-Harness chain must not be reported as Harness.");
        if (result.CommandLineLookups != HarnessProcessIdentityPolicy.MaxAncestorHops)
            throw new InvalidOperationException(
                "The walk must stop at the hop cap, got " + result.CommandLineLookups + " lookups.");

        // A self-parenting process must terminate the walk immediately.
        var selfParent = new FakeProcesses();
        selfParent.Add(77, 77, "C:\\Windows\\System32\\svchost.exe");
        HarnessIdentityResult self = HarnessProcessIdentityPolicy.Identify(
            77, selfParent.CommandLine, selfParent.ParentPid);
        if (self.IsHarness || self.CommandLineLookups != 1 || self.ParentLookups != 1)
            throw new InvalidOperationException("A self-parenting process must end the walk after one hop.");

        // A pid that cannot be resolved at all.
        var empty = new FakeProcesses();
        HarnessIdentityResult missing = HarnessProcessIdentityPolicy.Identify(
            999, empty.CommandLine, empty.ParentPid);
        if (missing.IsHarness)
            throw new InvalidOperationException("An unresolvable process must not be reported as Harness.");

        // Defensive: the callers are required.
        AssertThrows("null command line lookup", delegate
        {
            HarnessProcessIdentityPolicy.Identify(1, null, empty.ParentPid);
        });
        AssertThrows("null parent lookup", delegate
        {
            HarnessProcessIdentityPolicy.Identify(1, empty.CommandLine, null);
        });
    }

    private static void AssertThrows(string label, Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException("Expected an ArgumentException for " + label + ".");
    }

    /// <summary>A process table with no real processes behind it.</summary>
    private sealed class FakeProcesses
    {
        private readonly Dictionary<int, string> commandLines = new Dictionary<int, string>();
        private readonly Dictionary<int, int> parents = new Dictionary<int, int>();

        public void Add(int pid, int parentPid, string commandLine)
        {
            commandLines[pid] = commandLine;
            parents[pid] = parentPid;
        }

        public string CommandLine(int pid)
        {
            string value;
            return commandLines.TryGetValue(pid, out value) ? value : "";
        }

        public int ParentPid(int pid)
        {
            int value;
            return parents.TryGetValue(pid, out value) ? value : 0;
        }
    }
}
