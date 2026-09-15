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

        string installCommand = HarnessInstallPolicy.BuildDependencyInstallCommand(true);
        if (!HarnessInstallPolicy.HasFrozenLockfile(installCommand))
            throw new InvalidOperationException("Harness dependency installation must use the frozen official lockfile.");
        if (HarnessInstallPolicy.HasFrozenLockfile(HarnessInstallPolicy.BuildDependencyInstallCommand(false)))
            throw new InvalidOperationException("A plain pnpm install must not be treated as frozen.");
        if (installCommand.Contains("--ignore-scripts"))
            throw new InvalidOperationException("Windows compatibility must not suppress unrelated official install scripts.");
        if (HarnessInstallPolicy.LockfileContainsPackage("packages:\n  react@18.3.1:\n", "fs-ext"))
            throw new InvalidOperationException("An unrelated lockfile package must not match fs-ext.");
        if (!HarnessInstallPolicy.LockfileContainsPackage("packages:\n  fs-ext@2.1.1:\n", "fs-ext"))
            throw new InvalidOperationException("A package present in the lockfile was not detected.");
        string nativeHint = HarnessInstallPolicy.BuildInstallFailureHint(
            installCommand,
            "fs-ext@2.1.1\nnode-gyp rebuild\ngyp ERR! not ok",
            false);
        if (String.IsNullOrEmpty(nativeHint) || !nativeHint.Contains("官方源码没有声明"))
            throw new InvalidOperationException("The unexpected native dependency failure needs a clear Chinese diagnosis.");
        if (!String.IsNullOrEmpty(HarnessInstallPolicy.BuildInstallFailureHint(
            "pnpm run build", "node-gyp rebuild", false)))
            throw new InvalidOperationException("Native build failures outside dependency installation must not get the install diagnosis.");

        const string upstreamLease = "import { join } from 'node:path'\n" +
            "import { flock } from 'fs-ext'\n\n" +
            "/** Base name of the kernel lock file inside a session's directory. */\n" +
            "function flockAsync(fd: number, flags: 'exnb' | 'un'): Promise<void> {\n" +
            "  return new Promise((resolve, reject) => {\n" +
            "    flock(fd, flags, (error) => {\n";
        string patchedLease = HarnessInstallPolicy.ApplyWindowsFsExtLeaseCompatibility(upstreamLease);
        if (patchedLease.Contains("import { flock } from 'fs-ext'") ||
            !patchedLease.Contains("createRequire(import.meta.url)") ||
            !patchedLease.Contains("getDshFlock()(fd, flags"))
            throw new InvalidOperationException("Windows fs-ext compatibility patch did not defer the POSIX-only module load.");
        if (HarnessInstallPolicy.ApplyWindowsFsExtLeaseCompatibility(patchedLease) != patchedLease)
            throw new InvalidOperationException("Windows fs-ext lease compatibility patch must be idempotent.");

        const string upstreamWorkspace = "allowBuilds:\n  esbuild: true\n  fs-ext: true\n  node-pty: true\n";
        string patchedWorkspace = HarnessInstallPolicy.DisableFsExtBuildScript(upstreamWorkspace);
        if (!patchedWorkspace.Contains("fs-ext: false") || !patchedWorkspace.Contains("node-pty: true"))
            throw new InvalidOperationException("Windows compatibility must disable only fs-ext's native build script.");
        if (HarnessInstallPolicy.DisableFsExtBuildScript(patchedWorkspace) != patchedWorkspace)
            throw new InvalidOperationException("The fs-ext build policy patch must be idempotent.");

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

        // Passing the public build values must never throw. The inherited environment can
        // hold case-insensitive duplicate keys (HTTP_PROXY plus http_proxy), and touching
        // ProcessStartInfo.EnvironmentVariables then throws ArgumentException; these values
        // are diagnostic metadata for the build, so degrading to "not set" is the correct
        // outcome rather than a failed build.
        var process = new System.Diagnostics.ProcessStartInfo();
        bool applied;
        try
        {
            applied = BuildCommitEnvironment.Apply(process, "141eb6fef83422698aef7a981029e843e8161534", "0.1.5-rc.2");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                "Applying the build commit must never throw, but it threw " +
                error.GetType().Name + ": " + error.Message);
        }

        // The outcome is environment-dependent, so assert against what this machine's
        // environment actually allows rather than assuming the simple case.
        bool duplicateKeys = BuildCommitEnvironment.HasCaseInsensitiveDuplicateKeys();
        if (duplicateKeys)
        {
            if (applied)
                throw new InvalidOperationException(
                    "The commit must be dropped, not recorded, when the inherited environment has case-duplicate keys.");
            Console.WriteLine("  (build commit dropped as designed: environment has case-duplicate keys)");
        }
        else
        {
            if (!applied)
                throw new InvalidOperationException("The commit was not recorded in an environment that can hold it.");
            string commit = process.EnvironmentVariables[BuildCommitEnvironment.VariableName];
            if (commit != "141eb6fef83422698aef7a981029e843e8161534")
                throw new InvalidOperationException("The official source commit was not passed to the build environment.");
            string version = process.EnvironmentVariables[BuildCommitEnvironment.VersionVariableName];
            if (version != "0.1.5-rc.2")
                throw new InvalidOperationException("The official source version was not passed to the build environment.");
        }

        // A null process or blank value must be tolerated by the same entry point.
        if (BuildCommitEnvironment.Apply(null, "141eb6fef83422698aef7a981029e843e8161534", "0.1.5"))
            throw new InvalidOperationException("Applying to a null process must report that nothing was recorded.");
        if (BuildCommitEnvironment.Apply(new System.Diagnostics.ProcessStartInfo(), "  ", "  "))
            throw new InvalidOperationException("Applying a blank commit must report that nothing was recorded.");

        // Only known values may be written: an empty string would satisfy the official
        // profile's presence check and embed a blank brand into the client artifacts.
        if (!duplicateKeys)
        {
            var partial = new System.Diagnostics.ProcessStartInfo();
            if (!BuildCommitEnvironment.Apply(partial, "141eb6fef83422698aef7a981029e843e8161534", ""))
                throw new InvalidOperationException("A known commit with an unknown version is still worth recording.");
            if (partial.EnvironmentVariables.ContainsKey(BuildCommitEnvironment.VersionVariableName))
                throw new InvalidOperationException("An unknown version must not be written as an empty value.");
        }
    }
}
