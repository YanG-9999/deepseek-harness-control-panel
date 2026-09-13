using System;

/// <summary>
/// Covers the rules that decide whether a finished operation is reported as a
/// cancellation or a failure, and the argument shape used to stop the child tree.
///
/// The wording rule matters more than it looks: reporting a deliberate cancellation
/// as an error would pop a scary dialog and send the user looking for a fault that
/// does not exist.
/// </summary>
public static class OperationCancellationPolicyTests
{
    public static void Run()
    {
        VerifyCancellationDetection();
        VerifyFailureReporting();
        VerifyKillRaceDetection();
        VerifyButtonText();
        Console.WriteLine("Operation cancellation policy tests passed.");
    }

    private static void VerifyCancellationDetection()
    {
        if (!OperationCancellationPolicy.IsCancellation(true, false))
            throw new InvalidOperationException("A requested cancellation must count even if the task completed normally.");
        if (!OperationCancellationPolicy.IsCancellation(false, true))
            throw new InvalidOperationException("A canceled task must count even if the request flag was not seen.");
        if (!OperationCancellationPolicy.IsCancellation(true, true))
            throw new InvalidOperationException("Both flags together must count as a cancellation.");
        if (OperationCancellationPolicy.IsCancellation(false, false))
            throw new InvalidOperationException("An ordinary completion is not a cancellation.");
    }

    private static void VerifyFailureReporting()
    {
        // A user cancellation must never raise an error dialog.
        if (OperationCancellationPolicy.ShouldReportAsFailure(true, true))
            throw new InvalidOperationException("A cancellation must not be reported as a failure.");
        if (OperationCancellationPolicy.ShouldReportAsFailure(true, false))
            throw new InvalidOperationException("A cancellation is never a failure.");

        // A real fault with no cancellation request still needs the dialog.
        if (!OperationCancellationPolicy.ShouldReportAsFailure(false, true))
            throw new InvalidOperationException("A genuine fault must still be reported.");
        if (OperationCancellationPolicy.ShouldReportAsFailure(false, false))
            throw new InvalidOperationException("A successful run is not a failure.");
    }

    private static void VerifyTaskkillArguments()
    {
        string arguments = OperationCancellationPolicy.BuildTaskkillArguments(1234);
        // /T is the whole point: pnpm spawns node, which spawns more.
        if (arguments != "/PID 1234 /T /F")
            throw new InvalidOperationException("Unexpected taskkill arguments: " + arguments);

        AssertThrows("zero pid", delegate { OperationCancellationPolicy.BuildTaskkillArguments(0); });
        AssertThrows("negative pid", delegate { OperationCancellationPolicy.BuildTaskkillArguments(-5); });
    }

    private static void VerifyKillRaceDetection()
    {
        // A process that exits between the HasExited check and the kill raises
        // InvalidOperationException; that is the ordinary race and must stay quiet.
        if (!OperationCancellationPolicy.IsExpectedKillRace(new InvalidOperationException("already exited")))
            throw new InvalidOperationException("An already-exited process must be treated as the ordinary race.");

        // Anything else is a real problem and must be surfaced.
        if (OperationCancellationPolicy.IsExpectedKillRace(new System.ComponentModel.Win32Exception(5)))
            throw new InvalidOperationException("An access-denied kill must be reported, not swallowed.");
        if (OperationCancellationPolicy.IsExpectedKillRace(new UnauthorizedAccessException()))
            throw new InvalidOperationException("An unauthorized kill must be reported, not swallowed.");
        if (OperationCancellationPolicy.IsExpectedKillRace(null))
            throw new InvalidOperationException("A null error is not a kill race.");
    }

    private static void VerifyButtonText()
    {
        if (String.IsNullOrWhiteSpace(OperationCancellationPolicy.CancelButtonText))
            throw new InvalidOperationException("The cancel affordance needs a label.");
        if (OperationCancellationPolicy.CancelButtonText == "安装")
            throw new InvalidOperationException("The cancel label must differ from the install label.");
        if (String.IsNullOrWhiteSpace(OperationCancellationPolicy.CancelledLogLine))
            throw new InvalidOperationException("A cancellation needs a log line.");
        if (OperationCancellationPolicy.CancelledLogLine.IndexOf("取消", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The cancellation log line must say it was cancelled, not that it failed.");
    }

    private static void AssertThrows(string label, Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }
        throw new InvalidOperationException("Expected an ArgumentOutOfRangeException for " + label + ".");
    }
}
