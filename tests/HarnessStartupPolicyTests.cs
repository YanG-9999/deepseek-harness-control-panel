using System;

public static class HarnessStartupPolicyTests
{
    public static int Main()
    {
        Run();
        Console.WriteLine("Harness startup policy tests passed.");
        return 0;
    }

    public static void Run()
    {
        if (HarnessStartupPolicy.SelectLaunchMode(true) != HarnessLaunchMode.BuiltCli)
            throw new InvalidOperationException("A built Harness CLI should be used for normal startup.");
        if (HarnessStartupPolicy.SelectLaunchMode(false) != HarnessLaunchMode.SourceFallback)
            throw new InvalidOperationException("The source launcher should be used only as a compatibility fallback.");
        if (HarnessStartupPolicy.WebArguments != "web --no-open")
            throw new InvalidOperationException("Harness must not open a second browser window.");
        if (!HarnessStartupPolicy.IsWebReadyLine("dsh web: http://127.0.0.1:3080", 3080))
            throw new InvalidOperationException("The official ready log should mark the web service ready.");
        if (HarnessStartupPolicy.IsWebReadyLine("dsh web: opening the default browser", 3080))
            throw new InvalidOperationException("The browser handoff log must not be mistaken for service readiness.");
        if (HarnessStartupPolicy.IsWebReadyLine("dsh web: http://127.0.0.1:8080", 3080))
            throw new InvalidOperationException("A different port must not be treated as the managed Harness service.");
        if (HarnessStartupPolicy.IsWebReadyLine("dsh web: http://127.0.0.1:30808", 3080))
            throw new InvalidOperationException("A port with the managed port as a prefix must not be treated as ready.");
    }
}
