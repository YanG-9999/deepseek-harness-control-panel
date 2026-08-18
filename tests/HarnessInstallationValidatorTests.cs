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
    }
}
