using System;

public static class HarnessLifecyclePolicyTests
{
    public static int Main()
    {
        Run();
        return 0;
    }

    public static void Run()
    {
        string harnessDocument = "<!doctype html><html><script>window.__DSH_BOOT__ = {}</script></html>";
        if (!HarnessLifecyclePolicy.IsHarnessDocument(harnessDocument))
            throw new InvalidOperationException("A DSH boot document should be recognized as a Harness page.");
        if (HarnessLifecyclePolicy.IsHarnessDocument("<!doctype html><html><body>It works</body></html>"))
            throw new InvalidOperationException("An arbitrary local web page must not be treated as Harness.");
        if (!HarnessLifecyclePolicy.IsStartupReady(true, true, true, false))
            throw new InvalidOperationException("A live process, listening port, and Harness page should be ready.");
        if (!HarnessLifecyclePolicy.IsStartupReady(true, true, false, true))
            throw new InvalidOperationException("An official ready log should remain a fallback when the web document changes.");
        if (HarnessLifecyclePolicy.IsStartupReady(false, true, true, true))
            throw new InvalidOperationException("A dead process must never be reported as ready.");
        if (HarnessLifecyclePolicy.IsStartupReady(true, false, true, true))
            throw new InvalidOperationException("A closed port must never be reported as ready.");
        if (HarnessLifecyclePolicy.IsStartupReady(true, true, false, false))
            throw new InvalidOperationException("An unverified page must not be reported as ready.");
        if (HarnessLifecyclePolicy.StopWaitAttempts(30000, 250) != 120)
            throw new InvalidOperationException("Stopping should wait up to 30 seconds in 250ms intervals.");
        if (HarnessLifecyclePolicy.StopWaitAttempts(1, 250) != 1)
            throw new InvalidOperationException("Stopping should always make at least one readiness check.");
        if (!HarnessLifecyclePolicy.StartupFailureMessage(false, false).Contains("尚未监听"))
            throw new InvalidOperationException("The no-port startup failure should name the failed stage.");
        if (!HarnessLifecyclePolicy.StartupFailureMessage(true, false).Contains("页面尚未可访问"))
            throw new InvalidOperationException("The no-page startup failure should name the failed stage.");
        // Printed here, not in Main: the aggregate entry point calls Run() directly.
        Console.WriteLine("Harness lifecycle policy tests passed.");
    }
}
