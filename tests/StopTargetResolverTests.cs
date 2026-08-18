using System;

public static class StopTargetResolverTests
{
    public static int Main()
    {
        UninstallTargetPlannerTests.Run();

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
        return 0;
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
