using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Management;
using Microsoft.Win32;

public enum StopTargetKind
{
    None,
    HarnessProcess,
    ForeignPort
}

public sealed class StopResolution
{
    public StopTargetKind Kind { get; private set; }
    public int ProcessId { get; private set; }

    public StopResolution(StopTargetKind kind, int processId)
    {
        Kind = kind;
        ProcessId = processId;
    }
}

public static class StopTargetResolver
{
    public static StopResolution Resolve(
        int recordedPid,
        int portPid,
        bool recordedPidAlive,
        bool recordedPidIsHarness,
        bool portPidIsHarness)
    {
        if (portPid > 0)
        {
            return portPidIsHarness
                ? new StopResolution(StopTargetKind.HarnessProcess, portPid)
                : new StopResolution(StopTargetKind.ForeignPort, 0);
        }
        if (recordedPid > 0 && recordedPidAlive && recordedPidIsHarness)
            return new StopResolution(StopTargetKind.HarnessProcess, recordedPid);
        return new StopResolution(StopTargetKind.None, 0);
    }
}

public sealed class UninstallTarget
{
    public string Path { get; private set; }
    public string Description { get; private set; }

    public UninstallTarget(string path, string description)
    {
        Path = path;
        Description = description;
    }
}

public static class UninstallTargetPlanner
{
    public static List<UninstallTarget> BuildTargets(
        string installRoot,
        string harnessHome,
        string settingsDirectory)
    {
        var targets = new List<UninstallTarget>();
        AddTarget(targets, installRoot, "Harness 安装目录（其中的专用 Node/pnpm 如存在会一并删除）");
        AddTarget(targets, harnessHome, "Harness 用户数据（配置、API Key、会话和附件）");
        AddTarget(targets, settingsDirectory, "控制面板配置");
        return targets;
    }

    private static void AddTarget(List<UninstallTarget> targets, string path, string description)
    {
        if (String.IsNullOrWhiteSpace(path))
            return;
        string full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        if (!targets.Any(target => String.Equals(target.Path, full, StringComparison.OrdinalIgnoreCase)))
            targets.Add(new UninstallTarget(full, description));
    }
}

public enum LogMessageKind
{
    Normal,
    Command,
    Warning,
    Detail,
    Error
}

public sealed class FormattedLogLine
{
    public string Text { get; private set; }
    public LogMessageKind Kind { get; private set; }

    public FormattedLogLine(string text, LogMessageKind kind)
    {
        Text = text;
        Kind = kind;
    }
}

public static class LogLineFormatter
{
    public static FormattedLogLine Format(string message)
    {
        string text = (message ?? "").Trim();
        if (text.StartsWith("(!)", StringComparison.Ordinal))
            return new FormattedLogLine("警告：" + text.Substring(3).TrimStart(), LogMessageKind.Warning);
        if (text.StartsWith("-", StringComparison.Ordinal))
            return new FormattedLogLine("    · " + text.Substring(1).TrimStart(), LogMessageKind.Detail);
        if (text.StartsWith("#", StringComparison.Ordinal))
            return new FormattedLogLine("信息：" + text.Substring(1).TrimStart(), LogMessageKind.Normal);
        if (text.StartsWith(">", StringComparison.Ordinal))
            return new FormattedLogLine("执行：" + text.Substring(1).TrimStart(), LogMessageKind.Command);
        if (text.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            return new FormattedLogLine("错误：" + text, LogMessageKind.Error);
        return new FormattedLogLine(text, LogMessageKind.Normal);
    }
}

public static class HarnessInstallationValidator
{
    public static readonly string[] RequiredFiles = new[]
    {
        "package.json",
        "pnpm-workspace.yaml",
        "apps/cli/package.json",
        "apps/cli/src/bin.ts",
        "packages/session-query/tool-session-query/package.json",
        "packages/session-query/tool-session-query/src/index.ts"
    };

    public static List<string> FindMissingFiles(Func<string, bool> fileExists)
    {
        var missing = new List<string>();
        foreach (string relativePath in RequiredFiles)
        {
            if (!fileExists(relativePath))
                missing.Add(relativePath);
        }
        return missing;
    }
}

public static class BuildRetryPolicy
{
    public static bool ShouldRetry(string command, int attempt)
    {
        return String.Equals(command, "pnpm run build", StringComparison.OrdinalIgnoreCase) && attempt == 1;
    }
}

public static class HarnessInstallPolicy
{
    public const string FrozenLockfileSwitch = "--frozen-lockfile";

    public static string BuildDependencyInstallCommand(bool hasLockfile)
    {
        return "pnpm install " + (hasLockfile ? FrozenLockfileSwitch : "--no-frozen-lockfile");
    }

    public static bool HasFrozenLockfile(string command)
    {
        return Regex.IsMatch(command ?? "", "(^|\\s)" + Regex.Escape(FrozenLockfileSwitch) + "($|\\s)", RegexOptions.IgnoreCase);
    }

    public static bool LockfileContainsPackage(string lockfile, string packageName)
    {
        if (String.IsNullOrWhiteSpace(lockfile) || String.IsNullOrWhiteSpace(packageName))
            return false;
        string pattern = "(?m)^\\s*" + Regex.Escape(packageName.Trim()) + "@[^:\\r\\n]+:";
        return Regex.IsMatch(lockfile, pattern, RegexOptions.IgnoreCase);
    }

    public static bool SourceManifestContainsPackage(string root, string packageName)
    {
        if (String.IsNullOrWhiteSpace(root) || String.IsNullOrWhiteSpace(packageName) || !Directory.Exists(root))
            return false;
        string pattern = "\"" + Regex.Escape(packageName.Trim()) + "\"\\s*:";
        try
        {
            foreach (string manifest in Directory.GetFiles(root, "package.json", SearchOption.AllDirectories))
                if (Regex.IsMatch(File.ReadAllText(manifest), pattern, RegexOptions.IgnoreCase))
                    return true;
        }
        catch
        {
            // A best-effort diagnosis must never replace the original command error.
        }
        return false;
    }

    public static string ApplyWindowsFsExtLeaseCompatibility(string source)
    {
        if (String.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("官方 Harness 的 fs-ext 锁实现文件为空，已停止安装。");

        const string nativeImport = "import { flock } from 'fs-ext'";
        const string compatibilityMarker = "const dshRequire = createRequire(import.meta.url)";
        const string lockFileComment = "/** Base name of the kernel lock file inside a session's directory. */";
        const string flockCall = "    flock(fd, flags, (error) =>";
        if (source.Contains(compatibilityMarker))
        {
            if (!source.Contains("function getDshFlock()") || !source.Contains("getDshFlock()(fd, flags"))
                throw new InvalidOperationException("官方 Harness 的 fs-ext Windows 兼容入口不完整，已停止安装以避免破坏官方源码。");
            return source;
        }
        if (!source.Contains(nativeImport) || !source.Contains(lockFileComment) || !source.Contains(flockCall))
            throw new InvalidOperationException("官方 Harness 的 fs-ext Windows 兼容入口发生变化，已停止安装以避免破坏官方源码。");

        string patched = source.Replace(
            nativeImport,
            "import { createRequire } from 'node:module'\n" +
            "\ntype DshFlock = (fd: number, flags: 'exnb' | 'un', callback: (error: Error | null) => void) => void\n" +
            "\nconst dshRequire = createRequire(import.meta.url)\n" +
            "let dshFlock: DshFlock | undefined");
        patched = patched.Replace(
            lockFileComment,
            "function getDshFlock(): DshFlock {\n" +
            "  if (dshFlock === undefined) dshFlock = (dshRequire('fs-ext') as { flock: DshFlock }).flock\n" +
            "  return dshFlock\n" +
            "}\n\n" +
            lockFileComment);
        patched = patched.Replace(flockCall, "    getDshFlock()(fd, flags, (error) =>");
        if (patched == source || patched.Contains(nativeImport))
            throw new InvalidOperationException("无法应用官方 Harness 的 Windows fs-ext 兼容处理，已停止安装。");
        return patched;
    }

    public static string DisableFsExtBuildScript(string workspaceYaml)
    {
        if (String.IsNullOrWhiteSpace(workspaceYaml))
            throw new InvalidOperationException("官方 Harness 的 pnpm 工作区配置为空，已停止安装。");

        const string enabledPattern = "(?m)^(\\s*fs-ext\\s*:\\s*)true(\\s*(?:#.*)?)$";
        const string disabledPattern = "(?m)^\\s*fs-ext\\s*:\\s*false(?:\\s*(?:#.*)?)?$";
        if (Regex.IsMatch(workspaceYaml, disabledPattern, RegexOptions.IgnoreCase))
            return workspaceYaml;

        string patched = Regex.Replace(workspaceYaml, enabledPattern, "$1false$2", RegexOptions.IgnoreCase);
        if (patched == workspaceYaml)
            throw new InvalidOperationException("官方 Harness 的 fs-ext 构建策略发生变化，已停止安装以避免触发 node-gyp 编译。");
        return patched;
    }

    public static string BuildInstallFailureHint(string command, string output, bool fsExtDeclaredBySource)
    {
        if (!Regex.IsMatch(command ?? "", "\\bpnpm\\s+install\\b", RegexOptions.IgnoreCase))
            return "";
        string text = output ?? "";
        if (text.IndexOf("node-gyp", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("fs-ext", StringComparison.OrdinalIgnoreCase) < 0)
            return "";
        if (text.IndexOf("fs-ext", StringComparison.OrdinalIgnoreCase) >= 0 && !fsExtDeclaredBySource)
            return "依赖安装触发了 fs-ext 原生模块编译，但官方源码没有声明 fs-ext。已停止本次安装；更新流程会保留旧版本并自动回滚，请不要安装 Visual Studio，先查看完整日志。";
        if (text.IndexOf("fs-ext", StringComparison.OrdinalIgnoreCase) >= 0)
            return "已确认这是官方 Harness 的 fs-ext 原生依赖。Windows 版本应使用官方源码内置的 Win32 锁实现并跳过 fs-ext 编译；本次兼容安装未完成，更新流程会保留旧版本并自动回滚。";
        return "依赖安装触发了需要 node-gyp 的原生模块编译。控制面板不会要求普通用户安装 C++ 编译环境；更新失败时会保留旧版本并自动回滚，请查看完整日志。";
    }
}

public static class BuildCommitEnvironment
{
    public const string VariableName = "DSH_CLIENT_COMMIT_HASH";

    public static void Apply(ProcessStartInfo process, string commit)
    {
        if (process == null || String.IsNullOrWhiteSpace(commit))
            return;
        process.EnvironmentVariables[VariableName] = commit.Trim();
    }
}

public static class DirectoryCleanupPolicy
{
    // rmdir removes a junction itself and does not follow its target.
    public const string FallbackCommand = "rmdir /s /q";
    public const int FallbackTimeoutMilliseconds = 300000;
    public const int FallbackAttempts = 3;
    public const int RetryDelayMilliseconds = 1000;

    public static string ToExtendedPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
            return full;
        if (full.StartsWith("\\\\", StringComparison.OrdinalIgnoreCase))
            return "\\\\?\\UNC\\" + full.Substring(2);
        return "\\\\?\\" + full;
    }
}

public enum HarnessLaunchMode
{
    BuiltCli,
    SourceFallback
}

public static class HarnessStartupPolicy
{
    public const string BuiltCliRelativePath = "apps\\cli\\lib\\bin.js";
    public const string WebArguments = "web --no-open";

    public static HarnessLaunchMode SelectLaunchMode(bool builtCliExists)
    {
        return builtCliExists ? HarnessLaunchMode.BuiltCli : HarnessLaunchMode.SourceFallback;
    }

    public static bool IsWebReadyLine(string line, int port)
    {
        return !String.IsNullOrEmpty(GetWebReadyUrl(line, port));
    }

    public static string GetWebReadyUrl(string line, int port)
    {
        const string prefix = "dsh web:";
        string candidate = (line ?? "").Trim();
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return "";
        string remainder = candidate.Substring(prefix.Length).TrimStart();
        int separator = remainder.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        string value = separator >= 0 ? remainder.Substring(0, separator) : remainder;
        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
            !String.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            uri.Port != port)
            return "";
        return uri.AbsoluteUri;
    }

    public static string RedactWebToken(string value)
    {
        return Regex.Replace(value ?? "", "([?&]token=)[^&\\s)]+", "$1<redacted>", RegexOptions.IgnoreCase);
    }
}

/// <summary>
/// Transport selection and the out-of-process fetch helper. Some hosts (notably a
/// Clash-style TUN proxy that answers every name with a fake-IP from 198.18.0.0/15)
/// complete TLS through Node's OpenSSL stack but fail inside the .NET Framework
/// SCHANNEL stack with "安全包中没有可用的凭证". The panel therefore keeps .NET as
/// the default transport and falls back to a Node fetch helper once a request
/// proves the local TLS stack cannot complete a handshake.
/// </summary>
public static class NodeNetworkPolicy
{
    public const string UserAgent = "DeepSeekHarnessManager/1.0";

    /// <summary>
    /// SCHANNEL reports these when the local TLS stack cannot complete a handshake.
    /// They are a transport fault, never a server rejection, so the request is safe
    /// to repeat over another stack.
    ///
    /// HttpClient does not surface the deepest Win32 text: the observed chain is
    /// HttpRequestException("发送请求时出错。") wrapping
    /// WebException("请求被中止: 未能创建 SSL/TLS 安全通道。"). The Win32 message
    /// ("安全包中没有可用的凭证") stays in the table because a raw
    /// HttpWebRequest call does expose it.
    /// </summary>
    private static readonly string[] TlsStackFaults = new[]
    {
        "未能创建 SSL/TLS 安全通道",
        "Could not create SSL/TLS secure channel",
        "安全包中没有可用的凭证",
        "No credentials are available in the security package",
        "基础连接已经关闭",
        "The underlying connection was closed",
        "接收时发生错误",
        "unexpected error occurred on a receive"
    };

    /// <summary>
    /// True when the chain shows a TLS/transport layer failure. Detected
    /// structurally as well as by message, because HttpClient rewrites the
    /// deepest SCHANNEL text into a generic SSL/TLS channel error. The chain is
    /// walked so an <see cref="AggregateException"/> or an
    /// <c>HttpRequestException</c> wrapper still reaches the real cause.
    /// </summary>
    public static bool IsTlsStackFailure(Exception error)
    {
        for (Exception current = error; current != null; current = current.InnerException)
        {
            if (IsTlsStackFailure(current.Message))
                return true;
            if (current is System.Net.WebException)
                return true;
        }
        return false;
    }

    public static bool IsTlsStackFailure(string message)
    {
        if (String.IsNullOrWhiteSpace(message))
            return false;
        foreach (string fault in TlsStackFaults)
        {
            if (message.IndexOf(fault, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Quotes an argument for the Node command line. Quotes and control characters
    /// are refused rather than escaped so a crafted path cannot break out of the
    /// argument.
    /// </summary>
    public static string QuoteArgument(string value)
    {
        string candidate = value ?? "";
        if (candidate.IndexOf('"') >= 0 || candidate.IndexOf('\r') >= 0 || candidate.IndexOf('\n') >= 0)
            throw new InvalidOperationException("路径包含无法安全传给 Node 的字符：" + candidate);
        return "\"" + candidate + "\"";
    }

    public static string EscapeJson(string value)
    {
        if (String.IsNullOrEmpty(value))
            return "";
        var builder = new StringBuilder(value.Length + 16);
        foreach (char character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < ' ')
                        builder.Append("\\u").Append(((int)character).ToString("x4"));
                    else
                        builder.Append(character);
                    break;
            }
        }
        return builder.ToString();
    }

    /// <summary>Builds the request file the helper reads. Kept pure so it is testable.</summary>
    public static string BuildRequestJson(string url, string outputPath, int timeoutSeconds, string userAgent)
    {
        if (String.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("请求地址为空。");
        if (String.IsNullOrWhiteSpace(outputPath))
            throw new InvalidOperationException("输出路径为空。");
        if (timeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException("timeoutSeconds");
        return "{\"url\":\"" + EscapeJson(url) +
            "\",\"outputPath\":\"" + EscapeJson(outputPath) +
            "\",\"timeoutSeconds\":" + timeoutSeconds +
            ",\"userAgent\":\"" + EscapeJson(String.IsNullOrWhiteSpace(userAgent) ? UserAgent : userAgent) +
            "\"}";
    }

    public const int DefaultTimeoutSeconds = 1200;

    /// <summary>
    /// The fetch helper. It runs in a separate process so a stalled socket cannot
    /// wedge the UI thread, and it streams the body straight to disk so a multi
    /// hundred megabyte runtime archive is never buffered in memory.
    /// </summary>
    public const string HelperScript = @"import { readFile, writeFile } from 'node:fs/promises'

const requestPath = process.argv[2]
if (!requestPath) {
  console.log(JSON.stringify({ ok: false, error: 'missing request file argument' }))
  process.exit(2)
}

let request
try {
  request = JSON.parse(await readFile(requestPath, 'utf8'))
} catch (error) {
  console.log(JSON.stringify({ ok: false, error: 'cannot read request: ' + error.message }))
  process.exit(2)
}

const controller = new AbortController()
const timer = setTimeout(() => controller.abort(), Math.max(1, request.timeoutSeconds ?? 1200) * 1000)
try {
  const response = await fetch(request.url, {
    redirect: 'follow',
    signal: controller.signal,
    headers: { 'user-agent': request.userAgent ?? 'DeepSeekHarnessManager/1.0' },
  })
  const buffer = Buffer.from(await response.arrayBuffer())
  await writeFile(request.outputPath, buffer)
  console.log(JSON.stringify({
    ok: true,
    status: response.status,
    contentType: response.headers.get('content-type') ?? '',
    bytes: buffer.length,
    finalUrl: response.url,
  }))
} catch (error) {
  console.log(JSON.stringify({
    ok: false,
    aborted: error.name === 'AbortError',
    error: error.name + ': ' + error.message,
  }))
} finally {
  clearTimeout(timer)
}
";

    /// <summary>
    /// Reads one field out of the helper's single-line JSON report. A missing or
    /// non-numeric field yields <paramref name="fallback"/>.
    /// </summary>
    public static int ReadReportedNumber(string report, string key, int fallback)
    {
        if (String.IsNullOrEmpty(report))
            return fallback;
        Match match = Regex.Match(
            report,
            "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return fallback;
        int value;
        return Int32.TryParse(match.Groups[1].Value, out value) ? value : fallback;
    }

    public static bool IsSuccessReport(string report, int expectedStatus)
    {
        if (String.IsNullOrEmpty(report))
            return false;
        return Regex.IsMatch(report, "\"ok\"\\s*:\\s*true", RegexOptions.CultureInvariant) &&
            ReadReportedNumber(report, "status", 0) == expectedStatus;
    }

    /// <summary>True when the helper itself reported that it could not finish.</summary>
    public static bool IsFailureReport(string report)
    {
        if (String.IsNullOrEmpty(report))
            return true;
        return Regex.IsMatch(report, "\"ok\"\\s*:\\s*false", RegexOptions.CultureInvariant);
    }
}

public enum PluginSpecKind
{
    /// <summary>A bare npm package name, optionally with a dist-tag or version.</summary>
    NpmPackage,
    /// <summary>A GitHub shorthand or URL, which installs source and may need allowBuilds.</summary>
    GitHubRepository,
    /// <summary>A local directory.</summary>
    LocalDirectory,
    /// <summary>A packed tarball.</summary>
    Tarball
}

/// <summary>
/// Classifies the argument handed to <c>dsh plugin add</c>. The kind decides the
/// install-time risk text: git sources run their prepare script outside any
/// sandbox, so the confirmation must say so explicitly.
/// </summary>
public static class PluginSpecPolicy
{
    public static bool IsGitPrefixed(string spec)
    {
        return Regex.IsMatch(spec ?? "", "^(git\\+|github:|git@|https?://)", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// The git-hosted spec forms that the dsh CLI calls out when pnpm blocks a
    /// prepare script. Mirrors the CLI's own test in apps/cli/src/plugin.ts.
    /// </summary>
    public static bool TriggersPrepareScript(string spec)
    {
        string candidate = spec ?? "";
        return Regex.IsMatch(candidate, "^git\\+", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(candidate, "^github:", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(candidate, "\\.git(?:#|$)", RegexOptions.IgnoreCase);
    }

    public static PluginSpecKind Classify(string spec)
    {
        if (String.IsNullOrWhiteSpace(spec))
            throw new InvalidOperationException("插件 spec 为空。");
        string candidate = spec.Trim();

        if (candidate.StartsWith("github:", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ||
            candidate.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase) >= 0 ||
            candidate.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            return PluginSpecKind.GitHubRepository;

        if (candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("link:", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(@".\", StringComparison.Ordinal) ||
            candidate.StartsWith("./", StringComparison.Ordinal) ||
            candidate.StartsWith(@"..\", StringComparison.Ordinal) ||
            candidate.StartsWith("../", StringComparison.Ordinal) ||
            Regex.IsMatch(candidate, "^[A-Za-z]:[\\\\/]"))
        {
            return candidate.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
                ? PluginSpecKind.Tarball
                : PluginSpecKind.LocalDirectory;
        }

        if (candidate.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
            candidate.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            return PluginSpecKind.Tarball;

        return PluginSpecKind.NpmPackage;
    }

    /// <summary>
    /// Whether installing this spec can execute third-party code at install time.
    /// Only git sources run a prepare script, and pnpm gates that behind an
    /// explicit allowBuilds entry.
    /// </summary>
    public static bool RequiresBuildAuthorization(string spec)
    {
        return Classify(spec) == PluginSpecKind.GitHubRepository;
    }

    /// <summary>
    /// A bare npm package name, the one spec form that may be typed without a local
    /// path or URL. Rejects anything that looks like a flag or a second argument so
    /// a typed value can never inject extra pnpm arguments.
    /// </summary>
    public static bool IsSafePackageName(string candidate)
    {
        if (String.IsNullOrWhiteSpace(candidate))
            return false;
        string value = candidate.Trim();
        if (value.StartsWith("-", StringComparison.Ordinal))
            return false;
        if (value.IndexOf(' ') >= 0 || value.IndexOf('\t') >= 0 || value.IndexOf(';') >= 0 ||
            value.IndexOf('"') >= 0 || value.IndexOf('\'') >= 0 || value.IndexOf('&') >= 0 ||
            value.IndexOf('|') >= 0 || value.IndexOf('>') >= 0 || value.IndexOf('<') >= 0)
            return false;
        // npm name, optionally scoped, optionally with @tag or @version.
        return Regex.IsMatch(value, "^(@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*(@[A-Za-z0-9._^~*-]+)?$");
    }

    /// <summary>
    /// One sentence describing what the user is about to install and the exposure it
    /// carries. The git case must warn about install-time code execution.
    /// </summary>
    public static string DescribeInstallRisk(string spec)
    {
        PluginSpecKind kind = Classify(spec);
        switch (kind)
        {
            case PluginSpecKind.GitHubRepository:
                return "来源：Git 仓库（" + spec.Trim() + "）。" + Environment.NewLine +
                    "该插件来自源码，安装时会运行它自己的构建脚本（prepare），" +
                    "而且这一步在你的机器上执行、不在任何沙箱内。" + Environment.NewLine +
                    "只对你信任的仓库授权，并尽量在 spec 上锁定 commit（例如 github:owner/repo#<sha>）。";
            case PluginSpecKind.LocalDirectory:
                return "来源：本地目录（" + spec.Trim() + "）。" + Environment.NewLine +
                    "将把该目录链接进 profile。请确认这是你自己或你信任的插件源码。";
            case PluginSpecKind.Tarball:
                return "来源：本地压缩包（" + spec.Trim() + "）。" + Environment.NewLine +
                    "将安装该压缩包中已构建好的内容，安装过程不会运行构建脚本。";
            default:
                return "来源：npm 包（" + spec.Trim() + "）。" + Environment.NewLine +
                    "安装的是发布者预构建好的内容，安装过程不会运行构建脚本。";
        }
    }
}

public sealed class PluginCatalogEntry
{
    public string Name { get; private set; }
    public string DisplayName { get; private set; }
    public string Summary { get; private set; }
    public string Spec { get; private set; }
    public bool Official { get; private set; }
    public bool RequiresBuildAuthorization { get; private set; }
    public string Homepage { get; private set; }

    public PluginCatalogEntry(
        string name,
        string displayName,
        string summary,
        string spec,
        bool official,
        bool requiresBuildAuthorization,
        string homepage)
    {
        Name = name;
        DisplayName = String.IsNullOrWhiteSpace(displayName) ? name : displayName;
        Summary = summary ?? "";
        Spec = spec ?? name;
        Official = official;
        RequiresBuildAuthorization = requiresBuildAuthorization;
        Homepage = homepage ?? "";
    }

    /// <summary>Text shown in the marketplace list, where the package name is the identity.</summary>
    public string ListLabel
    {
        get { return DisplayName + "  —  " + Name; }
    }
}

/// <summary>
/// The shipped, human-reviewed plugin list. It exists because npm metadata alone
/// cannot identify installable plugins: the abbreviated search response omits the
/// <c>dsh</c> field, so bundle detection needs a full packument fetch per candidate.
/// Several official plugins (for example the Codex and Claude Code subagent
/// providers) publish no <c>dsh</c> field at all and would be invisible to any
/// purely automatic scan.
/// </summary>
public static class PluginCatalog
{
    private static readonly PluginCatalogEntry[] Entries = new[]
    {
        new PluginCatalogEntry(
            "@deepseek-ai/dsh-subagent-codex",
            "Codex 子代理提供方",
            "通过官方 app-server 协议接入 Codex 的一次性子代理。",
            "@deepseek-ai/dsh-subagent-codex", true, false,
            "https://github.com/deepseek-ai/deepseek-harness"),
        new PluginCatalogEntry(
            "@deepseek-ai/dsh-subagent-claude-code",
            "Claude Code 子代理提供方",
            "通过官方 Agent SDK 接入 Claude Code 的一次性子代理。",
            "@deepseek-ai/dsh-subagent-claude-code", true, false,
            "https://github.com/deepseek-ai/deepseek-harness"),
        new PluginCatalogEntry(
            "@deepseek-ai/dsh-subagent-acp",
            "ACP 子代理提供方",
            "通过 ACP（Agent Client Protocol）接入外部代理的一次性子代理。",
            "@deepseek-ai/dsh-subagent-acp", true, false,
            "https://github.com/deepseek-ai/deepseek-harness"),
        new PluginCatalogEntry(
            "@deepseek-ai/dsh-experimental-agent-team-profile",
            "Agent Teams（实验）",
            "在 dsh-base 之上启用 Agent Teams 的实验性组合包。",
            "@deepseek-ai/dsh-experimental-agent-team-profile", true, false,
            "https://github.com/deepseek-ai/deepseek-harness"),
        new PluginCatalogEntry(
            "turtle-ui",
            "turtle-ui（TUI 界面）",
            "官方文档点名的终端界面示例组合包，可用来验证 git 安装流程。",
            "turtle-ui", false, false,
            "https://github.com/deepseek-harness/turtle-ui"),
        new PluginCatalogEntry(
            "turtle1999/turtle-ui",
            "turtle-ui（GitHub 源码）",
            "官方文档中作为 git 安装示例的仓库；从源码安装会触发构建授权。",
            "github:turtle1999/turtle-ui", false, true,
            "https://github.com/turtle1999/turtle-ui"),
        new PluginCatalogEntry(
            "PerryLink/dsh-plugin-guide",
            "DSH 插件开发指南",
            "可安装的组合包，内容是关于 DSH 插件开发的说明。",
            "github:PerryLink/dsh-plugin-guide", false, true,
            "https://github.com/PerryLink/dsh-plugin-guide"),
        new PluginCatalogEntry(
            "@nanmicoder/dsh-agent-teams",
            "Agent Teams（第三方）",
            "第三方实现的 Agent Teams 插件；来源为 npm。",
            "@nanmicoder/dsh-agent-teams", false, false,
            "https://www.npmjs.com/package/@nanmicoder/dsh-agent-teams"),
        new PluginCatalogEntry(
            "@morlay/better-session",
            "better-session",
            "第三方会话增强插件；来源为 npm。",
            "@morlay/better-session", false, false,
            "https://www.npmjs.com/package/@morlay/better-session"),
        new PluginCatalogEntry(
            "dsh-knowledge",
            "dsh-knowledge",
            "第三方知识库/RAG 插件；来源为 npm。",
            "dsh-knowledge", false, false,
            "https://www.npmjs.com/package/dsh-knowledge")
    };

    public static List<PluginCatalogEntry> All()
    {
        return new List<PluginCatalogEntry>(Entries);
    }

    /// <summary>
    /// Filters by a free-text query over name, display name, and summary. An empty
    /// query returns everything, so the initial view shows the reviewed list.
    /// </summary>
    public static List<PluginCatalogEntry> Search(string query)
    {
        var results = new List<PluginCatalogEntry>();
        string needle = (query ?? "").Trim();
        foreach (PluginCatalogEntry entry in Entries)
        {
            if (needle.Length == 0 ||
                entry.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                entry.DisplayName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                entry.Summary.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                results.Add(entry);
        }
        return results;
    }
}

public sealed class PluginRepository
{
    public string FullName { get; private set; }
    public string Description { get; private set; }
    public int Stars { get; private set; }
    public string HtmlUrl { get; private set; }

    public PluginRepository(string fullName, string description, int stars, string htmlUrl)
    {
        FullName = fullName ?? "";
        Description = description ?? "";
        Stars = stars;
        HtmlUrl = htmlUrl ?? "";
    }

    /// <summary>
    /// The spec that installs this repository. <c>dsh plugin</c> forwards it to pnpm,
    /// which understands the <c>github:</c> shorthand.
    /// </summary>
    public string InstallSpec
    {
        get { return "github:" + FullName; }
    }

    public string ListLabel
    {
        get { return FullName + "  ★" + Stars; }
    }
}

/// <summary>
/// GitHub repository discovery. This is a discovery aid, not a trust boundary: a
/// repository found here is unreviewed, so the UI must confirm the install-time
/// code execution that a git source carries.
/// </summary>
public static class GitHubPluginPolicy
{
    /// <summary>
    /// Queries that surface installable bundles first, most specific first.
    /// <c>"dsh.bundle"</c> matches repositories that declare the bundle manifest
    /// field, which is exactly the installable set.
    /// </summary>
    public static readonly string[] DefaultQueries = new[]
    {
        "\"dsh.bundle\"",
        "dsh-base in:name,description",
        "dsh plugin in:name,description"
    };

    public const string SearchEndpoint = "https://api.github.com/search/repositories";

    /// <summary>
    /// Builds a search URL. The query is percent-encoded, so a user-typed query
    /// cannot inject extra query parameters.
    /// </summary>
    public static string BuildSearchUrl(string query, int perPage)
    {
        if (String.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("搜索关键词为空。");
        int size = perPage <= 0 ? 30 : Math.Min(perPage, 100);
        return SearchEndpoint +
            "?q=" + Uri.EscapeDataString(query.Trim()) +
            "&sort=stars&order=desc&per_page=" + size;
    }

    /// <summary>
    /// Parses a GitHub search response into repositories. A response with no
    /// <c>items</c> array yields an empty list rather than throwing, so an empty
    /// result set is not reported as a failure.
    /// </summary>
    public static List<PluginRepository> ParseSearchResponse(string json)
    {
        var results = new List<PluginRepository>();
        if (String.IsNullOrWhiteSpace(json))
            return results;

        var serializer = new JavaScriptSerializer();
        Dictionary<string, object> root;
        try
        {
            root = serializer.DeserializeObject(json) as Dictionary<string, object>;
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("GitHub 搜索返回的内容不是有效 JSON，可能是网络代理返回了错误页面。");
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("GitHub 搜索返回的内容不是有效 JSON，可能是网络代理返回了错误页面。");
        }

        if (root == null)
            return results;

        object itemsValue;
        if (!root.TryGetValue("items", out itemsValue))
            return results;
        object[] items = itemsValue as object[];
        if (items == null)
            return results;

        foreach (object item in items)
        {
            var row = item as Dictionary<string, object>;
            if (row == null)
                continue;
            string fullName = ReadString(row, "full_name");
            if (String.IsNullOrWhiteSpace(fullName))
                continue;
            results.Add(new PluginRepository(
                fullName,
                ReadString(row, "description"),
                ReadInt(row, "stargazers_count"),
                ReadString(row, "html_url")));
        }
        return results;
    }

    private static string ReadString(Dictionary<string, object> row, string key)
    {
        object value;
        if (!row.TryGetValue(key, out value) || value == null)
            return "";
        return value.ToString();
    }

    private static int ReadInt(Dictionary<string, object> row, string key)
    {
        object value;
        if (!row.TryGetValue(key, out value) || value == null)
            return 0;
        int parsed;
        return Int32.TryParse(value.ToString(), out parsed) ? parsed : 0;
    }
}

/// <summary>
/// Reads and updates a profile manifest. The manifest is the single source of truth
/// for what is installed: <c>dsh.profile.bundles</c> is the ordered layer list, and
/// <c>dependencies</c> holds the out-of-tree packages.
///
/// A bundle listed in <c>dsh.profile.bundles</c> but absent from
/// <c>dependencies</c> is an in-box bundle resolved from the dsh installation
/// itself (for example <c>@deepseek-ai/dsh-base</c>). Those must never be offered
/// for removal, because pnpm does not own them.
/// </summary>
public static class ProfileManifestPolicy
{
    public static string ManifestPath(string dshHome, string profileName)
    {
        if (String.IsNullOrWhiteSpace(dshHome))
            throw new InvalidOperationException("未配置 DSH_HOME。");
        if (String.IsNullOrWhiteSpace(profileName))
            throw new InvalidOperationException("profile 名称为空。");
        return Path.Combine(dshHome, "profiles", profileName, "package.json");
    }

    /// <summary>
    /// The profile whose bundles the running Harness actually composed. The panel
    /// always launches <c>dsh web</c>, which is the hard-coded alias for this
    /// profile.
    /// </summary>
    public const string DefaultProfileName = "web";

    public static List<string> ReadBundles(string manifestJson)
    {
        var bundles = new List<string>();
        object value = ReadPath(manifestJson, new[] { "dsh", "profile", "bundles" });
        object[] array = value as object[];
        if (array == null)
            return bundles;
        foreach (object item in array)
        {
            string name = item as string;
            if (!String.IsNullOrWhiteSpace(name))
                bundles.Add(name);
        }
        return bundles;
    }

    public static List<string> ReadDependencies(string manifestJson)
    {
        var names = new List<string>();
        object value = ReadPath(manifestJson, new[] { "dependencies" });
        var map = value as Dictionary<string, object>;
        if (map == null)
            return names;
        foreach (KeyValuePair<string, object> pair in map)
        {
            if (!String.IsNullOrWhiteSpace(pair.Key))
                names.Add(pair.Key);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Bundles the user may remove: listed as a layer and owned by pnpm as a
    /// dependency. In-box bundles fail the second test and are excluded.
    /// </summary>
    public static List<string> RemovableBundles(string manifestJson)
    {
        List<string> bundles = ReadBundles(manifestJson);
        List<string> dependencies = ReadDependencies(manifestJson);
        var removable = new List<string>();
        foreach (string bundle in bundles)
        {
            if (dependencies.Any(name => String.Equals(name, bundle, StringComparison.OrdinalIgnoreCase)))
                removable.Add(bundle);
        }
        return removable;
    }

    /// <summary>Bundles that come from the dsh installation and cannot be uninstalled.</summary>
    public static List<string> InBoxBundles(string manifestJson)
    {
        List<string> bundles = ReadBundles(manifestJson);
        List<string> removable = RemovableBundles(manifestJson);
        var inBox = new List<string>();
        foreach (string bundle in bundles)
        {
            if (!removable.Any(name => String.Equals(name, bundle, StringComparison.OrdinalIgnoreCase)))
                inBox.Add(bundle);
        }
        return inBox;
    }

    /// <summary>One-line rendering of the layer stack for the log and status text.</summary>
    public static string DescribeLayers(string manifestJson)
    {
        List<string> bundles = ReadBundles(manifestJson);
        if (bundles.Count == 0)
            return "profile 中没有配置任何组合包。";
        List<string> removable = RemovableBundles(manifestJson);
        var lines = new List<string>();
        int index = 1;
        foreach (string bundle in bundles)
        {
            bool external = removable.Any(name => String.Equals(name, bundle, StringComparison.OrdinalIgnoreCase));
            lines.Add(index + ". " + bundle + (external ? "（外部，可卸载）" : "（内置，不可卸载）"));
            index++;
        }
        return String.Join(Environment.NewLine, lines.ToArray());
    }

    private static object ReadPath(string json, string[] path)
    {
        if (String.IsNullOrWhiteSpace(json))
            return null;
        var serializer = new JavaScriptSerializer();
        Dictionary<string, object> current;
        try
        {
            current = serializer.DeserializeObject(json) as Dictionary<string, object>;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        for (int i = 0; i < path.Length; i++)
        {
            if (current == null)
                return null;
            object value;
            if (!current.TryGetValue(path[i], out value))
                return null;
            if (i == path.Length - 1)
                return value;
            current = value as Dictionary<string, object>;
        }
        return null;
    }
}

public static class StateSecretProtection
{
    private const string Prefix = "dpapi:";
    public static string Protect(string value)
    {
        if (String.IsNullOrEmpty(value))
            return "";
        byte[] plain = Encoding.UTF8.GetBytes(value);
        byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    public static string Unprotect(string value)
    {
        if (String.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return "";
        try
        {
            byte[] encrypted = Convert.FromBase64String(value.Substring(Prefix.Length));
            byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return "";
        }
        catch (FormatException)
        {
            return "";
        }
    }
}

public static class HarnessLifecyclePolicy
{
    public const int StartupTimeoutSeconds = 120;
    public const int StopTimeoutMilliseconds = 30000;
    public const int PollIntervalMilliseconds = 250;
    public const int EndpointProbeIntervalMilliseconds = 1000;
    public const int EndpointProbeTimeoutMilliseconds = 3000;
    public const string LocalWebUri = "http://127.0.0.1:3080/";

    public static bool IsHarnessDocument(string content)
    {
        return !String.IsNullOrWhiteSpace(content) &&
            content.IndexOf("<!doctype html", StringComparison.OrdinalIgnoreCase) >= 0 &&
            content.IndexOf("__DSH_BOOT__", StringComparison.Ordinal) >= 0;
    }

    public static bool IsStartupReady(bool processAlive, bool portOpen, bool endpointReady, bool officialReadyLog)
    {
        return processAlive && portOpen && (endpointReady || officialReadyLog);
    }

    public static int StopWaitAttempts(int timeoutMilliseconds, int pollIntervalMilliseconds)
    {
        if (pollIntervalMilliseconds <= 0)
            throw new ArgumentOutOfRangeException("pollIntervalMilliseconds");
        return Math.Max(1, timeoutMilliseconds / pollIntervalMilliseconds);
    }

    public static string StartupFailureMessage(bool portObserved, bool endpointObserved)
    {
        if (!portObserved)
            return "Harness 服务尚未监听 3080 端口。请检查日志中的 Node.js、依赖或端口占用错误。";
        if (!endpointObserved)
            return "Harness 端口已监听，但页面尚未可访问。通常是 Harness 或插件仍在初始化，请检查日志中的错误。";
        return "Harness 页面已响应，但启动进程未能保持运行。请检查日志中的退出原因。";
    }
}

public static class HarnessProfileDiagnostics
{
    public static bool IsThirdPartyPackage(string packageName)
    {
        return !String.IsNullOrWhiteSpace(packageName) &&
            !packageName.StartsWith("@deepseek-ai/", StringComparison.OrdinalIgnoreCase);
    }

    public static int CountThirdPartyPackages(IEnumerable<string> packageNames)
    {
        return packageNames == null ? 0 : packageNames.Count(IsThirdPartyPackage);
    }

    public static string BuildStartupHint(int configuredPackageCount, int thirdPartyPackageCount)
    {
        if (configuredPackageCount <= 0 || thirdPartyPackageCount <= 0)
            return "";
        return "启动提示：当前 Web 配置包含 " + configuredPackageCount + " 个扩展包，其中 " +
            thirdPartyPackageCount + " 个为第三方扩展。页面首次加载仍需由浏览器初始化这些扩展。";
    }
}

public static class HarnessProcessIdentityPolicy
{
    public static bool IsHarnessCommandLine(string commandLine)
    {
        string candidate = (commandLine ?? "").ToLowerInvariant();
        if (String.IsNullOrWhiteSpace(candidate))
            return false;
        if (candidate.Contains("deepseek-harness") || candidate.Contains("deepseekharness"))
            return true;
        bool startsWeb = candidate.Contains("web");
        return startsWeb &&
            (candidate.Contains("apps/cli/src/bin.ts") ||
             candidate.Contains("apps\\cli\\src\\bin.ts") ||
             candidate.Contains("apps/cli/lib/bin.js") ||
             candidate.Contains("apps\\cli\\lib\\bin.js") ||
             candidate.Contains("dsh web"));
    }
}

public sealed class ProcessExecutionResult
{
    public int ExitCode { get; private set; }
    public string StandardOutput { get; private set; }
    public string StandardError { get; private set; }

    public ProcessExecutionResult(int exitCode, string standardOutput, string standardError)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput ?? "";
        StandardError = standardError ?? "";
    }
}

public sealed class ManagerForm : Form
{
    private const string RepoInfoApi = "https://api.github.com/repos/deepseek-ai/deepseek-harness";
    private const string RepoApiTemplate = "https://api.github.com/repos/deepseek-ai/deepseek-harness/commits/{0}";
    private const string RepoZipTemplate = "https://github.com/deepseek-ai/deepseek-harness/archive/refs/heads/{0}.zip";
    private const string NodeIndex = "https://nodejs.org/dist/index.json";
    private const int FallbackMinimumNodeMajor = 20;
    private readonly Label pathBox = new Label();
    private readonly Label statusLabel = new Label();
    private readonly Label runningLabel = new Label();
    private readonly Label versionLabel = new Label();
    private readonly RichTextBox logBox = new RichTextBox();
    private readonly Button installButton = new Button();
    private readonly Button startButton = new Button();
    private readonly Button restartButton = new Button();
    private readonly Button stopButton = new Button();
    private readonly Button updateButton = new Button();
    private readonly Button openButton = new Button();
    private readonly Button rescanButton = new Button();
    private readonly Button openFolderButton = new Button();
    private readonly Button uninstallButton = new Button();
    private readonly Button marketplaceButton = new Button();
    private readonly HttpClient http = new HttpClient();
    private readonly object gate = new object();
    private bool busy;
    private Process server;
    private List<string> discoveredRoots = new List<string>();
    private string selectedNodeDirectory = "";
    private string selectedPnpm = "";
    private string selectedCorepack = "";
    private string selectedSourceCommit = "";
    private string nodeHelperPath = "";

    /// <summary>
    /// Set once a request proves the .NET Framework TLS stack cannot complete a
    /// handshake on this machine. From then on every outbound request goes through
    /// the Node helper without paying a failed attempt first.
    /// </summary>
    private bool nodeTransportRequired;

    public ManagerForm()
    {
        Text = "DeepSeek Harness 控制面板";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Width = 760;
        Height = 560;
        MinimumSize = new Size(700, 480);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepSeekHarnessManager/1.0");
        http.Timeout = TimeSpan.FromMinutes(20);

        BuildUi();
        pathBox.Text = LoadConfiguredRoot();
        RefreshState();
    }

    private void BuildUi()
    {
        var main = new TableLayoutPanel();
        main.Dock = DockStyle.Fill;
        main.Padding = new Padding(14);
        main.RowCount = 6;
        main.ColumnCount = 1;
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(main);

        var pathPanel = new TableLayoutPanel();
        pathPanel.Dock = DockStyle.Fill;
        pathPanel.ColumnCount = 2;
        pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var pathLabel = new Label { Text = "安装目录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        pathBox.Dock = DockStyle.Fill;
        pathBox.TextAlign = ContentAlignment.MiddleLeft;
        pathBox.AutoEllipsis = true;
        pathPanel.Controls.Add(pathLabel, 0, 0);
        pathPanel.Controls.Add(pathBox, 1, 0);
        main.Controls.Add(pathPanel, 0, 0);

        var statePanel = new TableLayoutPanel();
        statePanel.Dock = DockStyle.Fill;
        statePanel.ColumnCount = 2;
        statePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        statePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statePanel.Controls.Add(new Label { Text = "状态", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statePanel.Controls.Add(statusLabel, 1, 0);
        main.Controls.Add(statePanel, 0, 1);

        var runningPanel = new TableLayoutPanel();
        runningPanel.Dock = DockStyle.Fill;
        runningPanel.ColumnCount = 2;
        runningPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        runningPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        runningPanel.Controls.Add(new Label { Text = "运行状态", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        runningLabel.Dock = DockStyle.Fill;
        runningLabel.TextAlign = ContentAlignment.MiddleLeft;
        runningPanel.Controls.Add(runningLabel, 1, 0);
        main.Controls.Add(runningPanel, 0, 2);

        var infoPanel = new TableLayoutPanel();
        infoPanel.Dock = DockStyle.Fill;
        infoPanel.ColumnCount = 2;
        infoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        infoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        infoPanel.Controls.Add(new Label { Text = "Harness 版本", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        versionLabel.Dock = DockStyle.Fill;
        versionLabel.TextAlign = ContentAlignment.MiddleLeft;
        infoPanel.Controls.Add(versionLabel, 1, 0);
        main.Controls.Add(infoPanel, 0, 3);

        var buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Fill;
        buttons.WrapContents = true;
        buttons.AutoScroll = false;
        buttons.FlowDirection = FlowDirection.LeftToRight;
        AddButton(buttons, installButton, "一键安装", InstallClick);
        AddButton(buttons, uninstallButton, "彻底卸载", UninstallClick);
        AddButton(buttons, startButton, "启动", StartClick);
        AddButton(buttons, restartButton, "重启", RestartClick);
        AddButton(buttons, stopButton, "停止", StopClick);
        AddButton(buttons, updateButton, "检查 Harness 更新", UpdateClick);
        AddButton(buttons, openButton, "打开页面", OpenClick);
        AddButton(buttons, rescanButton, "重新扫描", RescanClick);
        AddButton(buttons, openFolderButton, "打开目录", OpenFolderClick);
        AddButton(buttons, marketplaceButton, "插件市场", MarketplaceClick);
        main.Controls.Add(buttons, 0, 4);

        logBox.ReadOnly = true;
        logBox.ScrollBars = RichTextBoxScrollBars.Vertical;
        logBox.WordWrap = true;
        logBox.HideSelection = false;
        logBox.Dock = DockStyle.Fill;
        logBox.BackColor = Color.White;
        main.Controls.Add(logBox, 0, 5);
    }

    private void AddButton(Control parent, Button button, string text, EventHandler handler)
    {
        button.Text = text;
        button.AutoSize = true;
        button.Height = 30;
        button.Click += handler;
        parent.Controls.Add(button);
    }

    private string DefaultInstallRoot()
    {
        string legacy = "D:\\deepseek-harness";
        if (Directory.Exists(Path.Combine(legacy, "apps")) && File.Exists(Path.Combine(legacy, "package.json")))
            return legacy;
        if (Directory.Exists("D:\\"))
            return legacy;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekHarness");
    }

    private string Root { get { return pathBox.Text.Trim(); } }
    private string Source { get { return Root; } }
    private string Runtime { get { return Path.Combine(Root, ".dsh-runtime", "node"); } }
    private string StateFile { get { return Path.Combine(Root, ".dsh-manager-state.json"); } }
    private string SettingsDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekHarnessManager"); } }
    private string SettingsFile { get { return Path.Combine(SettingsDirectory, "settings.json"); } }

    private void BrowseClick(object sender, EventArgs e)
    {
        if (IsConfiguredRoot())
        {
            MessageBox.Show(this, "Harness 已安装，安装目录已经锁定。如需更换目录，请使用迁移或重新安装流程。", "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using (var dialog = new FolderBrowserDialog())
        {
            dialog.SelectedPath = Directory.Exists(Root) ? Root : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            dialog.Description = "选择 DeepSeek Harness 的安装目录";
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                pathBox.Text = dialog.SelectedPath;
                RefreshState();
            }
        }
    }

    private void InstallClick(object sender, EventArgs e)
    {
        if (IsInstalled() && !IsInstallationReady())
        {
            string prompt = "检测到当前 Harness 安装不完整，无法启动。" + Environment.NewLine +
                "将重新下载官方源码并修复安装，保留 Harness 专用运行环境、日志和你的用户配置。" + Environment.NewLine +
                "确定开始修复吗？";
            if (Ask(prompt, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                RunAsync("正在修复 DeepSeek Harness", RepairAsync);
            return;
        }
        if (!ChooseInstallRoot())
            return;
        RunAsync("正在安装 DeepSeek Harness", InstallAsync);
    }

    private void StartClick(object sender, EventArgs e)
    {
        RunAsync("正在启动 DeepSeek Harness", delegate { return StartAsync(true); });
    }

    private void RestartClick(object sender, EventArgs e)
    {
        RunAsync("正在重启 DeepSeek Harness", async delegate
        {
            await StopAsync();
            await StartAsync(false);
        });
    }

    private void StopClick(object sender, EventArgs e)
    {
        RunAsync("正在停止 DeepSeek Harness", StopAsync);
    }

    private void UpdateClick(object sender, EventArgs e)
    {
        RunAsync("正在检查 DeepSeek Harness 更新", CheckUpdateAsync);
    }

    private void OpenClick(object sender, EventArgs e)
    {
        string readyUrl = StoredWebUrl();
        if (String.IsNullOrEmpty(readyUrl))
        {
            Log("当前运行实例没有可用的认证地址，请点击“重启”生成新的访问地址。");
            return;
        }
        Process.Start(readyUrl);
    }

    private void RescanClick(object sender, EventArgs e)
    {
        RefreshState();
        if (discoveredRoots.Count == 0)
        {
            Log("重新扫描完成：未检测到 DeepSeek Harness。");
        }
        else if (discoveredRoots.Count == 1)
        {
            if (!IsInstallationReady())
            {
                Log("重新扫描完成：安装不完整，需要修复。");
                foreach (string path in MissingInstallationFiles())
                    Log("缺少文件: " + path);
                return;
            }
            Log("重新扫描完成：已安装。");
            Log("安装目录: " + discoveredRoots[0]);
            Log("版本: " + LocalVersion());
        }
        else
        {
            Log("重新扫描完成：发现多个 Harness 安装。");
            foreach (string root in discoveredRoots)
                Log("安装目录: " + root);
        }
    }

    private void OpenFolderClick(object sender, EventArgs e)
    {
        if (Directory.Exists(Root))
            Process.Start("explorer.exe", "\"" + Root + "\"");
    }

    /// <summary>
    /// Opens the plugin marketplace. It installs through <c>dsh plugin</c> inside the
    /// installed CLI, so it needs the same Node and pnpm environment the rest of the
    /// panel prepares.
    /// </summary>
    private void MarketplaceClick(object sender, EventArgs e)
    {
        if (!IsInstalled() || !IsInstallationReady())
        {
            ShowInfo("请先完成 Harness 安装或修复，再管理插件。");
            return;
        }
        string dshHome = Environment.GetEnvironmentVariable("DSH_HOME");
        if (String.IsNullOrWhiteSpace(dshHome))
        {
            dshHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh");
        }
        try
        {
            using (var marketplace = new PluginMarketplaceForm(
                Source,
                dshHome,
                selectedNodeDirectory,
                selectedPnpm,
                selectedCorepack,
                FetchTextAsync))
                marketplace.ShowDialog(this);
        }
        catch (Exception ex)
        {
            ShowInfo("无法打开插件市场：" + Environment.NewLine + FlattenException(ex));
        }
    }

    private void UninstallClick(object sender, EventArgs e)
    {
        if (!IsInstalled())
            return;
        string summary = BuildUninstallSummary();
        string warning = "此操作不可恢复，将彻底删除 DeepSeek Harness。" +
            Environment.NewLine + Environment.NewLine +
            summary +
            Environment.NewLine + Environment.NewLine +
            "系统全局 Node、npm、pnpm 和其他项目不会被删除。" +
            Environment.NewLine + Environment.NewLine +
            "确定继续吗？";
        if (Ask(warning, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        RunAsync("正在彻底卸载 DeepSeek Harness", UninstallAsync);
    }

    private string BuildUninstallSummary()
    {
        var lines = new List<string>();
        foreach (UninstallTarget target in GetUninstallTargets())
            lines.Add("将删除：" + target.Description + Environment.NewLine + "  " + target.Path);
        return String.Join(Environment.NewLine, lines.ToArray());
    }

    private List<UninstallTarget> GetUninstallTargets()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string harnessHome = Path.Combine(userProfile, ".dsh");
        return UninstallTargetPlanner.BuildTargets(Root, harnessHome, SettingsDirectory);
    }

    private bool ChooseInstallRoot()
    {
        using (var dialog = new FolderBrowserDialog())
        {
            dialog.SelectedPath = Directory.Exists(Root) ? Root : "D:\\deepseek-harness";
            dialog.Description = "选择 DeepSeek Harness 安装目录";
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return false;
            string selected = Path.GetFullPath(dialog.SelectedPath).TrimEnd('\\');
            string driveRoot = (Path.GetPathRoot(selected) ?? "").TrimEnd('\\');
            if (String.Equals(selected, driveRoot, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "不能直接安装到磁盘根目录，请选择一个子目录，例如 D:\\deepseek-harness。", "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (IsSourceAt(selected))
            {
                MessageBox.Show(this, "该目录已经存在 DeepSeek Harness，请直接使用已有安装。", "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Information);
                pathBox.Text = selected;
                RefreshState();
                return false;
            }
            if (Directory.Exists(selected) && Directory.GetFileSystemEntries(selected).Length > 0)
            {
                MessageBox.Show(this, "安装目录必须为空，请选择空目录。", "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            pathBox.Text = selected;
            return true;
        }
    }

    private void RunAsync(string title, Func<Task> action)
    {
        lock (gate)
        {
            if (busy)
            {
                MessageBox.Show(this, "当前已有操作正在执行，请等待完成。", "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            busy = true;
        }
        SetButtons(false);
        Log(title + "...");
        Task.Run(action).ContinueWith(t =>
        {
            BeginInvoke((Action)delegate
            {
                busy = false;
                SetButtons(true);
                if (t.IsFaulted)
                {
                    string message = t.Exception == null ? "未知错误" : t.Exception.GetBaseException().Message;
                    Log("失败摘要：" + title + "未完成。原因：" + message);
                    MessageBox.Show(this, message, "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    Log("完成。");
                }
                RefreshState();
            });
        });
    }

    private void SetButtons(bool enabled)
    {
        bool installed = IsInstalled();
        bool ready = installed && IsInstallationReady();
        int portPid = FindPortOwner(3080);
        bool portBusy = IsPortOpen(3080);
        bool running = portBusy && portPid > 0 && IsLikelyHarnessProcess(portPid);
        bool multiple = discoveredRoots.Count > 1;
        installButton.Text = installed && !ready ? "修复安装" : "一键安装";
        installButton.Enabled = enabled && (!installed || !ready) && !multiple;
        startButton.Enabled = enabled && ready && !portBusy && !multiple;
        restartButton.Enabled = enabled && ready && running && !multiple;
        stopButton.Enabled = enabled && running && !multiple;
        updateButton.Enabled = enabled && ready && !multiple;
        openButton.Enabled = enabled && running;
        rescanButton.Enabled = enabled;
        openFolderButton.Enabled = enabled && Directory.Exists(Root);
        uninstallButton.Enabled = enabled && installed && !multiple;
        // The marketplace drives `dsh plugin`, which needs the installed CLI.
        marketplaceButton.Enabled = enabled && ready && !multiple;
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)delegate { Log(message); });
            return;
        }
        string clean = StripAnsiSequences(message);
        string[] lines = clean.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (string line in lines)
        {
            if (String.IsNullOrWhiteSpace(line))
                continue;
            FormattedLogLine formatted = LogLineFormatter.Format(line);
            logBox.SelectionStart = logBox.TextLength;
            logBox.SelectionLength = 0;
            logBox.SelectionColor = LogColor(formatted.Kind);
            logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + formatted.Text + Environment.NewLine);
        }
        logBox.SelectionColor = logBox.ForeColor;
        logBox.ScrollToCaret();
    }

    private static Color LogColor(LogMessageKind kind)
    {
        switch (kind)
        {
            case LogMessageKind.Warning:
                return Color.DarkOrange;
            case LogMessageKind.Error:
                return Color.Firebrick;
            case LogMessageKind.Command:
                return Color.DimGray;
            case LogMessageKind.Detail:
                return Color.DimGray;
            default:
                return Color.Black;
        }
    }

    private static string StripAnsiSequences(string message)
    {
        return Regex.Replace(message ?? "", "\u001B\\[[0-?]*[ -/]*[@-~]", "", RegexOptions.CultureInvariant);
    }

    private void RefreshState()
    {
        discoveredRoots = DiscoverInstallRoots();
        bool multiple = discoveredRoots.Count > 1;
        if (discoveredRoots.Count == 0)
            pathBox.Text = "";
        else if (!multiple && !IsInstalled())
            pathBox.Text = discoveredRoots[0];
        if (multiple && !discoveredRoots.Contains(Root, StringComparer.OrdinalIgnoreCase))
            pathBox.Text = discoveredRoots[0];
        bool installed = IsInstalled();
        bool ready = installed && IsInstallationReady();
        if (installed && !IsConfiguredRoot())
            SaveConfiguredRoot(Root);
        int portPid = FindPortOwner(3080);
        bool portBusy = IsPortOpen(3080);
        bool running = portBusy && portPid > 0 && IsLikelyHarnessProcess(portPid);
        if (multiple)
        {
            statusLabel.Text = "发现多个安装";
            runningLabel.Text = running ? "正在运行" : (portBusy ? "端口被其他程序占用" : "未运行");
            versionLabel.Text = "";
        }
        else if (installed && !ready)
        {
            statusLabel.Text = "安装不完整（需要修复）";
            runningLabel.Text = "未运行";
            versionLabel.Text = LocalVersion();
        }
        else if (installed && running)
        {
            statusLabel.Text = "已安装";
            runningLabel.Text = "正在运行";
            versionLabel.Text = LocalVersion();
        }
        else if (installed && portBusy)
        {
            statusLabel.Text = "已安装";
            runningLabel.Text = "端口被其他程序占用";
            versionLabel.Text = LocalVersion();
        }
        else if (installed)
        {
            statusLabel.Text = "已安装";
            runningLabel.Text = "未运行";
            versionLabel.Text = LocalVersion();
        }
        else if (running)
        {
            statusLabel.Text = "未安装";
            runningLabel.Text = "正在运行";
            versionLabel.Text = "";
        }
        else if (portBusy)
        {
            statusLabel.Text = "未安装";
            runningLabel.Text = "端口被其他程序占用";
            versionLabel.Text = "";
        }
        else
        {
            statusLabel.Text = "未安装";
            runningLabel.Text = "未运行";
            versionLabel.Text = "";
        }
        SetButtons(!busy);
    }

    private async Task InstallAsync()
    {
        if (IsInstalled())
            throw new InvalidOperationException("Harness 已经安装。请使用“检查 Harness 更新”或更换安装目录。");
        string installRoot = Root;
        string stage = installRoot + ".dsh-installing";
        bool ownsInstallRoot = false;
        if (Directory.Exists(stage))
            DeleteDirectoryTree(stage);
        try
        {
            string commit = await DownloadSourceAsync(stage);
            if (Directory.Exists(installRoot))
            {
                if (Directory.GetFileSystemEntries(installRoot).Length > 0)
                    throw new InvalidOperationException("安装目录不再为空，安装已停止以避免覆盖现有文件。");
                Directory.Delete(installRoot, false);
            }
            Directory.Move(stage, installRoot);
            ownsInstallRoot = true;
            selectedSourceCommit = commit;
            Directory.CreateDirectory(Path.Combine(installRoot, "logs"));
            ApplyWindowsHarnessCompatibility();
            await EnsureNodeAsync();
            await PreparePnpmAsync();
            await InstallDependenciesAsync("install");
            await RunToolAsync("pnpm run build", "build");
            await VerifyRunnableInstallationAsync();
            WriteState(commit);
            SaveConfiguredRoot(installRoot);
            Log("安装位置: " + installRoot);
        }
        catch
        {
            TryDeleteDirectory(stage);
            if (ownsInstallRoot)
                TryDeleteDirectory(installRoot);
            throw;
        }
    }

    private async Task RepairAsync()
    {
        if (!IsInstalled())
            throw new InvalidOperationException("未检测到可修复的 Harness 安装。");
        if (IsInstallationReady())
            throw new InvalidOperationException("Harness 安装完整，无需修复。");
        await ApplyUpdateAsync();
    }

    private async Task UninstallAsync()
    {
        await StopAsync();
        var failures = new List<string>();
        foreach (UninstallTarget target in GetUninstallTargets())
        {
            if (!IsSafeUninstallTarget(target.Path))
            {
                failures.Add(target.Path + "（安全检查拒绝删除）");
                continue;
            }
            if (!Directory.Exists(target.Path) && !File.Exists(target.Path))
            {
                Log("目标不存在，跳过: " + target.Path);
                continue;
            }
            try
            {
                Log("正在删除: " + target.Path);
                if (Directory.Exists(target.Path))
                    DeleteDirectoryTree(target.Path);
                else
                    File.Delete(target.Path);
                Log("已删除: " + target.Path);
            }
            catch (Exception error)
            {
                failures.Add(target.Path + "（" + error.Message + "）");
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                "卸载未完全完成，以下目标删除失败:" + Environment.NewLine +
                String.Join(Environment.NewLine, failures.ToArray()));

        pathBox.Text = "";
        Log("已彻底删除 Harness、用户数据、控制面板配置及 Harness 专用 Node/pnpm。");
    }

    private bool IsSafeUninstallTarget(string target)
    {
        if (String.IsNullOrWhiteSpace(target))
            return false;
        string full = Path.GetFullPath(target).TrimEnd('\\');
        string driveRoot = (Path.GetPathRoot(full) ?? "").TrimEnd('\\');
        if (String.IsNullOrEmpty(full) || String.Equals(full, driveRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        string expectedHome = Path.Combine(userProfile, ".dsh").TrimEnd('\\');
        string expectedSettings = SettingsDirectory.TrimEnd('\\');
        if (String.Equals(full, expectedHome, StringComparison.OrdinalIgnoreCase) ||
            String.Equals(full, expectedSettings, StringComparison.OrdinalIgnoreCase))
            return true;

        string configuredRoot = Root;
        if (!String.IsNullOrWhiteSpace(configuredRoot) &&
            String.Equals(full, Path.GetFullPath(configuredRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
            IsSourceAt(full))
            return true;
        return false;
    }

    private async Task StartAsync(bool openBrowser)
    {
        if (!IsInstalled())
            throw new InvalidOperationException("尚未安装 Harness，请先点击“一键安装”。");
        if (!IsInstallationReady())
            throw new InvalidOperationException("Harness 安装不完整，请点击“修复安装”恢复缺失的官方文件。");
        selectedSourceCommit = LocalCommit();
        await EnsureNodeAsync();
        LogProfileStartupHint();
        if (IsPortOpen(3080))
        {
            int owner = FindPortOwner(3080);
            if (owner > 0 && IsLikelyHarnessProcess(owner))
            {
                string existingUrl = StoredWebUrl();
                Log("Harness 已经在运行。" + (String.IsNullOrEmpty(existingUrl) ? "当前实例没有保存认证地址，请点击“重启”刷新。" : "认证地址可用。"));
                if (openBrowser)
                {
                    if (String.IsNullOrEmpty(existingUrl))
                        throw new InvalidOperationException("Harness 正在运行，但当前实例没有保存认证地址。请点击“重启”生成新的访问地址。");
                    Process.Start(existingUrl);
                }
                return;
            }
            throw new InvalidOperationException("3080 端口正被其他程序占用，请先释放端口后再启动 Harness。");
        }
        Stopwatch startupTime = Stopwatch.StartNew();
        TaskCompletionSource<string> webReady = new TaskCompletionSource<string>();
        ProcessStartInfo psi = await NewHarnessWebProcessAsync();
        server = new Process { StartInfo = psi, EnableRaisingEvents = true };
        server.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
        {
            if (String.IsNullOrEmpty(e.Data))
                return;
            string readyUrl = HarnessStartupPolicy.GetWebReadyUrl(e.Data, 3080);
            if (!String.IsNullOrEmpty(readyUrl))
            {
                Log(HarnessStartupPolicy.RedactWebToken(e.Data));
                webReady.TrySetResult(readyUrl);
            }
            else
            {
                Log(e.Data);
            }
        };
        server.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (!String.IsNullOrEmpty(e.Data)) Log(e.Data); };
        server.Start();
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        WriteState(ReadStateValue("commit"), server.Id.ToString(), "");
        Log("Harness 进程已启动，正在初始化服务和插件...");
        bool portObserved = false;
        bool endpointObserved = false;
        int endpointProbeEvery = HarnessLifecyclePolicy.EndpointProbeIntervalMilliseconds / HarnessLifecyclePolicy.PollIntervalMilliseconds;
        for (int i = 0; i < HarnessLifecyclePolicy.StartupTimeoutSeconds * 1000 / HarnessLifecyclePolicy.PollIntervalMilliseconds; i++)
        {
            bool portOpen = IsPortOpen(3080);
            if (!portObserved && portOpen)
            {
                portObserved = true;
                Log("Web 服务端口已监听，继续等待 Harness 完成初始化...");
            }
            if (portOpen && !endpointObserved && i % endpointProbeEvery == 0)
            {
                endpointObserved = await IsHarnessEndpointReadyAsync();
                if (endpointObserved)
                    Log("Harness 页面响应正常，已确认服务可访问。");
            }
            bool officialReadyLog = webReady.Task.IsCompleted;
            if (HarnessLifecyclePolicy.IsStartupReady(!server.HasExited, portOpen, endpointObserved, officialReadyLog))
            {
                startupTime.Stop();
                string readyUrl = officialReadyLog ? webReady.Task.Result : HarnessLifecyclePolicy.LocalWebUri;
                WriteState(ReadStateValue("commit"), server.Id.ToString(), readyUrl);
                Log("Harness 已就绪，用时 " + startupTime.Elapsed.TotalSeconds.ToString("0.0") + " 秒。" +
                    (officialReadyLog ? "已收到官方就绪日志。" : "已通过本地页面验证。"));
                if (openBrowser)
                {
                    Log("正在打开 Harness 页面...");
                    Process.Start(readyUrl);
                }
                return;
            }
            if (server.HasExited)
                throw new InvalidOperationException("Harness 启动失败，进程已退出，退出码 " + server.ExitCode + "。请查看下方日志。");
            await Task.Delay(HarnessLifecyclePolicy.PollIntervalMilliseconds);
        }
        throw new InvalidOperationException("等待 Harness 完成初始化超过 " + HarnessLifecyclePolicy.StartupTimeoutSeconds + " 秒。" +
            HarnessLifecyclePolicy.StartupFailureMessage(portObserved, endpointObserved));
    }

    private async Task<bool> IsHarnessEndpointReadyAsync()
    {
        try
        {
            using (var cancellation = new CancellationTokenSource(HarnessLifecyclePolicy.EndpointProbeTimeoutMilliseconds))
            using (HttpResponseMessage response = await http.GetAsync(
                HarnessLifecyclePolicy.LocalWebUri,
                HttpCompletionOption.ResponseContentRead,
                cancellation.Token))
            {
                if (!response.IsSuccessStatusCode)
                    return false;
                string content = await response.Content.ReadAsStringAsync();
                return HarnessLifecyclePolicy.IsHarnessDocument(content);
            }
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private void LogProfileStartupHint()
    {
        string profilePackage = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh", "profiles", "web", "package.json");
        if (!File.Exists(profilePackage))
            return;
        try
        {
            var serializer = new JavaScriptSerializer();
            var root = serializer.DeserializeObject(File.ReadAllText(profilePackage)) as Dictionary<string, object>;
            Dictionary<string, object> dsh;
            Dictionary<string, object> profile;
            object bundlesValue;
            if (root == null || !root.TryGetValue("dsh", out bundlesValue) ||
                (dsh = bundlesValue as Dictionary<string, object>) == null ||
                !dsh.TryGetValue("profile", out bundlesValue) ||
                (profile = bundlesValue as Dictionary<string, object>) == null ||
                !profile.TryGetValue("bundles", out bundlesValue))
                return;
            object[] bundles = bundlesValue as object[];
            if (bundles == null)
                return;
            List<string> names = bundles
                .Select(bundle => bundle as string)
                .Where(bundle => !String.IsNullOrWhiteSpace(bundle))
                .ToList();
            int thirdParty = HarnessProfileDiagnostics.CountThirdPartyPackages(names);
            string hint = HarnessProfileDiagnostics.BuildStartupHint(names.Count, thirdParty);
            if (!String.IsNullOrEmpty(hint))
                Log(hint);
        }
        catch
        {
            // A profile is user-owned and optional; diagnostics must never block startup.
        }
    }

    private async Task<ProcessStartInfo> NewHarnessWebProcessAsync()
    {
        string builtCli = Path.Combine(Source, HarnessStartupPolicy.BuiltCliRelativePath);
        if (HarnessStartupPolicy.SelectLaunchMode(File.Exists(builtCli)) == HarnessLaunchMode.BuiltCli)
        {
            Log("启动器：使用已构建 CLI。");
            return NewNodeProcess(QuoteArgument(builtCli) + " " + HarnessStartupPolicy.WebArguments, Source);
        }
        Log("启动器：未找到已构建 CLI，使用兼容启动模式。");
        await PreparePnpmAsync();
        return NewPnpmProcess("dsh " + HarnessStartupPolicy.WebArguments, Source);
    }

    private async Task StopAsync()
    {
        int recordedPid = ParseInt(ReadStateValue("pid"));
        int portPid = FindPortOwner(3080);
        bool recordedAlive = recordedPid > 0 && IsProcessAlive(recordedPid);
        bool recordedIsHarness = recordedAlive && IsLikelyHarnessProcess(recordedPid);
        bool portIsHarness = portPid > 0 && IsLikelyHarnessProcess(portPid);
        StopResolution resolution = StopTargetResolver.Resolve(
            recordedPid,
            portPid,
            recordedAlive,
            recordedIsHarness,
            portIsHarness);

        if (resolution.Kind == StopTargetKind.ForeignPort)
        {
            string commandLine = GetProcessCommandLine(portPid);
            string detail = String.IsNullOrEmpty(commandLine) ? "" : Environment.NewLine + commandLine;
            throw new InvalidOperationException("3080 端口由其他程序占用，管理器不会结束该进程。" + detail);
        }
        if (resolution.Kind == StopTargetKind.HarnessProcess)
        {
            int pid = resolution.ProcessId;
            RunTool("taskkill.exe", "/PID " + pid + " /T /F", Root);
            WriteState(ReadStateValue("commit"), "", "");
            for (int i = 0; i < HarnessLifecyclePolicy.StopWaitAttempts(
                HarnessLifecyclePolicy.StopTimeoutMilliseconds,
                HarnessLifecyclePolicy.PollIntervalMilliseconds) && IsPortOpen(3080); i++)
            {
                await Task.Delay(HarnessLifecyclePolicy.PollIntervalMilliseconds);
            }
            if (IsPortOpen(3080))
            {
                int remainingPid = FindPortOwner(3080);
                throw new InvalidOperationException("已等待 30 秒，但 3080 端口仍被占用。" +
                    (remainingPid > 0 ? "占用进程 PID: " + remainingPid + "。" : ""));
            }
            Log("已停止 Harness 进程树并释放 3080 端口。");
        }
        else
        {
            if (recordedPid > 0)
                WriteState(ReadStateValue("commit"), "", "");
            Log("Harness 当前未运行。");
        }
    }

    private async Task CheckUpdateAsync()
    {
        if (!IsInstalled())
            throw new InvalidOperationException("尚未安装 Harness，请先点击“一键安装”。");
        string branch = await GetDefaultBranchAsync();
        string remote = await GetRemoteCommitAsync(branch);
        string local = LocalCommit();
        Log("官方分支: " + branch);
        Log("本地提交: " + (String.IsNullOrEmpty(local) ? "未知" : local));
        Log("官方提交: " + remote);
        if (!String.IsNullOrEmpty(local) && String.Equals(local, remote, StringComparison.OrdinalIgnoreCase))
        {
            ShowInfo("DeepSeek Harness 已经是最新版本。");
            return;
        }
        if (Ask("发现 DeepSeek Harness 更新，是否现在更新？", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        await ApplyUpdateAsync();
    }

    private async Task ApplyUpdateAsync()
    {
        await StopAsync();
        string stage = Root + ".dsh-update";
        if (Directory.Exists(stage)) DeleteDirectoryTree(stage);
        string backup = Root + ".dsh-backup";
        if (Directory.Exists(backup)) DeleteDirectoryTree(backup);
        string downloadedCommit = await DownloadSourceAsync(stage);
        Directory.Move(Root, backup);
        try
        {
            Directory.Move(stage, Root);
            CopyPersistentDirectory(backup, Root, ".dsh-runtime");
            CopyPersistentDirectory(backup, Root, "logs");
            selectedSourceCommit = downloadedCommit;
            ApplyWindowsHarnessCompatibility();
            await EnsureNodeAsync();
            await PreparePnpmAsync();
            await InstallDependenciesAsync("update-install");
            await RunToolAsync("pnpm run build", "update-build");
            await VerifyRunnableInstallationAsync();
            WriteState(downloadedCommit);
            Log("新版本已构建完成，正在清理旧版本备份...");
            try
            {
                DeleteDirectoryTree(backup);
            }
            catch (Exception cleanupError)
            {
                Log("更新已成功，但旧版本备份暂时无法清理: " + backup + " (" + cleanupError.Message + ")");
            }
            Log("Harness 更新完成。");
        }
        catch (Exception updateError)
        {
            Exception cleanupError = null;
            try
            {
                if (Directory.Exists(Root))
                    DeleteDirectoryTree(Root);
            }
            catch (Exception ex)
            {
                cleanupError = ex;
            }
            if (!Directory.Exists(Root) && Directory.Exists(backup))
                Directory.Move(backup, Root);
            if (Directory.Exists(Root) && cleanupError != null)
                throw new InvalidOperationException("更新失败，自动回滚也未能完成。旧版本备份保留在: " + backup + Environment.NewLine + cleanupError.Message, updateError);
            selectedSourceCommit = ReadStateValue("commit");
            throw;
        }
        finally
        {
            TryDeleteDirectory(stage);
        }
    }

    private async Task<string> DownloadNodeAsync()
    {
        Log("查询官方 Node.js Windows 版本...");
        string json = await FetchTextAsync(NodeIndex);
        var serializer = new JavaScriptSerializer();
        var entries = serializer.DeserializeObject(json) as object[];
        string version = null;
        if (entries != null)
        {
            foreach (object entry in entries)
            {
                var item = entry as Dictionary<string, object>;
                if (item == null || !item.ContainsKey("version") || !item.ContainsKey("lts") || !item.ContainsKey("files"))
                    continue;
                bool lts = item["lts"] != null && item["lts"].ToString() != "false";
                var files = item["files"] as object[];
                bool winX64 = false;
                if (files != null)
                {
                    foreach (object file in files)
                        if (String.Equals(file.ToString(), "win-x64-zip", StringComparison.OrdinalIgnoreCase) ||
                            String.Equals(file.ToString(), "win-x64", StringComparison.OrdinalIgnoreCase))
                            winX64 = true;
                }
                if (lts && winX64)
                {
                    version = item["version"].ToString();
                    break;
                }
            }
        }
        if (String.IsNullOrEmpty(version))
            throw new InvalidOperationException("无法读取 Node.js 官方 LTS 版本列表。");
        string url = "https://nodejs.org/dist/" + version + "/node-" + version + "-win-x64.zip";
        string zip = Path.Combine(Path.GetTempPath(), "dsh-node-" + version + ".zip");
        await DownloadFileAsync(url, zip, true);
        return zip;
    }

    private async Task EnsureNodeAsync()
    {
        string requirement = GetRequiredNodeRequirement();
        Version systemVersion = null;
        foreach (string systemNode in FindSystemExecutables("node.exe"))
        {
            Version candidateVersion;
            if (!TryGetToolVersion(systemNode, "--version", out candidateVersion))
                continue;
            if (systemVersion == null)
                systemVersion = candidateVersion;
            if (!IsNodeCompatible(candidateVersion, requirement))
                continue;
            systemVersion = candidateVersion;
            selectedNodeDirectory = Path.GetDirectoryName(systemNode);
            Log("Node.js 可用: " + systemVersion + "（系统安装，要求 " + requirement + "）。");
            return;
        }

        string privateNode = FindPrivateExecutable("node.exe");
        Version privateVersion;
        if (TryGetToolVersion(privateNode, "--version", out privateVersion) && IsNodeCompatible(privateVersion, requirement))
        {
            selectedNodeDirectory = Path.GetDirectoryName(privateNode);
            Log("Node.js 可用: " + privateVersion + "（Harness 专用运行环境，要求 " + requirement + "）。");
            return;
        }

        string current = systemVersion == null ? "未检测到" : systemVersion.ToString();
        string message = systemVersion == null
            ? "未检测到可用的 Node.js。将下载官方 Node.js LTS，并仅配置给 DeepSeek Harness 使用，不会修改系统中的其他项目。是否继续？"
            : "检测到系统 Node.js " + current + "，但 Harness 要求 " + requirement + "。将下载官方 Node.js LTS，并仅配置给 DeepSeek Harness 使用，不会修改系统 Node.js。是否继续？";
        if (Ask(message, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            throw new InvalidOperationException("未配置符合要求的 Node.js，安装已取消。");

        string nodeZip = await DownloadNodeAsync();
        Log("Node.js 已下载，正在配置 Harness 专用运行环境。");
        InstallNode(nodeZip);
        string installedNode = FindPrivateExecutable("node.exe");
        Version installedVersion;
        if (!TryGetToolVersion(installedNode, "--version", out installedVersion))
            throw new InvalidOperationException("Node.js 已解压，但无法执行 node --version。请检查安全软件或文件权限。");
        if (!IsNodeCompatible(installedVersion, requirement))
            throw new InvalidOperationException("下载的 Node.js " + installedVersion + " 不满足 Harness 要求 " + requirement + "。");
        selectedNodeDirectory = Path.GetDirectoryName(installedNode);
        Log("Node.js 已就绪: " + installedVersion + "。");
    }

    private string GetRequiredNodeRequirement()
    {
        string packageJson = Path.Combine(Source, "package.json");
        if (!File.Exists(packageJson))
            return ">=" + FallbackMinimumNodeMajor;
        string json = File.ReadAllText(packageJson);
        Match match = Regex.Match(json, "\\\"engines\\\"\\s*:\\s*\\{.*?\\\"node\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : ">=" + FallbackMinimumNodeMajor;
    }

    private bool IsNodeCompatible(Version installed, string requirement)
    {
        if (installed == null)
            return false;
        Match minimum = Regex.Match(requirement ?? "", "(?:>=|\\^|~)?\\s*(\\d+)(?:\\.(\\d+))?(?:\\.(\\d+))?");
        if (!minimum.Success)
            return installed.Major >= FallbackMinimumNodeMajor;
        int major = Int32.Parse(minimum.Groups[1].Value);
        int minor = minimum.Groups[2].Success ? Int32.Parse(minimum.Groups[2].Value) : 0;
        int build = minimum.Groups[3].Success ? Int32.Parse(minimum.Groups[3].Value) : 0;
        return installed >= new Version(major, minor, build);
    }

    private void InstallNode(string zip)
    {
        string runtimeRoot = Path.Combine(Root, ".dsh-runtime");
        Directory.CreateDirectory(runtimeRoot);
        string extract = Path.Combine(Path.GetTempPath(), "dsh-node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extract);
        ZipFile.ExtractToDirectory(zip, extract);
        string extracted = null;
        foreach (string dir in Directory.GetDirectories(extract, "node-v*-win-x64"))
            extracted = dir;
        if (String.IsNullOrEmpty(extracted))
            throw new InvalidOperationException("Node.js 压缩包内容不符合预期。");
        if (Directory.Exists(Runtime)) DeleteDirectoryTree(Runtime);
        CopyDirectory(extracted, Path.Combine(runtimeRoot, "node"));
        Directory.Delete(extract, true);
        selectedNodeDirectory = Runtime;
    }

    private async Task<string> DownloadSourceAsync(string destination)
    {
        string zip = Path.Combine(Path.GetTempPath(), "dsh-source-" + Guid.NewGuid().ToString("N") + ".zip");
        string branch = await GetDefaultBranchAsync();
        string extract = Path.Combine(Path.GetTempPath(), "dsh-extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            await DownloadFileAsync(String.Format(RepoZipTemplate, Uri.EscapeDataString(branch)), zip, true);
            Directory.CreateDirectory(extract);
            ZipFile.ExtractToDirectory(zip, extract);
            string root = Directory.GetDirectories(extract)[0];
            if (Directory.Exists(destination)) DeleteDirectoryTree(destination);
            CopyDirectory(root, destination);
            EnsureSourceTreeComplete(destination);
            ValidateDownloadedSource(destination);
            return await GetRemoteCommitAsync(branch);
        }
        finally
        {
            TryDeleteDirectory(extract);
            try
            {
                if (File.Exists(zip))
                    File.Delete(zip);
            }
            catch (Exception cleanupError)
            {
                Log("清理源码下载临时文件失败: " + zip + " (" + cleanupError.Message + ")");
            }
        }
    }

    private void EnsureSourceTreeComplete(string root)
    {
        List<string> missing = MissingInstallationFiles(root);
        if (missing.Count == 0)
            return;
        throw new InvalidOperationException("官方源码下载不完整，缺少: " + String.Join("、", missing.ToArray()));
    }

    private void ValidateDownloadedSource(string root)
    {
        string lockfile = Path.Combine(root, "pnpm-lock.yaml");
        if (!File.Exists(lockfile))
            Log("官方源码未提供 pnpm-lock.yaml，将按官方 package.json 生成本地锁文件。");

        string[] bundledDependencies = Directory.GetDirectories(root, "node_modules", SearchOption.AllDirectories);
        if (bundledDependencies.Length > 0)
            throw new InvalidOperationException("官方源码包中包含预装 node_modules，已停止更新以避免混入非官方依赖。");
    }

    private async Task InstallDependenciesAsync(string logName)
    {
        bool hasLockfile = File.Exists(Path.Combine(Source, "pnpm-lock.yaml"));
        await RunToolAsync(HarnessInstallPolicy.BuildDependencyInstallCommand(hasLockfile), logName);
    }

    private void ApplyWindowsHarnessCompatibility()
    {
        if (!IsWindowsPlatform())
            return;
        string leaseFile = Path.Combine(Source, "packages", "session", "session-persistence-jsonl", "src", "lease.ts");
        string workspaceFile = Path.Combine(Source, "pnpm-workspace.yaml");
        if (!File.Exists(leaseFile))
            return;
        if (!File.Exists(workspaceFile))
            throw new InvalidOperationException("官方 Harness 缺少 pnpm-workspace.yaml，无法安全配置 Windows 兼容安装。");

        string source = File.ReadAllText(leaseFile);
        if (!source.Contains("import { flock } from 'fs-ext'") && !source.Contains("const dshRequire = createRequire(import.meta.url)"))
            return;

        string patchedLease = HarnessInstallPolicy.ApplyWindowsFsExtLeaseCompatibility(source);
        string workspace = File.ReadAllText(workspaceFile);
        string patchedWorkspace = HarnessInstallPolicy.DisableFsExtBuildScript(workspace);
        if (patchedLease != source)
            File.WriteAllText(leaseFile, patchedLease);
        if (patchedWorkspace != workspace)
            File.WriteAllText(workspaceFile, patchedWorkspace);
        Log("已启用官方 Harness 的 Windows 锁实现，并仅跳过 fs-ext 的 node-gyp 编译。");
    }

    private bool IsWindowsPlatform()
    {
        return Environment.OSVersion.Platform == PlatformID.Win32NT ||
            Environment.OSVersion.Platform == PlatformID.Win32Windows ||
            Environment.OSVersion.Platform == PlatformID.Win32S;
    }

    private async Task VerifyRunnableInstallationAsync()
    {
        EnsureSourceTreeComplete(Root);
        Log("正在验证 Harness 运行依赖...");
        await RunToolAsync("pnpm dsh web --help", "verify");
        Log("Harness 运行依赖验证通过。");
    }

    private void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.GetDirectories(source))
        {
            string name = Path.GetFileName(directory);
            CopyDirectory(directory, Path.Combine(destination, name));
        }
        foreach (string file in Directory.GetFiles(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, true);
        }
    }

    private void CopyPersistentDirectory(string oldRoot, string newRoot, string name)
    {
        string source = Path.Combine(oldRoot, name);
        if (Directory.Exists(source))
            CopyDirectory(source, Path.Combine(newRoot, name));
    }

    private void TryDeleteDirectory(string path)
    {
        if (String.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (Directory.Exists(path))
                DeleteDirectoryTree(path);
        }
        catch (Exception ex)
        {
            Log("清理临时目录失败: " + path + " (" + ex.Message + ")");
        }
    }

    private void DeleteDirectoryTree(string path)
    {
        if (!Directory.Exists(path))
            return;
        try
        {
            DeleteDirectoryTreeManaged(path);
        }
        catch (PathTooLongException)
        {
            DeleteDirectoryTreeWithRmdir(path);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            DeleteDirectoryTreeWithRmdir(path);
            return;
        }
        catch (IOException)
        {
            DeleteDirectoryTreeWithRmdir(path);
            return;
        }
        if (Directory.Exists(path))
            DeleteDirectoryTreeWithRmdir(path);
    }

    private void DeleteDirectoryTreeManaged(string path)
    {
        FileAttributes rootAttributes = File.GetAttributes(path);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path, false);
            return;
        }
        foreach (string entry in Directory.GetFileSystemEntries(path))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(entry, false);
                else
                    DeleteDirectoryTreeManaged(entry);
            }
            else
            {
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                File.Delete(entry);
            }
        }
        Directory.Delete(path, false);
    }

    private void DeleteDirectoryTreeWithRmdir(string path)
    {
        int lastExitCode = -1;
        for (int attempt = 1; attempt <= DirectoryCleanupPolicy.FallbackAttempts; attempt++)
        {
            if (!Directory.Exists(path))
                return;
            string extendedPath = DirectoryCleanupPolicy.ToExtendedPath(path);
            var psi = NewProcess("cmd.exe", "/c " + DirectoryCleanupPolicy.FallbackCommand + " " + QuoteArgument(extendedPath), Path.GetDirectoryName(path), false);
            using (var process = Process.Start(psi))
            {
                if (!process.WaitForExit(DirectoryCleanupPolicy.FallbackTimeoutMilliseconds))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("清理目录超过 5 分钟，已停止清理进程。备份目录将保留，之后可再次清理。");
                }
                lastExitCode = process.ExitCode;
            }
            if (!Directory.Exists(path))
                return;
            if (attempt < DirectoryCleanupPolicy.FallbackAttempts)
            {
                Log("清理目录暂未完成，正在重试（" + (attempt + 1) + "/" + DirectoryCleanupPolicy.FallbackAttempts + "）: " + path);
                Thread.Sleep(DirectoryCleanupPolicy.RetryDelayMilliseconds);
            }
        }
        throw new IOException("清理目录失败，已尝试 " + DirectoryCleanupPolicy.FallbackAttempts + " 次，目录仍存在: " + path +
            "。rmdir 最后退出码 " + lastExitCode + "。请关闭占用该目录的程序后重试。");
    }

    private string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private async Task PreparePnpmAsync()
    {
        string required = GetRequiredPnpmVersion();
        selectedPnpm = "";
        selectedCorepack = "";

        Version systemVersion = null;
        foreach (string systemPnpm in FindSystemExecutables("pnpm.cmd"))
        {
            Version candidateVersion;
            if (!TryGetToolVersion(systemPnpm, "--version", out candidateVersion))
                continue;
            if (systemVersion == null)
                systemVersion = candidateVersion;
            if (!IsPnpmCompatible(candidateVersion, required))
                continue;
            systemVersion = candidateVersion;
            selectedPnpm = systemPnpm;
            Log("pnpm 可用: " + systemVersion + "（系统安装，要求 " + required + "）。");
            return;
        }

        string privatePnpm = FindPrivateExecutable("pnpm.cmd");
        Version privateVersion;
        if (TryGetToolVersion(privatePnpm, "--version", out privateVersion) && IsPnpmCompatible(privateVersion, required))
        {
            selectedPnpm = privatePnpm;
            Log("pnpm 可用: " + privateVersion + "（Harness 专用运行环境，要求 " + required + "）。");
            return;
        }

        string found = systemVersion == null ? "未检测到" : systemVersion.ToString();
        string prompt = systemVersion == null
            ? "未检测到可用的 pnpm。需要配置 pnpm@" + required + " 供 DeepSeek Harness 使用，是否继续？"
            : "检测到系统 pnpm " + found + "，但 Harness 要求 " + required + "。需要为 Harness 配置兼容的 pnpm，不会修改系统 pnpm，是否继续？";
        if (Ask(prompt, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            throw new InvalidOperationException("未配置符合要求的 pnpm，操作已取消。");

        string corepack = FindExecutable("corepack.cmd");
        if (!String.IsNullOrEmpty(corepack))
        {
            Log("> corepack prepare pnpm@" + required + " --activate");
            int code = await RunProcessAsync(NewProcess(corepack, "prepare pnpm@" + required + " --activate", Source));
            Version corepackVersion;
            if (code == 0 && TryGetToolVersion(corepack, "pnpm --version", out corepackVersion) && IsPnpmCompatible(corepackVersion, required))
            {
                selectedCorepack = corepack;
                Log("pnpm 已通过 Corepack 配置: " + corepackVersion + "。");
                return;
            }
            Log("Corepack 未能配置兼容 pnpm，改用 npm 安装。");
        }

        string npm = FindExecutable("npm.cmd");
        if (String.IsNullOrEmpty(npm))
            throw new InvalidOperationException("未找到可用的 npm 或 Corepack，无法配置 pnpm。");
        Directory.CreateDirectory(Runtime);
        Log("> npm install --global pnpm@" + required);
        int installCode = await RunProcessAsync(NewProcess(npm, "install --global --prefix \"" + Runtime + "\" pnpm@" + required, Source));
        privatePnpm = FindPrivateExecutable("pnpm.cmd");
        if (installCode != 0 || !TryGetToolVersion(privatePnpm, "--version", out privateVersion) || !IsPnpmCompatible(privateVersion, required))
            throw new InvalidOperationException("自动配置 pnpm 失败，退出码 " + installCode + "。");
        selectedPnpm = privatePnpm;
        Log("pnpm 已就绪: " + privateVersion + "。");
    }

    private string GetRequiredPnpmVersion()
    {
        string json = File.ReadAllText(Path.Combine(Source, "package.json"));
        Match match = Regex.Match(json, "\"packageManager\"\\s*:\\s*\"pnpm@([0-9]+(?:\\.[0-9]+){0,2})");
        return match.Success ? match.Groups[1].Value : "11.7.0";
    }

    private bool IsPnpmCompatible(Version installed, string required)
    {
        Version expected;
        return installed != null && TryParseVersion(required, out expected) && installed == expected;
    }

    private async Task RunToolAsync(string command, string logName)
    {
        await RunToolAsync(command, logName, Source);
    }

    private async Task RunToolAsync(string command, string logName, string workingDirectory)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            Log("> " + command + (attempt == 1 ? "" : "（第 2 次尝试）"));
            ProcessStartInfo psi = NewToolProcess(command, workingDirectory);
            ProcessExecutionResult result = await RunProcessDetailedAsync(psi);
            if (result.ExitCode == 0)
                return;

            if (BuildRetryPolicy.ShouldRetry(command, attempt))
            {
                Log("构建第一次失败，正在自动重试；这通常是首次生成依赖或缓存并发造成的临时错误。");
                await Task.Delay(1000);
                continue;
            }

            string detail = LastOutputLines(result.StandardError, 30);
            if (String.IsNullOrWhiteSpace(detail))
                detail = LastOutputLines(result.StandardOutput, 30);
            if (!String.IsNullOrWhiteSpace(detail))
                Log("命令失败的最后输出：" + Environment.NewLine + detail);
            bool fsExtDeclared = false;
            try
            {
                string lockfile = Path.Combine(Source, "pnpm-lock.yaml");
                if (File.Exists(lockfile))
                    fsExtDeclared = HarnessInstallPolicy.LockfileContainsPackage(File.ReadAllText(lockfile), "fs-ext");
                if (!fsExtDeclared)
                    fsExtDeclared = HarnessInstallPolicy.SourceManifestContainsPackage(Source, "fs-ext");
            }
            catch
            {
                // The command failure is already known; a diagnostic must never hide it.
            }
            string hint = HarnessInstallPolicy.BuildInstallFailureHint(
                command,
                result.StandardError + Environment.NewLine + result.StandardOutput,
                fsExtDeclared);
            if (!String.IsNullOrEmpty(hint))
                Log(hint);
            throw new InvalidOperationException(command + " 失败，退出码 " + result.ExitCode + "." +
                (String.IsNullOrEmpty(hint) ? "" : Environment.NewLine + hint));
        }
    }

    private async Task<int> RunProcessAsync(ProcessStartInfo psi)
    {
        ProcessExecutionResult result = await RunProcessDetailedAsync(psi);
        return result.ExitCode;
    }

    private async Task<ProcessExecutionResult> RunProcessDetailedAsync(ProcessStartInfo psi)
    {
        var process = new Process { StartInfo = psi };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.Start();
        Task output = Task.Run(async delegate
        {
            string line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null)
            {
                standardOutput.AppendLine(line);
                Log(line);
            }
        });
        Task error = Task.Run(async delegate
        {
            string line;
            while ((line = await process.StandardError.ReadLineAsync()) != null)
            {
                standardError.AppendLine(line);
                Log(line);
            }
        });
        await Task.Run(delegate { process.WaitForExit(); });
        await Task.WhenAll(output, error);
        return new ProcessExecutionResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    private ProcessStartInfo NewToolProcess(string command, string workingDirectory)
    {
        string[] parts = command.Split(new[] { ' ' }, 2);
        if (parts[0] == "corepack")
        {
            string corepack = FindExecutable("corepack.cmd");
            if (String.IsNullOrEmpty(corepack))
                throw new InvalidOperationException("未找到 Corepack。");
            return NewProcess(corepack, parts[1], workingDirectory);
        }
        return NewPnpmProcess(command.Substring(5), workingDirectory);
    }

    private static string LastOutputLines(string output, int count)
    {
        if (String.IsNullOrWhiteSpace(output))
            return "";
        string[] lines = output.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int start = Math.Max(0, lines.Length - count);
        return String.Join(Environment.NewLine, lines.Skip(start).ToArray());
    }

    private ProcessStartInfo NewPnpmProcess(string args, string workingDirectory)
    {
        if (!String.IsNullOrEmpty(selectedPnpm))
            return NewProcess(selectedPnpm, args, workingDirectory);
        if (!String.IsNullOrEmpty(selectedCorepack))
            return NewProcess(selectedCorepack, "pnpm " + args, workingDirectory);
        throw new InvalidOperationException("未找到可用的 Node.js、Corepack 或 pnpm 运行环境。");
    }

    private ProcessStartInfo NewNodeProcess(string args, string workingDirectory)
    {
        string node = Path.Combine(selectedNodeDirectory, "node.exe");
        if (!File.Exists(node))
            throw new InvalidOperationException("已检测到 Node.js，但无法定位 node.exe。请重新扫描或修复安装。");
        return NewProcess(node, args, workingDirectory);
    }

    private string FindExecutable(string fileName)
    {
        string local = FindPrivateExecutable(fileName);
        return !String.IsNullOrEmpty(local) ? local : FindSystemExecutable(fileName);
    }

    private string FindPrivateExecutable(string fileName)
    {
        string local = Path.Combine(Runtime, fileName);
        return File.Exists(local) ? local : null;
    }

    private string FindSystemExecutable(string fileName)
    {
        return FindSystemExecutables(fileName).FirstOrDefault();
    }

    private List<string> FindSystemExecutables(string fileName)
    {
        var matches = new List<string>();
        Action<string> add = delegate(string candidate)
        {
            if (String.IsNullOrWhiteSpace(candidate))
                return;
            string full = candidate.Trim().Trim('"');
            if (File.Exists(full) && !matches.Any(x => String.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
                matches.Add(full);
        };
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string folder in path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                add(Path.Combine(folder.Trim().Trim('"'), fileName));
            }
            catch { }
        }
        add(ReadAppPath(fileName, Registry.CurrentUser));
        add(ReadAppPath(fileName, Registry.LocalMachine));
        string[] commonFolders = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs")
        };
        foreach (string folder in commonFolders)
        {
            add(Path.Combine(folder, fileName));
        }
        return matches;
    }

    private string ReadAppPath(string fileName, RegistryKey hive)
    {
        try
        {
            using (RegistryKey key = hive.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\" + fileName))
            {
                if (key == null)
                    return null;
                return key.GetValue(null) as string;
            }
        }
        catch { return null; }
    }

    private bool TryGetToolVersion(string file, string arguments, out Version version)
    {
        version = null;
        if (String.IsNullOrEmpty(file) || !File.Exists(file))
            return false;
        try
        {
            var psi = NewProcess(file, arguments, Source);
            using (var process = Process.Start(psi))
            {
                string output = process.StandardOutput.ReadToEnd() + " " + process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0 && TryParseVersion(output, out version);
            }
        }
        catch { return false; }
    }

    private bool TryParseVersion(string text, out Version version)
    {
        version = null;
        Match match = Regex.Match(text ?? "", "v?(\\d+)(?:\\.(\\d+))?(?:\\.(\\d+))?");
        if (!match.Success)
            return false;
        int major = Int32.Parse(match.Groups[1].Value);
        int minor = match.Groups[2].Success ? Int32.Parse(match.Groups[2].Value) : 0;
        int build = match.Groups[3].Success ? Int32.Parse(match.Groups[3].Value) : 0;
        version = new Version(major, minor, build);
        return true;
    }

    private ProcessStartInfo NewProcess(string file, string args, string workingDirectory, bool redirectOutput = true)
    {
        var psi = new ProcessStartInfo(file, args);
        psi.WorkingDirectory = workingDirectory;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = redirectOutput;
        psi.RedirectStandardError = redirectOutput;
        if (redirectOutput)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }
        try
        {
            string pathKey = psi.EnvironmentVariables.Keys.Cast<string>()
                .FirstOrDefault(key => String.Equals(key, "PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
            string nodePath = String.IsNullOrEmpty(selectedNodeDirectory) ? Runtime : selectedNodeDirectory;
            psi.EnvironmentVariables[pathKey] = nodePath + ";" + Environment.GetEnvironmentVariable("PATH");
            BuildCommitEnvironment.Apply(psi, selectedSourceCommit);
        }
        catch (ArgumentException)
        {
            // Some launchers provide both Path and PATH; inheriting the environment is safer there.
        }
        return psi;
    }

    private void RunTool(string file, string args, string workingDirectory)
    {
        var psi = NewProcess(file, args, workingDirectory, false);
        using (var p = Process.Start(psi))
        {
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException(file + " 执行失败，退出码 " + p.ExitCode + "。");
        }
    }

    /// <summary>
    /// Working directory for the fetch helper, its request files, and in-memory
    /// response files. Building a path here never creates the directory: with a
    /// working .NET transport nothing is ever staged, so the mere act of asking
    /// must not leave an empty folder behind in %TEMP%.
    /// </summary>
    private string NetworkScratchDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "dsh-manager");
    }

    /// <summary>
    /// Creates the scratch directory. Callers invoke this immediately before their
    /// first write so the directory appears only when something is really staged.
    /// </summary>
    private string EnsureNetworkScratchDirectory()
    {
        string directory = NetworkScratchDirectory();
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Writes the fetch helper once per session and returns its path. The helper is
    /// delivered from the compiled assembly so the panel stays a single file.
    /// </summary>
    private string NodeHelperFilePath()
    {
        if (!String.IsNullOrEmpty(nodeHelperPath) && File.Exists(nodeHelperPath))
            return nodeHelperPath;
        string path = Path.Combine(EnsureNetworkScratchDirectory(), "fetch-helper.mjs");
        File.WriteAllText(path, NodeNetworkPolicy.HelperScript, new UTF8Encoding(false));
        nodeHelperPath = path;
        return path;
    }

    /// <summary>
    /// Whether outbound requests must use the Node transport. Requires the helper
    /// file to be writable, which is the same requirement the panel already has for
    /// its staging directories.
    /// </summary>
    private bool ShouldUseNodeTransport()
    {
        if (!nodeTransportRequired)
            return false;
        try
        {
            NodeHelperFilePath();
            return true;
        }
        catch (Exception ex)
        {
            Log("无法准备 Node 网络通道: " + ex.Message);
            return false;
        }
    }

    private void RequireNodeTransport(Exception error)
    {
        if (nodeTransportRequired)
            return;
        nodeTransportRequired = true;
        Log("检测到本机 .NET 的 TLS 连接失败（通常是代理的 TUN/fake-IP 与 Windows SCHANNEL 不兼容）。");
        Log("已切换到 Node 网络通道继续请求；本地 Harness 页面访问不受影响。");
        if (error != null)
            Log("原始错误: " + FlattenException(error));
    }

    /// <summary>
    /// Fetches <paramref name="url"/> to <paramref name="path"/> through the Node
    /// helper. Runs out of process so a stalled socket cannot wedge the UI thread.
    /// </summary>
    private async Task FetchToFileViaNodeAsync(string url, string path, int timeoutSeconds)
    {
        string helper = NodeHelperFilePath();
        string requestFile = Path.Combine(
            EnsureNetworkScratchDirectory(),
            "request-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(
                requestFile,
                NodeNetworkPolicy.BuildRequestJson(url, path, timeoutSeconds, NodeNetworkPolicy.UserAgent),
                new UTF8Encoding(false));

            string arguments = NodeNetworkPolicy.QuoteArgument(helper) + " " + NodeNetworkPolicy.QuoteArgument(requestFile);
            ProcessStartInfo psi = NewNodeProcess(arguments, Root);
            ProcessExecutionResult result = await RunProcessDetailedAsync(psi);
            string report = (result.StandardOutput ?? "").Trim();

            if (result.ExitCode != 0 || NodeNetworkPolicy.IsFailureReport(report))
            {
                string detail = report;
                if (String.IsNullOrWhiteSpace(detail))
                    detail = (result.StandardError ?? "").Trim();
                throw new InvalidOperationException(
                    "Node 网络通道请求失败" +
                    (String.IsNullOrWhiteSpace(detail) ? "。" : "：" + Environment.NewLine + detail) +
                    Environment.NewLine + "地址: " + url);
            }

            int status = NodeNetworkPolicy.ReadReportedNumber(report, "status", 0);
            if (status < 200 || status >= 300)
                throw new InvalidOperationException("下载失败，服务器返回 HTTP " + status + "。" + Environment.NewLine + "地址: " + url);
        }
        finally
        {
            try
            {
                if (File.Exists(requestFile))
                    File.Delete(requestFile);
            }
            catch
            {
                // A leftover request file is harmless; never mask the fetch outcome.
            }
        }
    }

    /// <summary>
    /// Downloads to a file, preferring .NET and permanently switching to the Node
    /// transport when the local TLS stack cannot complete a handshake.
    ///
    /// <paramref name="ensureDestinationDirectory"/> is false for scratch requests
    /// whose directory is created only if the Node transport actually stages
    /// something, so routing a request through here never leaves an empty folder
    /// behind in %TEMP%.
    /// </summary>
    private async Task DownloadFileAsync(string url, string path, bool ensureDestinationDirectory)
    {
        string parent = Path.GetDirectoryName(path);
        if (ensureDestinationDirectory && !String.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        if (ShouldUseNodeTransport())
        {
            await FetchToFileViaNodeAsync(url, path, NodeNetworkPolicy.DefaultTimeoutSeconds);
            return;
        }

        bool retryAfterCreatingDirectory = false;
        bool fallBackToNode = false;
        try
        {
            await FetchToFileViaDotNetAsync(url, path);
            return;
        }
        catch (DirectoryNotFoundException)
        {
            // Defence in depth: a caller may stage into a directory that the lazy
            // creation path deliberately did not make. Create it and retry once,
            // rather than reporting a local path problem as a network failure.
            if (String.IsNullOrEmpty(parent))
                throw;
            Directory.CreateDirectory(parent);
            Log("下载目标目录不存在，已创建后重试: " + parent);
            retryAfterCreatingDirectory = true;
        }
        catch (Exception ex)
        {
            // This compiler targets C# 5, which forbids both exception filters and
            // await inside a catch block, so every catch here only decides and the
            // work happens after the handlers.
            //
            // The transport test must run over the whole chain and run FIRST:
            // HttpClient wraps the SCHANNEL failure in an HttpRequestException whose
            // own message is only "发送请求时出错。", so a top-level check would miss
            // it and misreport a broken TLS stack as an unconnectable server.
            if (NodeNetworkPolicy.IsTlsStackFailure(ex))
            {
                RequireNodeTransport(ex);
                fallBackToNode = true;
            }
            else if (ex is HttpRequestException)
            {
                throw new InvalidOperationException("无法建立 HTTPS 连接，请检查网络代理、证书或防火墙设置。" + Environment.NewLine + FlattenException(ex));
            }
            else
            {
                throw;
            }
        }

        if (fallBackToNode)
        {
            await FetchToFileViaNodeAsync(url, path, NodeNetworkPolicy.DefaultTimeoutSeconds);
            return;
        }
        if (retryAfterCreatingDirectory)
        {
            await FetchToFileViaDotNetAsync(url, path);
            return;
        }
    }

    /// <summary>One .NET download attempt. Exceptions propagate to the caller.</summary>
    private async Task FetchToFileViaDotNetAsync(string url, string path)
    {
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("下载失败，服务器返回 " + (int)response.StatusCode + " " + response.ReasonPhrase + "。");
            using (var input = await response.Content.ReadAsStreamAsync())
            using (var output = File.Create(path))
            {
                await input.CopyToAsync(output);
            }
        }
    }

    /// <summary>
    /// Fetches a small text resource through the same transport rules as
    /// <see cref="DownloadFileAsync"/>, for API responses that are parsed in memory.
    ///
    /// The scratch file lives directly in %TEMP% rather than in the Node scratch
    /// directory: the .NET transport writes it itself, so routing it through a
    /// directory that is only created for the Node transport would fail with a
    /// missing-path error on every request.
    /// </summary>
    private async Task<string> FetchTextAsync(string url)
    {
        string temporary = Path.Combine(
            Path.GetTempPath(),
            "dsh-fetch-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await DownloadFileAsync(url, temporary, false);
            return File.ReadAllText(temporary);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // A leftover response file is harmless; never mask the request outcome.
            }
        }
    }

    private async Task<string> GetDefaultBranchAsync()
    {
        string json;
        try
        {
            json = await FetchTextAsync(RepoInfoApi);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法连接 GitHub 官方仓库信息接口。" + Environment.NewLine + FlattenException(ex));
        }
        Match match = Regex.Match(json, "\"default_branch\"\\s*:\\s*\"([^\"]+)\"");
        if (!match.Success)
            throw new InvalidOperationException("无法读取官方仓库默认分支。");
        return match.Groups[1].Value;
    }

    private async Task<string> GetRemoteCommitAsync(string branch)
    {
        string json;
        try
        {
            json = await FetchTextAsync(String.Format(RepoApiTemplate, Uri.EscapeDataString(branch)));
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法连接 GitHub 官方更新接口，请检查网络代理、证书或防火墙设置。" + Environment.NewLine + FlattenException(ex));
        }
        Match match = Regex.Match(json, "\"sha\"\\s*:\\s*\"([0-9a-fA-F]{40})\"");
        if (!match.Success) throw new InvalidOperationException("无法读取官方 Harness 提交版本。");
        return match.Groups[1].Value;
    }

    private string FlattenException(Exception ex)
    {
        var parts = new List<string>();
        for (Exception current = ex; current != null; current = current.InnerException)
            parts.Add(current.Message);
        return String.Join(" -> ", parts.ToArray());
    }

    private bool IsPortOpen(int port)
    {
        try
        {
            using (var client = new System.Net.Sockets.TcpClient())
            {
                var task = client.ConnectAsync("127.0.0.1", port);
                return task.Wait(250) && client.Connected;
            }
        }
        catch { return false; }
    }

    private int FindPortOwner(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            using (var process = Process.Start(psi))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                foreach (string line in output.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains("LISTENING"))
                        continue;
                    string[] parts = Regex.Split(line.Trim(), "\\s+");
                    int pid;
                    if (parts.Length >= 5 && IsMatchingLocalEndpoint(parts[1], port) && Int32.TryParse(parts[parts.Length - 1], out pid))
                        return pid;
                }
            }
        }
        catch (Exception ex)
        {
            Log("读取端口占用进程失败: " + ex.Message);
        }
        return 0;
    }

    private bool IsMatchingLocalEndpoint(string endpoint, int port)
    {
        string suffix = ":" + port;
        if (String.IsNullOrEmpty(endpoint) || !endpoint.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;
        string address = endpoint.Substring(0, endpoint.Length - suffix.Length).Trim('[', ']');
        return address == "127.0.0.1" || address == "0.0.0.0" || address == "::" || address == "::1";
    }

    private string GetProcessCommandLine(int pid)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                    return item["CommandLine"] == null ? "" : item["CommandLine"].ToString();
            }
        }
        catch
        {
        }
        return "";
    }

    private int GetParentProcessId(int pid)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = " + pid))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                    return item["ParentProcessId"] == null ? 0 : Convert.ToInt32(item["ParentProcessId"]);
            }
        }
        catch
        {
        }
        return 0;
    }

    private string ReadStateValue(string key)
    {
        if (!File.Exists(StateFile)) return "";
        string json = File.ReadAllText(StateFile);
        Match match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    private bool IsInstalled()
    {
        return IsSourceAt(Root);
    }

    private bool IsInstallationReady()
    {
        return IsInstalled() && MissingInstallationFiles().Count == 0;
    }

    private List<string> MissingInstallationFiles()
    {
        return MissingInstallationFiles(Root);
    }

    private static List<string> MissingInstallationFiles(string root)
    {
        return HarnessInstallationValidator.FindMissingFiles(relativePath =>
            File.Exists(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    private bool IsSourceAt(string root)
    {
        if (String.IsNullOrWhiteSpace(root))
            return false;
        return File.Exists(Path.Combine(root, "package.json")) &&
            Directory.Exists(Path.Combine(root, "apps"));
    }

    private List<string> DiscoverInstallRoots()
    {
        var candidates = new List<string>();
        Action<string> add = delegate(string candidate)
        {
            if (String.IsNullOrEmpty(candidate))
                return;
            try
            {
                string full = Path.GetFullPath(candidate).TrimEnd('\\');
                if (IsSourceAt(full) && !candidates.Any(x => String.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
                    candidates.Add(full);
            }
            catch { }
        };

        add(LoadConfiguredRoot());
        add("D:\\deepseek-harness");
        add("D:\\DeepSeekHarness");
        add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekHarness"));
        add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeepSeekHarness"));
        return candidates;
    }

    private string LoadConfiguredRoot()
    {
        if (File.Exists(SettingsFile))
        {
            string configured = ReadJsonValue(File.ReadAllText(SettingsFile), "installRoot");
            if (!String.IsNullOrEmpty(configured))
                return configured;
        }
        return DefaultInstallRoot();
    }

    private bool IsConfiguredRoot()
    {
        if (!File.Exists(SettingsFile))
            return false;
        string configured = LoadConfiguredRoot();
        return !String.IsNullOrEmpty(configured) &&
            String.Equals(
                Path.GetFullPath(Root).TrimEnd('\\'),
                Path.GetFullPath(configured).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
    }

    private void SaveConfiguredRoot(string root)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsFile, "{\"installRoot\":\"" + root.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}");
    }

    private string ReadJsonValue(string json, string key)
    {
        Match match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"");
        if (!match.Success)
            return "";
        return match.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
    }

    private string LocalVersion()
    {
        string packageJson = Path.Combine(Source, "package.json");
        if (File.Exists(packageJson))
        {
            Match packageVersion = Regex.Match(File.ReadAllText(packageJson), "\"version\"\\s*:\\s*\"([^\"]+)\"");
            if (packageVersion.Success)
                return packageVersion.Groups[1].Value;
        }
        string commit = LocalCommit();
        if (!String.IsNullOrEmpty(commit))
            return commit.Length > 7 ? commit.Substring(0, 7) : commit;
        string head = Path.Combine(Source, ".git", "HEAD");
        if (File.Exists(head))
        {
            string value = File.ReadAllText(head).Trim();
            if (value.StartsWith("ref: "))
            {
                string reference = value.Substring(5).Trim().Replace('/', Path.DirectorySeparatorChar);
                string refFile = Path.Combine(Source, ".git", reference);
                if (File.Exists(refFile))
                    return File.ReadAllText(refFile).Trim();
            }
            else if (Regex.IsMatch(value, "^[0-9a-fA-F]{40}$"))
            {
                return value;
            }
        }
        return "未知";
    }

    private string LocalCommit()
    {
        string commit = ReadStateValue("commit");
        if (Regex.IsMatch(commit ?? "", "^[0-9a-fA-F]{40}$"))
            return commit;
        string head = Path.Combine(Source, ".git", "HEAD");
        if (File.Exists(head))
        {
            string value = File.ReadAllText(head).Trim();
            if (value.StartsWith("ref: "))
            {
                string reference = value.Substring(5).Trim().Replace('/', Path.DirectorySeparatorChar);
                string refFile = Path.Combine(Source, ".git", reference);
                if (File.Exists(refFile))
                    return File.ReadAllText(refFile).Trim();
            }
            else if (Regex.IsMatch(value, "^[0-9a-fA-F]{40}$"))
            {
                return value;
            }
        }
        return "";
    }

    private bool IsProcessAlive(int pid)
    {
        try
        {
            using (Process.GetProcessById(pid))
                return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsLikelyHarnessProcess(int pid)
    {
        int current = pid;
        for (int depth = 0; depth < 8 && current > 0; depth++)
        {
            if (HarnessProcessIdentityPolicy.IsHarnessCommandLine(GetProcessCommandLine(current)))
                return true;
            int parent = GetParentProcessId(current);
            if (parent == current)
                break;
            current = parent;
        }
        return false;
    }

    private string StoredWebUrl()
    {
        string value = StateSecretProtection.Unprotect(ReadStateValue("url"));
        return HarnessStartupPolicy.GetWebReadyUrl("dsh web: " + value, 3080);
    }

    private void WriteState(string commit, string pid = null, string url = null)
    {
        Directory.CreateDirectory(Root);
        string currentPid = pid ?? ReadStateValue("pid");
        string currentUrl = url == null ? ReadStateValue("url") : StateSecretProtection.Protect(url);
        string json = "{\"commit\":\"" + JsonEscape(commit ?? "") +
            "\",\"pid\":\"" + JsonEscape(currentPid) +
            "\",\"url\":\"" + JsonEscape(currentUrl) +
            "\",\"updated\":\"" + DateTime.UtcNow.ToString("o") + "\"}";
        File.WriteAllText(StateFile, json);
    }

    private static string JsonEscape(string value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private int ParseInt(string value)
    {
        int result;
        return Int32.TryParse(value, out result) ? result : 0;
    }

    private DialogResult Ask(string text, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        if (InvokeRequired)
            return (DialogResult)Invoke(new Func<DialogResult>(delegate { return MessageBox.Show(this, text, "DeepSeek Harness", buttons, icon); }));
        return MessageBox.Show(this, text, "DeepSeek Harness", buttons, icon);
    }

    private void ShowInfo(string text)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(delegate { ShowInfo(text); }));
            return;
        }
        MessageBox.Show(this, text, "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

/// <summary>
/// Plugin marketplace window. Every mutation goes through
/// <c>dsh plugin --profile web</c>, which forwards to pnpm inside the profile; the
/// panel never edits the profile manifest itself, so the CLI stays the single
/// owner of the bundle layer list.
/// </summary>
public sealed class PluginMarketplaceForm : Form
{
    private readonly string source;
    private readonly string dshHome;
    private readonly string nodeDirectory;
    private readonly string pnpmPath;
    private readonly string corepackPath;

    private readonly TextBox searchBox = new TextBox();
    private readonly Button searchButton = new Button();
    private readonly Button clearButton = new Button();
    private readonly CheckBox githubToggle = new CheckBox();
    private readonly ListBox resultList = new ListBox();
    private readonly TextBox detailBox = new TextBox();
    private readonly Button installButton = new Button();
    private readonly Button removeButton = new Button();
    private readonly Button refreshButton = new Button();
    private readonly Button restartHintButton = new Button();
    private readonly RichTextBox logBox = new RichTextBox();
    private readonly Label statusLabel = new Label();

    private readonly string manifestFile;
    private readonly object gate = new object();
    private bool busy;
    private List<PluginCatalogEntry> catalogEntries = new List<PluginCatalogEntry>();
    private List<PluginRepository> repositories = new List<PluginRepository>();
    private string installedManifest = "";

    /// <summary>
    /// Fetches a text resource through the panel's own transport, so the marketplace
    /// shares the .NET-then-Node HTTP fallback instead of duplicating it.
    /// </summary>
    private readonly Func<string, Task<string>> fetchTextResolver;

    public PluginMarketplaceForm(
        string source,
        string dshHome,
        string nodeDirectory,
        string pnpmPath,
        string corepackPath,
        Func<string, Task<string>> fetchTextResolver)
    {
        this.source = source;
        this.dshHome = dshHome;
        this.nodeDirectory = nodeDirectory;
        this.pnpmPath = pnpmPath;
        this.corepackPath = corepackPath;
        this.fetchTextResolver = fetchTextResolver;
        manifestFile = ProfileManifestPolicy.ManifestPath(dshHome, ProfileManifestPolicy.DefaultProfileName);

        Text = "插件市场 — DeepSeek Harness";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Width = 980;
        Height = 680;
        MinimumSize = new Size(860, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();
        LoadInstalledManifest();
        PopulateCatalog("");
        Log("插件市场已打开。");
        Log("profile: " + ProfileManifestPolicy.DefaultProfileName + "（即 dsh web 使用的 profile）");
        Log("配置文件: " + manifestFile);
        Log("已安装的组合包层：" + Environment.NewLine + ProfileManifestPolicy.DescribeLayers(installedManifest));
        Log("提示：组合包成员变化后需要重启 Harness 才会生效。");
    }

    private void BuildUi()
    {
        var main = new TableLayoutPanel();
        main.Dock = DockStyle.Fill;
        main.Padding = new Padding(12);
        main.ColumnCount = 1;
        main.RowCount = 4;
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        Controls.Add(main);

        // Row 0: search controls.
        var searchRow = new TableLayoutPanel();
        searchRow.Dock = DockStyle.Fill;
        searchRow.ColumnCount = 6;
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        searchRow.Controls.Add(new Label { Text = "搜索", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        searchBox.Dock = DockStyle.Fill;
        searchBox.TextChanged += delegate { PopulateCatalog(searchBox.Text); };
        searchRow.Controls.Add(searchBox, 1, 0);
        AddButton(searchRow, searchButton, "搜索", SearchClick);
        searchRow.Controls.Add(searchButton, 2, 0);
        AddButton(searchRow, clearButton, "清空", ClearClick);
        searchRow.Controls.Add(clearButton, 3, 0);
        githubToggle.Text = "在 GitHub 搜索仓库";
        githubToggle.Dock = DockStyle.Fill;
        githubToggle.CheckedChanged += delegate { UpdateSearchModeText(); };
        searchRow.Controls.Add(githubToggle, 4, 0);
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        searchRow.Controls.Add(statusLabel, 5, 0);
        main.Controls.Add(searchRow, 0, 0);

        // Row 1: result list and details.
        var split = new SplitContainer();
        split.Dock = DockStyle.Fill;
        split.Orientation = Orientation.Vertical;
        split.SplitterDistance = 380;
        resultList.Dock = DockStyle.Fill;
        resultList.IntegralHeight = false;
        resultList.SelectedIndexChanged += delegate { ShowSelectedDetail(); };
        split.Panel1.Controls.Add(resultList);
        detailBox.Dock = DockStyle.Fill;
        detailBox.Multiline = true;
        detailBox.ReadOnly = true;
        detailBox.ScrollBars = ScrollBars.Vertical;
        detailBox.WordWrap = true;
        detailBox.BackColor = Color.White;
        split.Panel2.Controls.Add(detailBox);
        main.Controls.Add(split, 0, 1);

        // Row 2: actions.
        var actions = new FlowLayoutPanel();
        actions.Dock = DockStyle.Fill;
        actions.WrapContents = false;
        AddButton(actions, installButton, "安装所选", InstallClick);
        AddButton(actions, removeButton, "卸载所选", RemoveClick);
        AddButton(actions, refreshButton, "刷新已安装", RefreshClick);
        AddButton(actions, restartHintButton, "重启 Harness", RestartHintClick);
        main.Controls.Add(actions, 0, 2);

        // Row 3: log.
        logBox.ReadOnly = true;
        logBox.ScrollBars = RichTextBoxScrollBars.Vertical;
        logBox.WordWrap = true;
        logBox.Dock = DockStyle.Fill;
        logBox.BackColor = Color.White;
        main.Controls.Add(logBox, 0, 3);

        UpdateSearchModeText();
        SetBusy(false);
    }

    private void AddButton(Control parent, Button button, string text, EventHandler handler)
    {
        button.Text = text;
        button.AutoSize = true;
        button.Height = 30;
        button.Click += handler;
        parent.Controls.Add(button);
    }

    /// <summary>Root containing the built CLI. The panel's install root is the source tree.</summary>
    private string DshCliPath()
    {
        return Path.Combine(source, "apps", "cli", "lib", "bin.js");
    }

    private string NodeExecutable()
    {
        if (!String.IsNullOrWhiteSpace(nodeDirectory))
        {
            string candidate = Path.Combine(nodeDirectory, "node.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (String.IsNullOrWhiteSpace(folder))
                continue;
            try
            {
                string candidate = Path.Combine(folder.Trim().Trim('"'), "node.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }
        return null;
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)delegate { Log(message); });
            return;
        }
        foreach (string line in (message ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (String.IsNullOrWhiteSpace(line))
                continue;
            logBox.SelectionStart = logBox.TextLength;
            logBox.SelectionLength = 0;
            logBox.SelectionColor = Color.Black;
            logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine);
        }
        logBox.SelectionColor = logBox.ForeColor;
        logBox.ScrollToCaret();
    }

    private void SetBusy(bool value)
    {
        busy = value;
        installButton.Enabled = !value;
        removeButton.Enabled = !value;
        refreshButton.Enabled = !value;
        searchButton.Enabled = !value;
        githubToggle.Enabled = !value;
        resultList.Enabled = !value;
        UseWaitCursor = value;
    }

    private void RunAsync(string title, Func<Task> action)
    {
        lock (gate)
        {
            if (busy)
            {
                MessageBox.Show(this, "当前已有操作正在执行，请等待完成。", "插件市场", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            busy = true;
        }
        SetBusy(true);
        Log(title + "...");
        Task.Run(action).ContinueWith(delegate(Task task)
        {
            BeginInvoke((Action)delegate
            {
                SetBusy(false);
                if (task.IsFaulted)
                {
                    string message = task.Exception == null ? "未知错误" : task.Exception.GetBaseException().Message;
                    Log("失败：" + message);
                    MessageBox.Show(this, message, "插件市场", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    Log("完成。");
                }
                LoadInstalledManifest();
                RefreshListOnly();
            });
        });
    }

    private void LoadInstalledManifest()
    {
        try
        {
            installedManifest = File.Exists(manifestFile) ? File.ReadAllText(manifestFile) : "";
        }
        catch (Exception ex)
        {
            installedManifest = "";
            Log("读取 profile 配置失败: " + ex.Message);
        }
    }

    private List<string> InstalledRemovableBundles()
    {
        return ProfileManifestPolicy.RemovableBundles(installedManifest);
    }

    private bool IsInstalledBundle(string name)
    {
        List<string> bundles = ProfileManifestPolicy.ReadBundles(installedManifest);
        return bundles.Any(bundle => String.Equals(bundle, name, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateSearchModeText()
    {
        searchButton.Text = githubToggle.Checked ? "搜索 GitHub" : "过滤";
    }

    /// <summary>
    /// Renders curated entries. The text box filters locally so typing narrows the
    /// reviewed list without a network round trip.
    /// </summary>
    private void PopulateCatalog(string query)
    {
        catalogEntries = PluginCatalog.Search(query);
        repositories = new List<PluginRepository>();
        resultList.BeginUpdate();
        resultList.Items.Clear();
        foreach (PluginCatalogEntry entry in catalogEntries)
        {
            string state = IsInstalledBundle(entry.Name) ? "[已安装] " : "";
            string origin = entry.Official ? "官方" : "第三方";
            resultList.Items.Add(state + entry.ListLabel + "  (" + origin + ")");
        }
        resultList.EndUpdate();
        if (catalogEntries.Count > 0)
            resultList.SelectedIndex = 0;
        else
            detailBox.Text = "没有匹配的插件。可以勾选“在 GitHub 搜索仓库”再按回车或点击按钮，去 GitHub 上找。";
    }

    private void PopulateRepositories()
    {
        resultList.BeginUpdate();
        resultList.Items.Clear();
        foreach (PluginRepository repository in repositories)
            resultList.Items.Add(repository.ListLabel);
        resultList.EndUpdate();
        if (repositories.Count > 0)
            resultList.SelectedIndex = 0;
        else
            detailBox.Text = "GitHub 没有返回结果。";
    }

    private void RefreshListOnly()
    {
        if (githubToggle.Checked && repositories.Count > 0)
            PopulateRepositories();
        else
            PopulateCatalog(searchBox.Text);
    }

    private string SelectedSpec()
    {
        int index = resultList.SelectedIndex;
        if (index < 0)
            return "";
        if (githubToggle.Checked && index < repositories.Count)
            return repositories[index].InstallSpec;
        if (index < catalogEntries.Count)
            return catalogEntries[index].Spec;
        return "";
    }

    private void ShowSelectedDetail()
    {
        int index = resultList.SelectedIndex;
        if (index < 0)
        {
            detailBox.Text = "";
            return;
        }
        var lines = new List<string>();

        if (githubToggle.Checked && index < repositories.Count)
        {
            PluginRepository repository = repositories[index];
            lines.Add("GitHub 仓库");
            lines.Add("名称: " + repository.FullName);
            lines.Add("Star: " + repository.Stars);
            lines.Add("地址: " + repository.HtmlUrl);
            lines.Add("");
            lines.Add("简介: " + (String.IsNullOrWhiteSpace(repository.Description) ? "(无)" : repository.Description));
            lines.Add("");
            lines.Add("将使用的 spec: " + repository.InstallSpec);
            lines.Add("");
            lines.Add(PluginSpecPolicy.DescribeInstallRisk(repository.InstallSpec));
            lines.Add("");
            lines.Add("注意：GitHub 搜索结果是未经审核的第三方代码。");
            lines.Add("安装前请先打开仓库确认它声明了 dsh.bundle，并检查其内容。");
        }
        else if (index < catalogEntries.Count)
        {
            PluginCatalogEntry entry = catalogEntries[index];
            lines.Add("精选插件");
            lines.Add("名称: " + entry.Name);
            lines.Add("标题: " + entry.DisplayName);
            lines.Add("来源: " + (entry.Official ? "官方" : "第三方（已人工确认）"));
            lines.Add("spec: " + entry.Spec);
            if (!String.IsNullOrWhiteSpace(entry.Homepage))
                lines.Add("主页: " + entry.Homepage);
            lines.Add("");
            lines.Add("简介: " + entry.Summary);
            lines.Add("");
            bool installed = IsInstalledBundle(entry.Name);
            lines.Add("当前状态: " + (installed ? "已安装" : "未安装"));
            if (installed)
            {
                bool removable = InstalledRemovableBundles().Any(
                    name => String.Equals(name, entry.Name, StringComparison.OrdinalIgnoreCase));
                lines.Add("可否卸载: " + (removable ? "可以（外部组合包）" : "不可以（内置组合包，由 dsh 安装目录提供）"));
            }
            lines.Add("");
            lines.Add(PluginSpecPolicy.DescribeInstallRisk(entry.Spec));
        }

        detailBox.Text = String.Join(Environment.NewLine, lines.ToArray());
    }

    private void ClearClick(object sender, EventArgs e)
    {
        searchBox.Text = "";
        githubToggle.Checked = false;
        PopulateCatalog("");
    }

    private void RefreshClick(object sender, EventArgs e)
    {
        LoadInstalledManifest();
        RefreshListOnly();
        Log("已重新读取 profile 配置。");
        Log("已安装的组合包层：" + Environment.NewLine + ProfileManifestPolicy.DescribeLayers(installedManifest));
    }

    private void RestartHintClick(object sender, EventArgs e)
    {
        MessageBox.Show(
            this,
            "请在主窗口中点击“重启”，让新的组合包层生效。",
            "插件市场",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    /// <summary>
    /// Searches GitHub. The default query targets repositories that declare the
    /// bundle manifest field, which is the installable set.
    /// </summary>
    private void SearchClick(object sender, EventArgs e)
    {
        if (!githubToggle.Checked)
        {
            PopulateCatalog(searchBox.Text);
            return;
        }
        string query = searchBox.Text.Trim();
        if (query.Length == 0)
            query = GitHubPluginPolicy.DefaultQueries[0];
        RunAsync("正在搜索 GitHub 仓库（" + query + "）", delegate { return SearchGitHubAsync(query); });
    }

    private async Task SearchGitHubAsync(string query)
    {
        string url = GitHubPluginPolicy.BuildSearchUrl(query, 30);
        string json = await fetchTextResolver(url);
        List<PluginRepository> found = GitHubPluginPolicy.ParseSearchResponse(json);
        repositories = found;
        catalogEntries = new List<PluginCatalogEntry>();
        Log("GitHub 返回 " + found.Count + " 个仓库。");
        if (found.Count == 0)
            Log("可以换个关键词，例如 " + GitHubPluginPolicy.DefaultQueries[0] + " 或插件名。");
    }

    private void InstallClick(object sender, EventArgs e)
    {
        string spec = SelectedSpec();
        if (String.IsNullOrWhiteSpace(spec))
        {
            MessageBox.Show(this, "请先在上方列表中选择一个插件。", "插件市场", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!PluginSpecPolicy.IsSafePackageName(spec) &&
            PluginSpecPolicy.Classify(spec) == PluginSpecKind.NpmPackage)
        {
            MessageBox.Show(this, "这个 npm 包名包含不安全字符，已拒绝安装。", "插件市场", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string prompt = "即将安装：" + Environment.NewLine + spec + Environment.NewLine + Environment.NewLine +
            PluginSpecPolicy.DescribeInstallRisk(spec) + Environment.NewLine + Environment.NewLine +
            "安装完成后需要重启 Harness 才会生效。确定继续吗？";
        DialogResult answer = MessageBox.Show(this, prompt, "确认安装插件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
            return;
        RunAsync("正在安装插件 " + spec, delegate { return InstallPluginAsync(spec); });
    }

    private async Task InstallPluginAsync(string spec)
    {
        int exitCode = await RunDshPluginAsync(new[] { "add", spec });
        if (exitCode == 0)
        {
            LoadInstalledManifest();
            Log("插件已安装: " + spec);
            Log("当前组合包层：" + Environment.NewLine + ProfileManifestPolicy.DescribeLayers(installedManifest));
            Log("请回到主窗口点击“重启”，让该插件生效。");
            MessageBox.Show(
                this,
                "插件已安装。" + Environment.NewLine + Environment.NewLine +
                "请回到主窗口点击“重启”让插件生效。" + Environment.NewLine +
                "若该插件提供工具，还需在 agent preset 中启用对应工具行，agent 才能看到它。",
                "插件市场",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // pnpm >= 10 blocks a git dependency's prepare script until the profile
        // allowlists it. The CLI names the file; surface the exact remedy.
        if (PluginSpecPolicy.TriggersPrepareScript(spec))
        {
            string workspaceFile = Path.Combine(Path.GetDirectoryName(manifestFile), "pnpm-workspace.yaml");
            Log("该插件来自 Git 源码，pnpm 默认阻止它自己的构建脚本。");
            Log("上面的 pnpm 输出会给出需要加入 allowBuilds 的包键。");
            Log("把那个键加入: " + workspaceFile);
            Log("格式为：" + Environment.NewLine + "allowBuilds:" + Environment.NewLine + "  <包名>: true");
            Log("然后回到这里重新安装。授权等于允许该包代码在你的机器上执行，请只对可信来源授权。");
        }
        throw new InvalidOperationException("dsh plugin add 失败，退出码 " + exitCode + "。请查看上方日志。");
    }

    private void RemoveClick(object sender, EventArgs e)
    {
        int index = resultList.SelectedIndex;
        string name = "";
        if (githubToggle.Checked)
        {
            MessageBox.Show(this, "GitHub 搜索结果不能直接卸载。请切换到精选列表，或到“已安装”条目上操作。", "插件市场", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (index >= 0 && index < catalogEntries.Count)
            name = catalogEntries[index].Name;
        if (String.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "请先在上方列表中选择一个插件。", "插件市场", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!IsInstalledBundle(name))
        {
            MessageBox.Show(this, name + " 当前没有安装。", "插件市场", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!InstalledRemovableBundles().Any(item => String.Equals(item, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                this,
                name + " 是内置组合包，由 dsh 安装目录提供，不能卸载。",
                "插件市场",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        string prompt = "即将卸载：" + Environment.NewLine + name + Environment.NewLine + Environment.NewLine +
            "会同时移除该依赖和它贡献的配置层。卸载后需要重启 Harness 才会生效。" + Environment.NewLine +
            "确定继续吗？";
        if (MessageBox.Show(this, prompt, "确认卸载插件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        RunAsync("正在卸载插件 " + name, delegate { return RemovePluginAsync(name); });
    }

    private async Task RemovePluginAsync(string name)
    {
        int exitCode = await RunDshPluginAsync(new[] { "remove", name });
        if (exitCode != 0)
            throw new InvalidOperationException("dsh plugin remove 失败，退出码 " + exitCode + "。请查看上方日志。");
        LoadInstalledManifest();
        Log("插件已卸载: " + name);
        Log("当前组合包层：" + Environment.NewLine + ProfileManifestPolicy.DescribeLayers(installedManifest));
        Log("请回到主窗口点击“重启”，让改动生效。");
    }

    /// <summary>
    /// Runs <c>node apps/cli/lib/bin.js plugin --profile web &lt;args&gt;</c>. The
    /// panel deliberately never writes the profile manifest itself; the CLI owns the
    /// layer reconciliation.
    /// </summary>
    private async Task<int> RunDshPluginAsync(string[] arguments)
    {
        string node = NodeExecutable();
        if (String.IsNullOrWhiteSpace(node))
            throw new InvalidOperationException("未找到 Node.js，无法调用 dsh CLI。请先在主窗口执行一次启动或安装。");
        string cli = DshCliPath();
        if (!File.Exists(cli))
            throw new InvalidOperationException("未找到已构建的 dsh CLI: " + cli + Environment.NewLine +
                "请在主窗口点击“启动”（会使用兼容启动模式）或重新构建 Harness。");

        var parts = new List<string>();
        parts.Add(NodeNetworkPolicy.QuoteArgument(cli));
        parts.Add("plugin");
        parts.Add("--profile");
        parts.Add(ProfileManifestPolicy.DefaultProfileName);
        foreach (string argument in arguments)
            parts.Add(NodeNetworkPolicy.QuoteArgument(argument));

        var psi = new ProcessStartInfo(node, String.Join(" ", parts.ToArray()));
        psi.WorkingDirectory = source;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        // dsh forwards to pnpm and needs it on PATH, exactly as the panel's own
        // tool invocations do.
        string pathKey = "PATH";
        string pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        string prefix = !String.IsNullOrWhiteSpace(nodeDirectory) ? nodeDirectory : Path.GetDirectoryName(node);
        if (!String.IsNullOrWhiteSpace(pnpmPath))
            prefix = Path.GetDirectoryName(pnpmPath) + ";" + prefix;
        else if (!String.IsNullOrWhiteSpace(corepackPath))
            prefix = Path.GetDirectoryName(corepackPath) + ";" + prefix;
        psi.EnvironmentVariables[pathKey] = prefix + ";" + pathValue;
        psi.EnvironmentVariables["DSH_HOME"] = dshHome;

        Log("> dsh plugin --profile " + ProfileManifestPolicy.DefaultProfileName + " " + String.Join(" ", arguments));
        return await RunProcessStreamingAsync(psi);
    }

    /// <summary>Runs a process and echoes both streams into the marketplace log.</summary>
    private async Task<int> RunProcessStreamingAsync(ProcessStartInfo psi)
    {
        var process = new Process { StartInfo = psi };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.Start();
        Task output = Task.Run(async delegate
        {
            string line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null)
            {
                standardOutput.AppendLine(line);
                Log(line);
            }
        });
        Task error = Task.Run(async delegate
        {
            string line;
            while ((line = await process.StandardError.ReadLineAsync()) != null)
            {
                standardError.AppendLine(line);
                Log(line);
            }
        });
        await Task.Run(delegate { process.WaitForExit(); });
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }
}

public static class Program
{
    [STAThread]
    public static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ManagerForm());
    }
}
