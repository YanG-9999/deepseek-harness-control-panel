using System;

/// <summary>
/// Covers the build-profile choice the installer and updater both depend on.
///
/// The brand shown in the browser is compiled into the client artifacts, so building
/// without the official profile silently replaces it with the local-build fallback
/// after every update. These checks pin the selection rules: the official profile is
/// used only when the source really declares it and both public values the profile
/// demands are known, because a wrong choice here either loses the brand or fails an
/// install that the plain build would have completed.
/// </summary>
public static class HarnessBuildPolicyTests
{
    private const string OfficialManifest =
        "{ \"name\": \"dsh-root\", \"scripts\": { \"build\": \"tsx scripts/build.ts\", " +
        "\"build:official\": \"tsx scripts/build.ts --profile official\" } }";

    private const string PlainManifest =
        "{ \"name\": \"dsh-root\", \"scripts\": { \"build\": \"tsx scripts/build.ts\" } }";

    public static void Run()
    {
        VerifyOfficialProfileIsPreferred();
        VerifyFallbacks();
        VerifyCommandRecognition();
        VerifyRetryPolicy();
        Console.WriteLine("Harness build policy tests passed.");
    }

    private static void VerifyOfficialProfileIsPreferred()
    {
        string command = HarnessBuildPolicy.ChooseCommand(OfficialManifest, "c291e7961a515f6d7af9304e7fd1d257929aef26", "0.1.5-rc.2");
        if (command != HarnessBuildPolicy.OfficialCommand)
            throw new InvalidOperationException("A source that declares build:official must be built with it, got: " + command);

        // A short commit prefix is what the build itself accepts, so it must be accepted here.
        if (HarnessBuildPolicy.ChooseCommand(OfficialManifest, "c291e79", "0.1.5") != HarnessBuildPolicy.OfficialCommand)
            throw new InvalidOperationException("A seven-character commit prefix must be enough.");
    }

    private static void VerifyFallbacks()
    {
        // Older sources have no such script; asking for it would fail the whole install.
        if (HarnessBuildPolicy.ChooseCommand(PlainManifest, "c291e79", "0.1.5") != HarnessBuildPolicy.PlainCommand)
            throw new InvalidOperationException("A source without build:official must fall back to the plain build.");

        // The official profile refuses to run without a commit, so an unknown one must
        // not be smuggled through as an empty value.
        if (HarnessBuildPolicy.ChooseCommand(OfficialManifest, "", "0.1.5") != HarnessBuildPolicy.PlainCommand)
            throw new InvalidOperationException("An unknown commit must fall back to the plain build.");
        if (HarnessBuildPolicy.ChooseCommand(OfficialManifest, null, "0.1.5") != HarnessBuildPolicy.PlainCommand)
            throw new InvalidOperationException("A null commit must fall back to the plain build.");
        if (HarnessBuildPolicy.ChooseCommand(OfficialManifest, "not-a-commit", "0.1.5") != HarnessBuildPolicy.PlainCommand)
            throw new InvalidOperationException("A non-hash commit must fall back to the plain build.");

        // An unreadable version would make the official build throw before compiling.
        if (HarnessBuildPolicy.ChooseCommand(OfficialManifest, "c291e79", "") != HarnessBuildPolicy.PlainCommand)
            throw new InvalidOperationException("A blank version must fall back to the plain build.");
        if (HarnessBuildPolicy.ChooseCommand(OfficialManifest, "c291e79", "   ") != HarnessBuildPolicy.PlainCommand)
            throw new InvalidOperationException("A whitespace version must fall back to the plain build.");

        if (HarnessBuildPolicy.ChooseCommand("", "c291e79", "0.1.5") != HarnessBuildPolicy.PlainCommand)
            throw new InvalidOperationException("A missing manifest must fall back to the plain build.");
        if (HarnessBuildPolicy.ChooseCommand(null, "c291e79", "0.1.5") != HarnessBuildPolicy.PlainCommand)
            throw new InvalidOperationException("A null manifest must fall back to the plain build.");
    }

    private static void VerifyCommandRecognition()
    {
        if (!HarnessBuildPolicy.IsBuildCommand("pnpm run build"))
            throw new InvalidOperationException("The plain build command must be recognised.");
        if (!HarnessBuildPolicy.IsBuildCommand("PNPM RUN BUILD:OFFICIAL"))
            throw new InvalidOperationException("Command recognition must ignore case.");
        if (HarnessBuildPolicy.IsBuildCommand("pnpm install --frozen-lockfile"))
            throw new InvalidOperationException("An install is not a build and must not be retried as one.");
        if (HarnessBuildPolicy.IsBuildCommand(""))
            throw new InvalidOperationException("An empty command is not a build.");

        // The profile lives in the script name, so a near miss must not count.
        if (HarnessBuildPolicy.DeclaresOfficialProfile(PlainManifest))
            throw new InvalidOperationException("A source without the script must not claim the profile.");
        if (!HarnessBuildPolicy.DeclaresOfficialProfile(OfficialManifest))
            throw new InvalidOperationException("The declared script must be found.");
        if (HarnessBuildPolicy.DeclaresOfficialProfile("{\"build:official-note\": \"text\"}"))
            throw new InvalidOperationException("Only a real script key may count.");
    }

    private static void VerifyRetryPolicy()
    {
        // The retry exists for a first-build flake, so it has to cover whichever build
        // command the panel picked; it previously knew only the plain one.
        if (!BuildRetryPolicy.ShouldRetry(HarnessBuildPolicy.OfficialCommand, 1))
            throw new InvalidOperationException("The first official build must be retried once.");
        if (!BuildRetryPolicy.ShouldRetry(HarnessBuildPolicy.PlainCommand, 1))
            throw new InvalidOperationException("The first plain build must still be retried once.");
        if (BuildRetryPolicy.ShouldRetry(HarnessBuildPolicy.OfficialCommand, 2))
            throw new InvalidOperationException("Only the first attempt may be retried.");
        if (BuildRetryPolicy.ShouldRetry("pnpm install --frozen-lockfile", 1))
            throw new InvalidOperationException("An install must not be retried by the build policy.");
    }
}
