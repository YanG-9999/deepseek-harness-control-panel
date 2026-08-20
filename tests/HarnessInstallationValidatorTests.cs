using System;
using System.Collections.Generic;

public static class HarnessInstallationValidatorTests
{
    public static void Run()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in HarnessInstallationValidator.RequiredFiles)
            present.Add(path);

        List<string> complete = HarnessInstallationValidator.FindMissingFiles(path => present.Contains(path));
        if (complete.Count != 0)
            throw new InvalidOperationException("A complete Harness source tree was marked incomplete.");

        present.Remove("packages/session-query/tool-session-query/package.json");
        List<string> missing = HarnessInstallationValidator.FindMissingFiles(path => present.Contains(path));
        if (missing.Count != 1 || missing[0] != "packages/session-query/tool-session-query/package.json")
            throw new InvalidOperationException("The missing workspace package was not detected.");

        if (!BuildRetryPolicy.ShouldRetry("pnpm run build", 1))
            throw new InvalidOperationException("The first Harness build failure should be retried.");
        if (BuildRetryPolicy.ShouldRetry("pnpm run build", 2))
            throw new InvalidOperationException("The Harness build should not retry more than once.");
        if (BuildRetryPolicy.ShouldRetry("pnpm install", 1))
            throw new InvalidOperationException("Non-build commands should not use the build retry policy.");
    }
}
