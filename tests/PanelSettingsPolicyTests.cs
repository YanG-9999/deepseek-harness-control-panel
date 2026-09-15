using System;
using System.Collections.Generic;

/// <summary>
/// Covers the settings merge rule.
///
/// The writer keeps only the keys it knows, so a key that is missing from the known
/// list is erased the next time a different key is saved. That happened for real: the
/// daily update-check marker was dropped whenever the install root was written, which
/// silently turned the "one check per day" limit back into "one check per launch".
/// </summary>
public static class PanelSettingsPolicyTests
{
    public static void Run()
    {
        VerifyKnownKeysCoverEverySetting();
        VerifySavingOneKeyKeepsTheOthers();
        VerifyBlankValuesAreNotCarried();
        VerifyMissingEntriesAreTolerated();
        VerifyKeyNamesAreCaseInsensitive();
        VerifyKeyIsRequired();
        Console.WriteLine("Panel settings policy tests passed.");
    }

    private static void VerifyKnownKeysCoverEverySetting()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in PanelSettingsPolicy.KnownKeys)
        {
            if (String.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("A known settings key must not be blank.");
            if (!seen.Add(key))
                throw new InvalidOperationException("A known settings key is listed twice: " + key);
        }

        // Both settings the panel actually uses have to be listed, or writing one
        // erases the other.
        foreach (string required in new[]
        {
            PanelSettingsPolicy.InstallRootKey,
            PanelSettingsPolicy.LastAutoUpdateCheckDateKey
        })
        {
            if (!seen.Contains(required))
                throw new InvalidOperationException("The known settings keys must include " + required + ".");
        }
    }

    private static void VerifySavingOneKeyKeepsTheOthers()
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        existing[PanelSettingsPolicy.InstallRootKey] = "C:\\dsh";
        existing[PanelSettingsPolicy.LastAutoUpdateCheckDateKey] = "2026-09-14";

        // Saving the install root must not erase the update-check marker, and vice versa.
        Dictionary<string, string> afterRootWrite = PanelSettingsPolicy.Merge(
            existing, PanelSettingsPolicy.InstallRootKey, "D:\\Harness");
        if (afterRootWrite[PanelSettingsPolicy.InstallRootKey] != "D:\\Harness")
            throw new InvalidOperationException("The written key must win.");
        if (afterRootWrite[PanelSettingsPolicy.LastAutoUpdateCheckDateKey] != "2026-09-14")
            throw new InvalidOperationException("Writing the install root must keep the daily update-check marker.");

        Dictionary<string, string> afterDateWrite = PanelSettingsPolicy.Merge(
            existing, PanelSettingsPolicy.LastAutoUpdateCheckDateKey, "2026-09-15");
        if (afterDateWrite[PanelSettingsPolicy.LastAutoUpdateCheckDateKey] != "2026-09-15")
            throw new InvalidOperationException("The written key must win.");
        if (afterDateWrite[PanelSettingsPolicy.InstallRootKey] != "C:\\dsh")
            throw new InvalidOperationException("Writing the daily marker must keep the install root.");
    }

    private static void VerifyBlankValuesAreNotCarried()
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        existing[PanelSettingsPolicy.InstallRootKey] = "";
        existing[PanelSettingsPolicy.LastAutoUpdateCheckDateKey] = "   ";

        Dictionary<string, string> merged = PanelSettingsPolicy.Merge(
            existing, PanelSettingsPolicy.LastAutoUpdateCheckDateKey, "2026-09-15");
        if (merged.ContainsKey(PanelSettingsPolicy.InstallRootKey))
            throw new InvalidOperationException("A blank install root must not be written back as a value.");
        if (merged[PanelSettingsPolicy.LastAutoUpdateCheckDateKey] != "2026-09-15")
            throw new InvalidOperationException("The written value must survive blank neighbours.");
    }

    private static void VerifyMissingEntriesAreTolerated()
    {
        // First write on a fresh machine: no settings file, no existing entries.
        foreach (IDictionary<string, string> empty in new IDictionary<string, string>[] { null, new Dictionary<string, string>() })
        {
            Dictionary<string, string> merged = PanelSettingsPolicy.Merge(
                empty, PanelSettingsPolicy.InstallRootKey, "D:\\deepseek-harness");
            if (merged.Count != 1 || merged[PanelSettingsPolicy.InstallRootKey] != "D:\\deepseek-harness")
                throw new InvalidOperationException("A first write must produce exactly the written key.");
        }

        // A null value is stored as empty rather than crashing the write.
        Dictionary<string, string> nullValue = PanelSettingsPolicy.Merge(null, PanelSettingsPolicy.InstallRootKey, null);
        if (nullValue[PanelSettingsPolicy.InstallRootKey] != "")
            throw new InvalidOperationException("A null value must be stored as an empty string.");
    }

    private static void VerifyKeyNamesAreCaseInsensitive()
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        existing["INSTALLROOT"] = "C:\\dsh";

        Dictionary<string, string> merged = PanelSettingsPolicy.Merge(
            existing, PanelSettingsPolicy.LastAutoUpdateCheckDateKey, "2026-09-15");
        if (!merged.ContainsKey(PanelSettingsPolicy.InstallRootKey))
            throw new InvalidOperationException("A differently cased existing key must still be carried over.");
    }

    private static void VerifyKeyIsRequired()
    {
        foreach (string bad in new[] { null, "", "   " })
        {
            try
            {
                PanelSettingsPolicy.Merge(null, bad, "value");
            }
            catch (ArgumentException)
            {
                continue;
            }
            throw new InvalidOperationException("A blank settings key must be rejected.");
        }
    }
}
