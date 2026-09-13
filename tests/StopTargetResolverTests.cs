using System;

public static class StopTargetResolverTests
{
    public static int Main()
    {
        // The aggregated suite runs every policy class; a failure here must name the
        // class and the real message, because a corrupted exception string is
        // otherwise unreportable.
        try
        {
            RunAll();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAILED in aggregated suite");
            Console.Error.WriteLine("type: " + error.GetType().FullName);
            Console.Error.WriteLine("message: " + SafeMessage(error));
            Console.Error.WriteLine("stack: " + (error.StackTrace ?? "(none)"));
            Exception inner = error.InnerException;
            while (inner != null)
            {
                Console.Error.WriteLine("inner " + inner.GetType().FullName + ": " + SafeMessage(inner));
                inner = inner.InnerException;
            }
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// Formats a message without calling ToString(), which can itself throw for some
    /// exception types and would hide the original failure.
    /// </summary>
    private static string SafeMessage(Exception error)
    {
        try
        {
            return error.Message;
        }
        catch (Exception formatting)
        {
            return "(message unavailable: " + formatting.GetType().Name + ")";
        }
    }

    private static void RunAll()
    {
        ControlPanelLayoutTests.Run();
        UninstallTargetPlannerTests.Run();
        LogLineFormatterTests.Run();
        LogViewRenderingTests.Run();
        HarnessInstallationValidatorTests.Run();
        HarnessStartupPolicyTests.Run();
        HarnessLifecyclePolicyTests.Run();
        HarnessProfileDiagnosticsTests.Run();
        HarnessProcessIdentityPolicyTests.Run();
        NodeNetworkPolicyTests.Run();
        UnexpectedErrorReportTests.Run();
        HarnessUpdatePolicyTests.Run();
        SingleInstancePolicyTests.Run();
        HarnessStatusChangePolicyTests.Run();
        OperationCancellationPolicyTests.Run();
        LogSearchPolicyTests.Run();
        HarnessPortPolicyTests.Run();
        AutoStartAndTrayPolicyTests.Run();
        PanelVersionPolicyTests.Run();

        AssertResolution(
            StopTargetKind.None,
            0,
            StopTargetResolver.Resolve(1234, 0, false, false, false),
            "stale recorded PID should be ignored");

        AssertResolution(
            StopTargetKind.None,
            0,
            StopTargetResolver.Resolve(1234, 0, true, false, false),
            "reused unrelated PID should not be terminated");

        AssertResolution(
            StopTargetKind.HarnessProcess,
            1234,
            StopTargetResolver.Resolve(1234, 0, true, true, false),
            "verified orphaned Harness process should be terminated");

        AssertResolution(
            StopTargetKind.ForeignPort,
            0,
            StopTargetResolver.Resolve(1234, 5678, false, false, false),
            "foreign process listening on port 3080 should be protected");

        AssertResolution(
            StopTargetKind.HarnessProcess,
            5678,
            StopTargetResolver.Resolve(1234, 5678, false, false, true),
            "verified Harness port owner should be terminated");

        Console.WriteLine("StopTargetResolver tests passed.");
    }

    private static void AssertResolution(
        StopTargetKind expectedKind,
        int expectedPid,
        StopResolution actual,
        string message)
    {
        if (actual.Kind != expectedKind || actual.ProcessId != expectedPid)
        {
            throw new InvalidOperationException(
                message + ": expected " + expectedKind + "/" + expectedPid +
                ", got " + actual.Kind + "/" + actual.ProcessId + ".");
        }
    }
}
