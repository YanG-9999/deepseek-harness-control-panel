using System;
using System.Collections.Generic;
using System.IO;

public static class PluginMarketplacePolicyTests
{
    public static int Main()
    {
        Run();
        return 0;
    }

    public static void Run()
    {
        VerifySpecClassification();
        VerifyPrepareScriptDetection();
        VerifyPackageNameSafety();
        VerifyInstallRiskText();
        VerifyCatalog();
        VerifyGitHubSearchUrl();
        VerifyGitHubSearchParsing();
        VerifyProfileManifest();
        Console.WriteLine("Plugin marketplace policy tests passed.");
    }

    private static void VerifySpecClassification()
    {
        AssertKind("bare name", "dsh-knowledge", PluginSpecKind.NpmPackage);
        AssertKind("scoped name", "@deepseek-ai/dsh-subagent-codex", PluginSpecKind.NpmPackage);
        AssertKind("tagged name", "turtle-ui@2.0.0", PluginSpecKind.NpmPackage);
        AssertKind("github shorthand", "github:owner/repo", PluginSpecKind.GitHubRepository);
        AssertKind("git+ prefix", "git+https://example.com/x.git", PluginSpecKind.GitHubRepository);
        AssertKind("github url", "https://github.com/owner/repo", PluginSpecKind.GitHubRepository);
        AssertKind("windows path", @"C:\work\my-plugin", PluginSpecKind.LocalDirectory);
        AssertKind("relative path", "./hello-plugin", PluginSpecKind.LocalDirectory);
        AssertKind("file: prefix", "file:C:/work/plugin", PluginSpecKind.LocalDirectory);
        AssertKind("tarball path", @"C:\work\plugin-0.1.0.tgz", PluginSpecKind.Tarball);
        AssertKind("bare tarball", "plugin-0.1.0.tgz", PluginSpecKind.Tarball);
        AssertThrows("empty spec", delegate { PluginSpecPolicy.Classify(""); });
        AssertThrows("null spec", delegate { PluginSpecPolicy.Classify(null); });
    }

    private static void VerifyPrepareScriptDetection()
    {
        // Mirrors the dsh CLI's own regex in apps/cli/src/plugin.ts.
        if (!PluginSpecPolicy.TriggersPrepareScript("github:owner/repo"))
            throw new InvalidOperationException("A github: spec must be flagged as needing build authorization.");
        if (!PluginSpecPolicy.TriggersPrepareScript("git+https://x/y.git"))
            throw new InvalidOperationException("A git+ spec must be flagged as needing build authorization.");
        if (!PluginSpecPolicy.TriggersPrepareScript("https://example.com/y.git"))
            throw new InvalidOperationException("A .git URL must be flagged as needing build authorization.");
        if (!PluginSpecPolicy.TriggersPrepareScript("https://example.com/y.git#abc123"))
            throw new InvalidOperationException("A .git URL with a commit must be flagged as needing build authorization.");
        if (PluginSpecPolicy.TriggersPrepareScript("@deepseek-ai/dsh-subagent-codex"))
            throw new InvalidOperationException("An npm package does not run a prepare script on install.");
        if (PluginSpecPolicy.TriggersPrepareScript(@".\plugin-1.0.0.tgz"))
            throw new InvalidOperationException("A local tarball does not run a prepare script on install.");

        if (!PluginSpecPolicy.RequiresBuildAuthorization("github:owner/repo"))
            throw new InvalidOperationException("A git source must require build authorization.");
        if (PluginSpecPolicy.RequiresBuildAuthorization("dsh-knowledge"))
            throw new InvalidOperationException("An npm source must not require build authorization.");
    }

    private static void VerifyPackageNameSafety()
    {
        if (!PluginSpecPolicy.IsSafePackageName("dsh-knowledge"))
            throw new InvalidOperationException("A plain package name is safe.");
        if (!PluginSpecPolicy.IsSafePackageName("@deepseek-ai/dsh-subagent-codex"))
            throw new InvalidOperationException("A scoped package name is safe.");
        if (!PluginSpecPolicy.IsSafePackageName("turtle-ui@2.0.0"))
            throw new InvalidOperationException("A tagged package name is safe.");
        if (!PluginSpecPolicy.IsSafePackageName("@scope/name@^1.2.3"))
            throw new InvalidOperationException("A ranged package name is safe.");

        // Anything that could inject extra pnpm arguments must be refused.
        if (PluginSpecPolicy.IsSafePackageName("--global"))
            throw new InvalidOperationException("A leading dash must be refused.");
        if (PluginSpecPolicy.IsSafePackageName("pkg --registry http://evil"))
            throw new InvalidOperationException("A space must be refused.");
        if (PluginSpecPolicy.IsSafePackageName("pkg; rm -rf /"))
            throw new InvalidOperationException("A shell separator must be refused.");
        if (PluginSpecPolicy.IsSafePackageName("pkg && calc"))
            throw new InvalidOperationException("A command separator must be refused.");
        if (PluginSpecPolicy.IsSafePackageName("pkg|calc"))
            throw new InvalidOperationException("A pipe must be refused.");
        if (PluginSpecPolicy.IsSafePackageName(""))
            throw new InvalidOperationException("An empty name must be refused.");
        if (PluginSpecPolicy.IsSafePackageName(null))
            throw new InvalidOperationException("A null name must be refused.");
    }

    private static void VerifyInstallRiskText()
    {
        // The git case carries the only install-time code execution warning.
        string git = PluginSpecPolicy.DescribeInstallRisk("github:owner/repo");
        if (git.IndexOf("构建脚本", StringComparison.Ordinal) < 0 ||
            git.IndexOf("不在任何沙箱内", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The git risk text must warn about out-of-sandbox build scripts.");
        if (git.IndexOf("锁定 commit", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The git risk text must recommend pinning a commit.");

        string npm = PluginSpecPolicy.DescribeInstallRisk("dsh-knowledge");
        if (npm.IndexOf("不会运行构建脚本", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The npm risk text must state that no build script runs.");
        if (npm.IndexOf("不在任何沙箱内", StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The npm risk text must not claim install-time code execution.");

        string tarball = PluginSpecPolicy.DescribeInstallRisk(@".\plugin.tgz");
        if (tarball.IndexOf("本地压缩包", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A tarball must be described as a local archive.");

        string local = PluginSpecPolicy.DescribeInstallRisk(@"C:\work\plugin");
        if (local.IndexOf("本地目录", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A directory must be described as a local directory.");
    }

    private static void VerifyCatalog()
    {
        List<PluginCatalogEntry> all = PluginCatalog.All();
        if (all.Count == 0)
            throw new InvalidOperationException("The curated catalog must not be empty.");

        // The curated list is the chosen discovery source, so it must cover both the
        // official providers and at least one verified third-party entry.
        if (!all.Exists(entry => entry.Official))
            throw new InvalidOperationException("The catalog must include official entries.");
        if (!all.Exists(entry => !entry.Official))
            throw new InvalidOperationException("The catalog must include third-party entries.");

        foreach (PluginCatalogEntry entry in all)
        {
            if (String.IsNullOrWhiteSpace(entry.Name) || String.IsNullOrWhiteSpace(entry.Spec))
                throw new InvalidOperationException("Every catalog entry needs a name and a spec.");
            // Every curated spec must survive classification.
            PluginSpecPolicy.Classify(entry.Spec);
            // An entry that installs from git must warn about the build script.
            if (PluginSpecPolicy.RequiresBuildAuthorization(entry.Spec) && !entry.RequiresBuildAuthorization)
                throw new InvalidOperationException("Catalog entry " + entry.Name + " installs from git but is not flagged for build authorization.");
        }

        if (PluginCatalog.Search("").Count != all.Count)
            throw new InvalidOperationException("An empty query must return the whole catalog.");
        if (PluginCatalog.Search("subagent").Count == 0)
            throw new InvalidOperationException("A name search must match the subagent providers.");
        if (PluginCatalog.Search("子代理").Count == 0)
            throw new InvalidOperationException("A display-name search must match Chinese labels.");
        if (PluginCatalog.Search("zzz-not-a-plugin").Count != 0)
            throw new InvalidOperationException("A non-matching query must return nothing.");

        // Case-insensitive matching keeps the box forgiving.
        if (PluginCatalog.Search("TURTLE").Count == 0)
            throw new InvalidOperationException("Search must be case-insensitive.");
    }

    private static void VerifyGitHubSearchUrl()
    {
        string url = GitHubPluginPolicy.BuildSearchUrl("\"dsh.bundle\"", 30);
        if (url.IndexOf("per_page=30", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The search URL must carry the page size.");
        if (url.IndexOf("sort=stars", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The search URL must sort by stars.");
        // The quote must be encoded, never placed raw in the query string.
        if (url.IndexOf("%22dsh.bundle%22", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The query must be percent-encoded.");

        // A crafted query must not be able to add its own parameters.
        string injected = GitHubPluginPolicy.BuildSearchUrl("x&per_page=1&q=evil", 30);
        if (injected.IndexOf("per_page=1", StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("A crafted query must not inject query parameters.");

        if (GitHubPluginPolicy.BuildSearchUrl("x", 500).IndexOf("per_page=100", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The page size must be capped at GitHub's maximum.");
        AssertThrows("blank search query", delegate { GitHubPluginPolicy.BuildSearchUrl("  ", 30); });

        if (GitHubPluginPolicy.DefaultQueries.Length == 0 ||
            GitHubPluginPolicy.DefaultQueries[0].IndexOf("dsh.bundle", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The most specific bundle query must come first.");
    }

    private static void VerifyGitHubSearchParsing()
    {
        string json = "{\"total_count\":2,\"items\":[" +
            "{\"full_name\":\"owner/alpha\",\"description\":\"first\",\"stargazers_count\":42,\"html_url\":\"https://github.com/owner/alpha\"}," +
            "{\"full_name\":\"owner/beta\",\"description\":null,\"stargazers_count\":7,\"html_url\":\"https://github.com/owner/beta\"}]}";
        List<PluginRepository> found = GitHubPluginPolicy.ParseSearchResponse(json);
        if (found.Count != 2)
            throw new InvalidOperationException("Both repositories must be parsed.");
        if (found[0].FullName != "owner/alpha" || found[0].Stars != 42)
            throw new InvalidOperationException("The repository fields must be parsed.");
        if (found[1].Description != "")
            throw new InvalidOperationException("A null description must become an empty string.");
        if (found[0].InstallSpec != "github:owner/alpha")
            throw new InvalidOperationException("The install spec must use the github: shorthand.");

        // An empty result set is not an error.
        if (GitHubPluginPolicy.ParseSearchResponse("{\"total_count\":0,\"items\":[]}").Count != 0)
            throw new InvalidOperationException("An empty item list must parse to an empty result.");
        if (GitHubPluginPolicy.ParseSearchResponse("{}").Count != 0)
            throw new InvalidOperationException("A response without items must parse to an empty result.");
        if (GitHubPluginPolicy.ParseSearchResponse("").Count != 0)
            throw new InvalidOperationException("An empty body must parse to an empty result.");

        // A repository without a full_name cannot be installed, so it is skipped.
        if (GitHubPluginPolicy.ParseSearchResponse("{\"items\":[{\"description\":\"x\"}]}").Count != 0)
            throw new InvalidOperationException("A repository without full_name must be skipped.");

        // A proxy error page is not JSON and must be reported, not silently empty.
        AssertThrows("non-JSON response", delegate { GitHubPluginPolicy.ParseSearchResponse("<html>blocked</html>"); });
    }

    private static void VerifyProfileManifest()
    {
        // The exact shape dsh writes after `dsh plugin add`, captured from a real run.
        string installed =
            "{\"name\":\"dsh-profile-mkt-test\",\"private\":true," +
            "\"dependencies\":{\"@deepseek-ai/dsh-experimental-agent-team-profile\":\"0.1.5-alpha.2\"}," +
            "\"dsh\":{\"profile\":{\"bundles\":[\"@deepseek-ai/dsh-base\",\"@deepseek-ai/dsh-experimental-agent-team-profile\"],\"patchReload\":\"live\"}}}";

        List<string> bundles = ProfileManifestPolicy.ReadBundles(installed);
        if (bundles.Count != 2 || bundles[0] != "@deepseek-ai/dsh-base")
            throw new InvalidOperationException("The bundle layer order must be preserved.");

        // The in-box bundle has no dependency entry, which is exactly what makes it
        // unremovable.
        List<string> removable = ProfileManifestPolicy.RemovableBundles(installed);
        if (removable.Count != 1 || removable[0] != "@deepseek-ai/dsh-experimental-agent-team-profile")
            throw new InvalidOperationException("Only the pnpm-owned bundle may be removable.");
        if (removable.Contains("@deepseek-ai/dsh-base"))
            throw new InvalidOperationException("An in-box bundle must never be offered for removal.");

        List<string> inBox = ProfileManifestPolicy.InBoxBundles(installed);
        if (inBox.Count != 1 || inBox[0] != "@deepseek-ai/dsh-base")
            throw new InvalidOperationException("The in-box bundle must be reported as unremovable.");

        string described = ProfileManifestPolicy.DescribeLayers(installed);
        if (described.IndexOf("内置，不可卸载", StringComparison.Ordinal) < 0 ||
            described.IndexOf("外部，可卸载", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The layer description must label both bundle kinds.");

        // The shipped web profile: both bundles are in-box, so nothing is removable.
        string shippedWeb =
            "{\"name\":\"dsh-profile-web\",\"private\":true,\"dependencies\":{}," +
            "\"dsh\":{\"profile\":{\"bundles\":[\"@deepseek-ai/dsh-base\",\"@deepseek-ai/dsh-web-app\"],\"patchReload\":\"live\"}}}";
        if (ProfileManifestPolicy.RemovableBundles(shippedWeb).Count != 0)
            throw new InvalidOperationException("A stock profile must expose nothing as removable.");
        if (ProfileManifestPolicy.InBoxBundles(shippedWeb).Count != 2)
            throw new InvalidOperationException("Both stock bundles are in-box.");

        // After removal dsh deletes the whole dependencies key.
        string afterRemoval =
            "{\"name\":\"dsh-profile-mkt-test\",\"private\":true," +
            "\"dsh\":{\"profile\":{\"bundles\":[\"@deepseek-ai/dsh-base\"],\"patchReload\":\"live\"}}}";
        if (ProfileManifestPolicy.ReadDependencies(afterRemoval).Count != 0)
            throw new InvalidOperationException("A missing dependencies key must read as empty.");
        if (ProfileManifestPolicy.ReadBundles(afterRemoval).Count != 1)
            throw new InvalidOperationException("The remaining bundle must still be listed.");

        // Malformed input must degrade, never throw at the caller.
        if (ProfileManifestPolicy.ReadBundles("not json").Count != 0)
            throw new InvalidOperationException("Malformed JSON must read as no bundles.");
        if (ProfileManifestPolicy.ReadBundles("").Count != 0)
            throw new InvalidOperationException("An empty manifest must read as no bundles.");
        if (ProfileManifestPolicy.ReadBundles("{\"dsh\":{\"profile\":{}}}").Count != 0)
            throw new InvalidOperationException("A manifest without bundles must read as no bundles.");

        // The manifest path follows the documented profile layout.
        string path = ProfileManifestPolicy.ManifestPath(@"C:\home\.dsh", "web");
        if (path != Path.Combine(@"C:\home\.dsh", "profiles", "web", "package.json"))
            throw new InvalidOperationException("The manifest path must follow $DSH_HOME/profiles/<name>/package.json.");
        AssertThrows("blank home", delegate { ProfileManifestPolicy.ManifestPath("", "web"); });
        AssertThrows("blank profile", delegate { ProfileManifestPolicy.ManifestPath(@"C:\home", ""); });
        if (ProfileManifestPolicy.DefaultProfileName != "web")
            throw new InvalidOperationException("The panel launches dsh web, so the default profile is web.");
    }

    private static void AssertKind(string label, string spec, PluginSpecKind expected)
    {
        PluginSpecKind actual = PluginSpecPolicy.Classify(spec);
        if (actual != expected)
            throw new InvalidOperationException(
                label + ": expected " + expected + " but got " + actual + " for spec '" + spec + "'.");
    }

    private static void AssertThrows(string label, Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            return;
        }
        throw new InvalidOperationException("Expected an exception for " + label + ".");
    }
}
