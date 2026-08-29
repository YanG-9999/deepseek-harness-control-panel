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

        if (DirectoryCleanupPolicy.FallbackCommand != "rmdir /s /q")
            throw new InvalidOperationException("Directory cleanup must use junction-safe rmdir.");
        if (DirectoryCleanupPolicy.FallbackTimeoutMilliseconds <= 0)
            throw new InvalidOperationException("Directory cleanup must have a timeout.");
        if (DirectoryCleanupPolicy.FallbackAttempts < 2)
            throw new InvalidOperationException("Directory cleanup must retry transient Windows locks.");
        if (DirectoryCleanupPolicy.RetryDelayMilliseconds <= 0)
            throw new InvalidOperationException("Directory cleanup retries must have a short delay.");
        if (DirectoryCleanupPolicy.ToExtendedPath("D:\\DeepSeekHarness.dsh-backup") != "\\\\?\\D:\\DeepSeekHarness.dsh-backup")
            throw new InvalidOperationException("Local cleanup paths must use the Windows extended-length prefix.");
        if (DirectoryCleanupPolicy.ToExtendedPath("\\\\server\\share\\Harness") != "\\\\?\\UNC\\server\\share\\Harness")
            throw new InvalidOperationException("UNC cleanup paths must use the Windows extended-length prefix.");

        var process = new System.Diagnostics.ProcessStartInfo();
        BuildCommitEnvironment.Apply(process, "141eb6fef83422698aef7a981029e843e8161534");
        string commit = process.EnvironmentVariables["DSH_CLIENT_COMMIT_HASH"];
        if (commit != "141eb6fef83422698aef7a981029e843e8161534")
            throw new InvalidOperationException("The official source commit was not passed to the build environment.");
    }
}
