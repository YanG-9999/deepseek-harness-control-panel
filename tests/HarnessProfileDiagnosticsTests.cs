using System;

public static class HarnessProfileDiagnosticsTests
{
    public static int Main()
    {
        Run();
        return 0;
    }

    public static void Run()
    {
        string[] packages = new[]
        {
            "@deepseek-ai/dsh-base",
            "@deepseek-ai/dsh-web-app",
            "dsh-better-sidebar",
            "@community/dsh-memory"
        };
        if (HarnessProfileDiagnostics.CountThirdPartyPackages(packages) != 2)
            throw new InvalidOperationException("Only non-official package names should count as third-party extensions.");
        if (HarnessProfileDiagnostics.CountThirdPartyPackages(null) != 0)
            throw new InvalidOperationException("A missing profile package list should be safe to diagnose.");
        string hint = HarnessProfileDiagnostics.BuildStartupHint(4, 2);
        if (!hint.Contains("4") || !hint.Contains("2") || !hint.Contains("第三方"))
            throw new InvalidOperationException("The startup hint should explain the configured extension count.");
        if (HarnessProfileDiagnostics.BuildStartupHint(2, 0) != "")
            throw new InvalidOperationException("Official-only profiles should not produce an unnecessary warning.");
        // Printed here, not in Main: the aggregate entry point calls Run() directly.
        Console.WriteLine("Harness profile diagnostics tests passed.");
    }
}
