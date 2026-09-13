using System;

public static class HarnessStartupPolicyTests
{
    public static int Main()
    {
        Run();
        return 0;
    }

    public static void Run()
    {
        if (HarnessStartupPolicy.SelectLaunchMode(true) != HarnessLaunchMode.BuiltCli)
            throw new InvalidOperationException("A built Harness CLI should be used for normal startup.");
        if (HarnessStartupPolicy.SelectLaunchMode(false) != HarnessLaunchMode.SourceFallback)
            throw new InvalidOperationException("The source launcher should be used only as a compatibility fallback.");
        if (!HarnessStartupPolicy.BuildWebArguments(3080).StartsWith("web --no-open", StringComparison.Ordinal))
            throw new InvalidOperationException("Harness must not open a second browser window.");
        if (!HarnessStartupPolicy.IsWebReadyLine("dsh web: http://127.0.0.1:3080", 3080))
            throw new InvalidOperationException("The official ready log should mark the web service ready.");
        string authenticatedUrl = HarnessStartupPolicy.GetWebReadyUrl(
            "dsh web: http://127.0.0.1:3080/?token=test-token (LAN: http://192.168.1.5:3080/?token=test-token)",
            3080);
        if (authenticatedUrl != "http://127.0.0.1:3080/?token=test-token")
            throw new InvalidOperationException("The authenticated loopback URL must be preserved exactly.");
        string redactedUrl = HarnessStartupPolicy.RedactWebToken(authenticatedUrl);
        if (redactedUrl.Contains("test-token") || !redactedUrl.Contains("token=<redacted>"))
            throw new InvalidOperationException("The web token must not be exposed in control panel logs.");
        string protectedUrl = StateSecretProtection.Protect(authenticatedUrl);
        if (protectedUrl.Contains("test-token") || StateSecretProtection.Unprotect(protectedUrl) != authenticatedUrl)
            throw new InvalidOperationException("The stored web URL must be encrypted for the current Windows user.");
        if (!HarnessStartupPolicy.IsWebReadyLine("dsh web: http://127.0.0.1:3080/?token=test-token", 3080))
            throw new InvalidOperationException("An authenticated official ready URL should mark the service ready.");
        if (HarnessStartupPolicy.GetWebReadyUrl("dsh web: http://evil.example:3080/?token=test-token", 3080) != "")
            throw new InvalidOperationException("A non-loopback ready URL must never be opened.");
        if (HarnessStartupPolicy.GetWebReadyUrl("dsh web: http://127.0.0.1:3081/?token=test-token", 3080) != "")
            throw new InvalidOperationException("A ready URL on another port must never be opened.");
        if (HarnessStartupPolicy.IsWebReadyLine("dsh web: opening the default browser", 3080))
            throw new InvalidOperationException("The browser handoff log must not be mistaken for service readiness.");
        if (HarnessStartupPolicy.IsWebReadyLine("dsh web: http://127.0.0.1:8080", 3080))
            throw new InvalidOperationException("A different port must not be treated as the managed Harness service.");
        if (HarnessStartupPolicy.IsWebReadyLine("dsh web: http://127.0.0.1:30808", 3080))
            throw new InvalidOperationException("A port with the managed port as a prefix must not be treated as ready.");
        // Printed here, not in Main: the aggregate entry point calls Run() directly.
        Console.WriteLine("Harness startup policy tests passed.");
    }
}
