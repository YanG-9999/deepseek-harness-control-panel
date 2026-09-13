using System;

/// <summary>
/// Covers port validation and the strings built from a port.
///
/// The validation rules matter because an unusable port would leave the panel unable
/// to probe the service it just launched, and because ports under 1024 cannot be bound
/// without the administrator rights this panel deliberately never requests.
/// </summary>
public static class HarnessPortPolicyTests
{
    public static void Run()
    {
        VerifyValidation();
        VerifyParsing();
        VerifyLocalWebUri();
        VerifyPortArgument();
        VerifyLaunchArguments();
        Console.WriteLine("Harness port policy tests passed.");
    }

    private static void VerifyValidation()
    {
        if (!HarnessPortPolicy.IsValid(HarnessPortPolicy.DefaultPort))
            throw new InvalidOperationException("The default port must be valid.");
        if (!HarnessPortPolicy.IsValid(HarnessPortPolicy.MinimumPort))
            throw new InvalidOperationException("The lowest allowed port must be valid.");
        if (!HarnessPortPolicy.IsValid(HarnessPortPolicy.MaximumPort))
            throw new InvalidOperationException("The highest allowed port must be valid.");

        if (HarnessPortPolicy.IsValid(HarnessPortPolicy.MinimumPort - 1))
            throw new InvalidOperationException("A privileged port must be rejected: elevator rights are never requested.");
        if (HarnessPortPolicy.IsValid(0))
            throw new InvalidOperationException("Port zero is not a usable configured port.");
        if (HarnessPortPolicy.IsValid(-1))
            throw new InvalidOperationException("A negative port must be rejected.");
        if (HarnessPortPolicy.IsValid(HarnessPortPolicy.MaximumPort + 1))
            throw new InvalidOperationException("A port above 65535 must be rejected.");
    }

    private static void VerifyParsing()
    {
        int port;
        string error;

        if (!HarnessPortPolicy.TryParse("8080", out port, out error) || port != 8080)
            throw new InvalidOperationException("A plain port must parse.");
        if (!HarnessPortPolicy.TryParse("  8080  ", out port, out error) || port != 8080)
            throw new InvalidOperationException("Surrounding whitespace must be tolerated.");
        if (!HarnessPortPolicy.TryParse("65535", out port, out error) || port != 65535)
            throw new InvalidOperationException("The maximum port must parse.");

        // Each rejection must explain itself rather than silently defaulting.
        if (HarnessPortPolicy.TryParse("", out port, out error) || String.IsNullOrEmpty(error))
            throw new InvalidOperationException("An empty value must be rejected with a reason.");
        if (HarnessPortPolicy.TryParse("abc", out port, out error) || String.IsNullOrEmpty(error))
            throw new InvalidOperationException("Non-numeric text must be rejected with a reason.");
        if (HarnessPortPolicy.TryParse("80", out port, out error) || error.IndexOf("管理员", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A privileged port must be rejected and the reason must mention elevation.");
        if (HarnessPortPolicy.TryParse("70000", out port, out error) || String.IsNullOrEmpty(error))
            throw new InvalidOperationException("An out-of-range port must be rejected with a reason.");
        if (HarnessPortPolicy.TryParse(null, out port, out error) || String.IsNullOrEmpty(error))
            throw new InvalidOperationException("A null value must be rejected with a reason.");
    }

    private static void VerifyLocalWebUri()
    {
        if (HarnessPortPolicy.BuildLocalWebUri(8080) != "http://127.0.0.1:8080/")
            throw new InvalidOperationException("The local URI must carry the configured port.");
        if (HarnessPortPolicy.BuildLocalWebUri(HarnessPortPolicy.DefaultPort) != "http://127.0.0.1:3080/")
            throw new InvalidOperationException("The default URI must be unchanged from the previous hard-coded value.");
        // An invalid port would produce an address the panel cannot probe.
        AssertThrows("zero port URI", delegate { HarnessPortPolicy.BuildLocalWebUri(0); });
        AssertThrows("privileged port URI", delegate { HarnessPortPolicy.BuildLocalWebUri(80); });
    }

    private static void VerifyPortArgument()
    {
        if (HarnessPortPolicy.BuildPortArgument(8080) != "--port 8080")
            throw new InvalidOperationException("The port argument must be passed to the web launch.");
        AssertThrows("zero port argument", delegate { HarnessPortPolicy.BuildPortArgument(0); });
    }

    /// <summary>
    /// The launch line must actually carry the port, otherwise Harness would listen on
    /// its own default while the panel probes the configured one.
    /// </summary>
    private static void VerifyLaunchArguments()
    {
        string arguments = HarnessStartupPolicy.BuildWebArguments(8080);
        if (arguments.IndexOf("web", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The web subcommand must still be present.");
        if (arguments.IndexOf("--no-open", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The panel opens the browser itself, so --no-open must stay.");
        if (arguments.IndexOf("--port 8080", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The launch arguments must carry the configured port: " + arguments);

        string defaultArguments = HarnessStartupPolicy.BuildWebArguments(HarnessPortPolicy.DefaultPort);
        if (defaultArguments.IndexOf("--port 3080", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The default launch must still specify 3080 explicitly.");
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
