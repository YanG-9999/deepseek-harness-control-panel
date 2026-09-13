using System;

/// <summary>
/// Covers the version conversions the installer depends on.
///
/// The executable's version resource, the installer's version, and the Apps-and-features
/// entry must agree; a mismatch is how a package ends up unable to upgrade itself. The
/// four-field conversion matters because the Win32 version resource is numeric only, so a
/// pre-release suffix has to be dropped rather than passed through.
/// </summary>
public static class PanelVersionPolicyTests
{
    public static void Run()
    {
        VerifyDeclaredVersion();
        VerifyReleaseMetadataAgrees();
        VerifyNumericConversion();
        VerifyNumericRejections();
        VerifyPackageName();
        VerifyHarnessVersionDisplay();
        Console.WriteLine("Panel version policy tests passed.");
    }

    private static void VerifyDeclaredVersion()
    {
        string version = PanelVersionPolicy.Version;
        if (String.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("The panel must declare a version.");
        // It has to be convertible, or the installer build would fail later.
        PanelVersionPolicy.ToNumericVersion(version);
    }

    /// <summary>
    /// The version is written in four places: the policy constant plus AssemblyVersion,
    /// AssemblyFileVersion, and AssemblyInformationalVersion. Only the constant is read by
    /// the build scripts, so the attributes drift silently when a release forgets them -
    /// and that drift is exactly what makes an installed copy refuse to upgrade itself.
    /// </summary>
    private static void VerifyReleaseMetadataAgrees()
    {
        string expected = PanelVersionPolicy.ToNumericVersion(PanelVersionPolicy.Version);
        System.Reflection.Assembly panel = typeof(PanelVersionPolicy).Assembly;

        string assemblyVersion = panel.GetName().Version.ToString();
        if (assemblyVersion != expected)
            throw new InvalidOperationException(
                "AssemblyVersion is " + assemblyVersion + ", but the declared version " +
                PanelVersionPolicy.Version + " needs " + expected + ".");

        string location = panel.Location;
        if (String.IsNullOrEmpty(location) || !System.IO.File.Exists(location))
            return;

        System.Diagnostics.FileVersionInfo info = System.Diagnostics.FileVersionInfo.GetVersionInfo(location);
        if (info.FileVersion != expected)
            throw new InvalidOperationException(
                "The file version resource is " + info.FileVersion + ", but the declared version " +
                PanelVersionPolicy.Version + " needs " + expected + ".");
        if (info.ProductVersion != PanelVersionPolicy.Version)
            throw new InvalidOperationException(
                "The informational version is " + info.ProductVersion + ", but the declared version is " +
                PanelVersionPolicy.Version + ".");
    }

    private static void VerifyNumericConversion()
    {
        if (PanelVersionPolicy.ToNumericVersion("0.1.0") != "0.1.0.0")
            throw new InvalidOperationException("A three-part version must gain a fourth field.");
        if (PanelVersionPolicy.ToNumericVersion("1") != "1.0.0.0")
            throw new InvalidOperationException("A one-part version must be padded.");
        if (PanelVersionPolicy.ToNumericVersion("1.2") != "1.2.0.0")
            throw new InvalidOperationException("A two-part version must be padded.");
        if (PanelVersionPolicy.ToNumericVersion("1.2.3.4") != "1.2.3.4")
            throw new InvalidOperationException("A four-part version must pass through.");
        if (PanelVersionPolicy.ToNumericVersion("  1.2.3  ") != "1.2.3.0")
            throw new InvalidOperationException("Surrounding whitespace must be tolerated.");

        // A pre-release suffix is not numeric, so it must be dropped rather than emitted.
        if (PanelVersionPolicy.ToNumericVersion("0.1.5-rc.1") != "0.1.5.0")
            throw new InvalidOperationException("A pre-release suffix must be dropped: " + PanelVersionPolicy.ToNumericVersion("0.1.5-rc.1"));
        if (PanelVersionPolicy.ToNumericVersion("0.1.5-alpha.2") != "0.1.5.0")
            throw new InvalidOperationException("An alpha suffix must be dropped.");
        if (PanelVersionPolicy.ToNumericVersion("0.1.5+build.7") != "0.1.5.0")
            throw new InvalidOperationException("Build metadata must be dropped.");

        // More than four fields cannot fit the resource, so the extra ones are trimmed.
        if (PanelVersionPolicy.ToNumericVersion("1.2.3.4.5") != "1.2.3.4")
            throw new InvalidOperationException("A five-part version must be trimmed to four.");

        // The result must always be parseable as a Version.
        Version parsed;
        if (!Version.TryParse(PanelVersionPolicy.ToNumericVersion(PanelVersionPolicy.Version), out parsed))
            throw new InvalidOperationException("The converted version must be a valid System.Version.");
    }

    private static void VerifyNumericRejections()
    {
        AssertThrows("blank version", delegate { PanelVersionPolicy.ToNumericVersion(""); });
        AssertThrows("null version", delegate { PanelVersionPolicy.ToNumericVersion(null); });
        AssertThrows("whitespace version", delegate { PanelVersionPolicy.ToNumericVersion("   "); });
        AssertThrows("non-numeric part", delegate { PanelVersionPolicy.ToNumericVersion("1.x.3"); });
        AssertThrows("negative part", delegate { PanelVersionPolicy.ToNumericVersion("1.-2.3"); });
        // A suffix-only value has no numeric core at all.
        AssertThrows("suffix only", delegate { PanelVersionPolicy.ToNumericVersion("-rc.1"); });
    }

    private static void VerifyPackageName()
    {
        string name = PanelVersionPolicy.BuildInstallerFileName("0.1.0");
        if (name != "DeepSeekHarnessControlPanel-0.1.0-setup.exe")
            throw new InvalidOperationException("Unexpected package name: " + name);
        // A pre-release keeps its suffix in the file name; only the resource drops it.
        string preRelease = PanelVersionPolicy.BuildInstallerFileName("0.1.5-rc.1");
        if (preRelease != "DeepSeekHarnessControlPanel-0.1.5-rc.1-setup.exe")
            throw new InvalidOperationException("A pre-release package name must keep its suffix: " + preRelease);
        // No timestamp: a release must replace its predecessor rather than pile up.
        if (name.IndexOf(DateTime.Now.Year.ToString(), StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The package name must not carry a date.");

        AssertThrows("blank package version", delegate { PanelVersionPolicy.BuildInstallerFileName(""); });
        AssertThrows("null package version", delegate { PanelVersionPolicy.BuildInstallerFileName(null); });
    }

    /// <summary>
    /// The row labelled "Harness 版本" must show a version number and nothing else.
    /// It previously carried the update-check result, which overwrote the version with
    /// "already up to date" exactly when the user wanted to read the version.
    /// </summary>
    private static void VerifyHarnessVersionDisplay()
    {
        if (HarnessVersionText.ForDisplay("0.1.5-rc.2") != "v0.1.5-rc.2")
            throw new InvalidOperationException("The installed version must be shown with a leading v.");
        if (HarnessVersionText.ForDisplay("0.1.5") != "v0.1.5")
            throw new InvalidOperationException("A release version must be shown with a leading v.");
        if (HarnessVersionText.ForDisplay("  0.1.5-rc.2  ") != "v0.1.5-rc.2")
            throw new InvalidOperationException("Surrounding whitespace must be trimmed.");

        // Never double the prefix if a future source already carries one.
        if (HarnessVersionText.ForDisplay("v0.1.5") != "v0.1.5")
            throw new InvalidOperationException("An existing v prefix must not be doubled.");
        if (HarnessVersionText.ForDisplay("V0.1.5") != "V0.1.5")
            throw new InvalidOperationException("An existing uppercase prefix must not be doubled.");

        // An unknown version must read as blank, never as a made-up version number.
        if (HarnessVersionText.ForDisplay("") != "" || HarnessVersionText.ForDisplay(null) != "")
            throw new InvalidOperationException("An unknown version must be blank.");
        if (HarnessVersionText.ForDisplay("   ") != "")
            throw new InvalidOperationException("A whitespace version must be blank.");
        if (HarnessVersionText.ForDisplay("未知") != "v未知")
            throw new InvalidOperationException("A non-empty placeholder still gets the prefix; callers pass blank instead.");

        // The update hint and the version must not share a field: the status line is
        // where the hint belongs.
        if (HarnessVersionText.ForDisplay("0.1.5-rc.2").IndexOf("最新", StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The version display must not contain update wording.");
    }

    private static void AssertThrows(string label, Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("Expected an InvalidOperationException for " + label + ".");
    }
}
