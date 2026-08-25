using System;

public static class HarnessProcessIdentityPolicyTests
{
    public static int Main()
    {
        Run();
        Console.WriteLine("Harness process identity policy tests passed.");
        return 0;
    }

    public static void Run()
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
}
