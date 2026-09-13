using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Management;
using Microsoft.Win32;

// Version metadata. Without these the executable reports 0.0.0.0, which leaves the
// installer and the "Apps and features" entry showing nothing useful.
[assembly: System.Reflection.AssemblyTitle("DeepSeek Harness 控制面板")]
[assembly: System.Reflection.AssemblyProduct("DeepSeek Harness Control Panel")]
[assembly: System.Reflection.AssemblyCompany("")]
[assembly: System.Reflection.AssemblyVersion("0.1.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.1.2.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("0.1.2")]

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

/// <summary>
/// What a removal target holds. The kind drives the default selection, because a
/// program directory can be reinstalled but a user-data directory cannot be rebuilt.
/// </summary>
public enum UninstallTargetKind
{
    /// <summary>The Harness program tree and its private Node/pnpm runtime.</summary>
    ProgramFiles,
    /// <summary>Settings, API keys, sessions, and attachments under the Harness home.</summary>
    UserData,
    /// <summary>This control panel's own settings.</summary>
    PanelSettings
}

public sealed class UninstallTarget
{
    public string Path { get; private set; }
    public string Description { get; private set; }
    public UninstallTargetKind Kind { get; private set; }

    public UninstallTarget(string path, string description, UninstallTargetKind kind)
    {
        Path = path;
        Description = description;
        Kind = kind;
    }

    /// <summary>
    /// Whether this target starts selected. User data does not: it holds credentials
    /// and conversation history that cannot be recovered, so removing it must be a
    /// deliberate choice rather than a default that is scrolled past.
    /// </summary>
    public bool SelectedByDefault
    {
        get { return Kind != UninstallTargetKind.UserData; }
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
        AddTarget(targets, installRoot, "Harness 安装目录（其中的专用 Node/pnpm 如存在会一并删除）", UninstallTargetKind.ProgramFiles);
        AddTarget(targets, harnessHome, "Harness 用户数据（配置、API Key、会话和附件）", UninstallTargetKind.UserData);
        AddTarget(targets, settingsDirectory, "控制面板配置", UninstallTargetKind.PanelSettings);
        return targets;
    }

    private static void AddTarget(
        List<UninstallTarget> targets,
        string path,
        string description,
        UninstallTargetKind kind)
    {
        if (String.IsNullOrWhiteSpace(path))
            return;
        string full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        if (!targets.Any(target => String.Equals(target.Path, full, StringComparison.OrdinalIgnoreCase)))
            targets.Add(new UninstallTarget(full, description, kind));
    }
}

/// <summary>
/// Measures how much a removal target holds, and renders the choice the user made.
/// Separated from the dialog so the wording and the size rules are testable.
/// </summary>
public static class UninstallSelectionPolicy
{
    /// <summary>Reported when the path does not exist at all.</summary>
    public const long Missing = -1;

    /// <summary>
    /// Reported when the path exists but could not be measured. "Unknown" and "empty"
    /// are different answers, and showing 0 B for an unreadable directory would lie.
    /// </summary>
    public const long Unmeasurable = -2;

    public static long MeasureSizeBytes(string path)
    {
        if (String.IsNullOrWhiteSpace(path))
            return Missing;
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).Length;
            if (!Directory.Exists(path))
                return Missing;

            long total = 0;
            foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    // A file that vanished or is locked is simply not counted; a size
                    // that is slightly low beats failing the whole dialog.
                }
            }
            return total;
        }
        catch (Exception)
        {
            return Unmeasurable;
        }
    }

    /// <summary>A human-readable size for the dialog.</summary>
    public static string DescribeSize(long bytes)
    {
        if (bytes == Missing)
            return "不存在";
        if (bytes == Unmeasurable)
            return "无法测量";
        if (bytes < 1024)
            return bytes + " B";
        double kilobytes = bytes / 1024.0;
        if (kilobytes < 1024)
            return kilobytes.ToString("0.0") + " KB";
        double megabytes = kilobytes / 1024.0;
        if (megabytes < 1024)
            return megabytes.ToString("0.0") + " MB";
        return (megabytes / 1024.0).ToString("0.00") + " GB";
    }

    /// <summary>
    /// The confirmation text. It names every target with its size and marks whether the
    /// user chose it, so the summary and the checkboxes cannot disagree.
    /// </summary>
    public static string DescribeSelection(IEnumerable<UninstallTarget> targets, Func<UninstallTarget, bool> isSelected)
    {
        var lines = new List<string>();
        bool anySelected = false;
        bool anyKept = false;

        if (targets != null)
        {
            foreach (UninstallTarget target in targets)
            {
                bool selected = isSelected != null && isSelected(target);
                string size = DescribeSize(MeasureSizeBytes(target.Path));
                lines.Add((selected ? "[删除] " : "[保留] ") + target.Description);
                lines.Add("        " + target.Path + "   (" + size + ")");
                if (selected)
                    anySelected = true;
                else
                    anyKept = true;
            }
        }

        if (!anySelected)
            lines.Add("没有勾选任何要删除的项目。");
        else if (anyKept)
            lines.Add("未勾选的项目会完整保留。");

        return String.Join(Environment.NewLine, lines.ToArray());
    }

    /// <summary>
    /// The trailing warning. It calls out the irreversible case explicitly, because
    /// removing user data cannot be undone by reinstalling.
    /// </summary>
    public static string BuildWarning(IEnumerable<UninstallTarget> targets, Func<UninstallTarget, bool> isSelected)
    {
        bool removesUserData = false;
        if (targets != null && isSelected != null)
        {
            foreach (UninstallTarget target in targets)
            {
                if (target.Kind == UninstallTargetKind.UserData && isSelected(target))
                    removesUserData = true;
            }
        }

        string warning = "删除后无法恢复。";
        if (removesUserData)
        {
            warning += Environment.NewLine +
                "你勾选了用户数据：其中的 API Key、会话记录和附件将被永久删除，" +
                "重新安装 Harness 也无法找回。";
        }
        return warning;
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

/// <summary>
/// Formats an unexpected exception into a report the user can act on. Kept free of
/// dialogs and file IO so the text is unit-testable, and so the crash path itself
/// cannot throw.
/// </summary>
public static class UnexpectedErrorReport
{
    /// <summary>
    /// Renders the type, message, stack, and inner-exception chain.
    ///
    /// Every field is read defensively: a stack overflow or an out-of-memory fault
    /// can make <c>Message</c> or <c>StackTrace</c> throw, and an exception handler
    /// that throws while reporting a crash loses the original fault entirely.
    /// </summary>
    public static string Format(string source, Exception error)
    {
        var builder = new StringBuilder();
        builder.AppendLine("控制面板遇到未预期的错误。");
        builder.AppendLine();
        builder.AppendLine("位置: " + SafeText(source));
        if (error == null)
        {
            builder.AppendLine("异常: (无)");
            return builder.ToString();
        }

        int depth = 0;
        for (Exception current = error; current != null; current = current.InnerException)
        {
            string prefix = depth == 0 ? "异常" : "  内部异常 " + depth;
            builder.AppendLine(prefix + ": " + SafeTypeName(current));
            builder.AppendLine("  消息: " + SafeText(SafeMessage(current)));
            string stack = SafeText(SafeStackTrace(current));
            if (!String.IsNullOrEmpty(stack))
                builder.AppendLine("  堆栈: " + stack);
            depth++;
            if (depth > 8)
            {
                builder.AppendLine("  (内部异常链过长，已截断)");
                break;
            }
        }
        return builder.ToString();
    }

    private static string SafeTypeName(Exception error)
    {
        try
        {
            return error.GetType().FullName;
        }
        catch (Exception)
        {
            return "(无法读取异常类型)";
        }
    }

    private static string SafeMessage(Exception error)
    {
        try
        {
            return error.Message;
        }
        catch (Exception)
        {
            // A corrupted exception can throw from ToString() and from Message.
            return "(无法读取异常消息)";
        }
    }

    private static string SafeStackTrace(Exception error)
    {
        try
        {
            return error.StackTrace;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string SafeText(string value)
    {
        return value ?? "";
    }
}

public static class HarnessInstallationValidator
{    public static readonly string[] RequiredFiles = new[]
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

    /// <summary>
    /// Passes the official source commit to a child process.
    ///
    /// Accessing <see cref="ProcessStartInfo.EnvironmentVariables"/> throws when the
    /// inherited environment holds case-insensitive duplicate keys, which a proxy
    /// setup commonly produces (HTTP_PROXY plus http_proxy, NO_PROXY plus no_proxy).
    /// .NET's environment dictionary is a case-insensitive Hashtable, so copying the
    /// inherited environment fails on the collision. The commit hash is diagnostic
    /// metadata, so dropping it is strictly better than failing the build.
    /// </summary>
    /// <returns>True when the commit was recorded, false when it had to be dropped.</returns>
    public static bool Apply(ProcessStartInfo process, string commit)
    {
        if (process == null || String.IsNullOrWhiteSpace(commit))
            return false;
        try
        {
            process.EnvironmentVariables[VariableName] = commit.Trim();
            return true;
        }
        catch (ArgumentException)
        {
            // Duplicate case-variant keys in the inherited environment.
            return false;
        }
    }

    /// <summary>
    /// Whether the inherited environment contains keys that differ only by case.
    /// Read through the case-sensitive view, because the process environment itself
    /// cannot be enumerated once such a collision exists.
    /// </summary>
    public static bool HasCaseInsensitiveDuplicateKeys()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        System.Collections.IDictionary variables = Environment.GetEnvironmentVariables();
        foreach (object key in variables.Keys)
        {
            string name = key as string;
            if (name == null)
                continue;
            if (!seen.Add(name))
                return true;
        }
        return false;
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

    /// <summary>
    /// The web launch arguments for a given port. The port is appended to the fixed
    /// arguments so the panel and Harness agree on where the service lives.
    /// </summary>
    public static string BuildWebArguments(int port)
    {
        return WebArguments + " " + HarnessPortPolicy.BuildPortArgument(port);
    }

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

/// <summary>
/// Measures text so the drawn buttons can size themselves to their content.
/// </summary>
public static class UiMeasure
{
    /// <summary>
    /// The width an action button needs: its label, its icon, and the padding around
    /// both. Measured rather than fixed because the nine action buttons share one row and
    /// their labels differ a lot in length.
    /// </summary>
    public static int MeasureButtonWidth(string text, UiIcon icon)
    {
        Size textSize = TextRenderer.MeasureText(text ?? "", UiStyle.ButtonFont());
        int width = textSize.Width + UiStyle.ButtonPadding;
        if (UiIconPainter.HasIcon(icon))
            width += UiStyle.IconSize + UiStyle.IconGap;
        return Math.Max(width, 84);
    }

    /// <summary>
    /// The width a log toolbar button needs, which is tighter: five of them share a row
    /// with the search box.
    /// </summary>
    public static int MeasureToolbarButtonWidth(string text, UiIcon icon)
    {
        Size textSize = TextRenderer.MeasureText(text ?? "", UiStyle.ButtonFont());
        int width = textSize.Width + 22;
        if (UiIconPainter.HasIcon(icon))
            width += UiStyle.IconSize + 6;
        return Math.Max(width, 34);
    }
}

/// <summary>
/// Every height the layout needs, derived from the fonts rather than hard-coded.
///
/// A fixed pixel height is only correct at one text size. On a display with scaling the
/// same point size renders taller, the text outgrows its container, and the bottom of it
/// is clipped. Deriving each height from the font it has to contain makes the layout
/// follow the text instead of assuming it.
/// </summary>
public static class UiMetrics
{
    /// <summary>Height of a bordered or filled value box.</summary>
    public static int FieldHeight()
    {
        return Math.Max(36, UiStyle.BodyFont().Height + 10);
    }

    /// <summary>Height of a caption line above a field.</summary>
    public static int CaptionHeight()
    {
        return Math.Max(22, UiStyle.LabelFont().Height + 5);
    }

    /// <summary>Gap between a caption and the value under it.</summary>
    public const int CaptionGap = 6;

    /// <summary>Height of a card's title row.</summary>
    public static int CardTitleHeight()
    {
        return Math.Max(30, UiStyle.CardTitleFont().Height + 9);
    }

    /// <summary>Card padding above and below its contents.</summary>
    public const int CardPaddingY = 16;

    /// <summary>Height of an action button.</summary>
    public static int ButtonHeight()
    {
        return Math.Max(UiStyle.ButtonHeight, UiStyle.ButtonFont().Height + 26);
    }

    /// <summary>Height of a log toolbar control.</summary>
    public static int ToolHeight()
    {
        return Math.Max(UiStyle.ToolButtonHeight, UiStyle.ButtonFont().Height + 20);
    }

    /// <summary>Height of the search field, which follows the toolbar.</summary>
    public static int SearchHeight()
    {
        return Math.Max(30, UiStyle.BodyFont().Height + 12);
    }

    /// <summary>
    /// The height a card needs for a title plus one row of fields: the runtime information
    /// card, which is the tallest of the fixed rows.
    /// </summary>
    public static int InfoCardHeight()
    {
        return 126;
    }

    /// <summary>The height a card needs for a title plus a single row of buttons.</summary>
    public static int ActionCardHeight()
    {
        return 62;
    }

    /// <summary>The header band: the brand mark, which is the tallest thing in it.</summary>
    public static int HeaderHeight()
    {
        return 126;
    }

    /// <summary>The brand mark's square size.</summary>
    public static int BrandMarkSize()
    {
        return 94;
    }
}

/// <summary>
/// Shared geometry. Small helpers so the drawn shapes agree everywhere.
/// </summary>
public static class UiShapes
{
    /// <summary>A rounded rectangle path, used by every card, chip, and button.</summary>
    public static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        // An empty rectangle has nothing to round, and GDI+ rejects the arcs it would
        // produce. Cards and fields get resized to nothing while a window is minimized.
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return path;
        int diameter = Math.Max(1, radius * 2);
        if (diameter > bounds.Width) diameter = bounds.Width;
        if (diameter > bounds.Height) diameter = bounds.Height;
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Fills a rounded rectangle and optionally strokes it. The path is disposed here
    /// because callers would otherwise leak one GDI+ object per repaint.
    /// </summary>
    public static void DrawRounded(
        Graphics graphics,
        Rectangle bounds,
        int radius,
        Color fill,
        Color border,
        int borderWidth)
    {
        // Inset by the border width so the stroke is not clipped by the bounds.
        var target = new Rectangle(
            bounds.X + borderWidth,
            bounds.Y + borderWidth,
            Math.Max(1, bounds.Width - (borderWidth * 2)),
            Math.Max(1, bounds.Height - (borderWidth * 2)));
        using (System.Drawing.Drawing2D.GraphicsPath path = RoundedRect(target, radius))
        {
            using (var brush = new SolidBrush(fill))
                graphics.FillPath(brush, path);
            if (borderWidth > 0)
            {
                using (var pen = new Pen(border, borderWidth))
                    graphics.DrawPath(pen, path);
            }
        }
    }
}

/// <summary>
/// The page gradient behind every card.
///
/// Kept as one guarded helper because the unguarded version crashed the panel: a
/// minimized window has no client area at all (ClientRectangle is 0x0), Windows still
/// sends it the erase message, and LinearGradientBrush refuses an empty rectangle. The
/// exception came out of a paint message, so it surfaced as a crash dialog over a window
/// that looked perfectly healthy.
/// </summary>
public static class UiBackground
{
    /// <summary>
    /// Fills <paramref name="bounds"/> with the page colour, doing nothing when there is no
    /// area to fill. Callers pass the raw client rectangle, which is empty whenever the
    /// window is minimized or still has no size.
    ///
    /// This was a gradient, and the gradient was the panel's slowest operation by two
    /// orders of magnitude: measured on the machine this was written for, filling the
    /// window with a gradient took ~45 ms where a solid fill takes under 1 ms (the same
    /// measurement against the screen returned 495 ms versus 0.6 ms). The page is repainted
    /// once for every transparent container, about fifteen times per frame, so the gradient
    /// alone accounted for most of a second of startup. The two colours it interpolated
    /// were two points apart in a near-white blue, so nothing is visible for the trade.
    /// </summary>
    public static void Paint(Graphics graphics, Rectangle bounds)
    {
        if (graphics == null || bounds.Width <= 0 || bounds.Height <= 0)
            return;
        using (var brush = new SolidBrush(UiStyle.WindowBackground))
            graphics.FillRectangle(brush, bounds);
    }
}

/// <summary>
/// A white rounded card with a hairline border, matching the grouped panels in the
/// design. WinForms panels are square, so the shape is painted rather than configured.
/// </summary>
public sealed class UiCardPanel : Panel
{
    public int CornerRadius { get; set; }
    public Color BorderColor { get; set; }

    /// <summary>
    /// Whether to paint the soft drop shadow. Off for a card that is flush against a
    /// container edge, where the shadow would only bleed onto the border.
    /// </summary>
    public bool ShowShadow { get; set; }

    public UiCardPanel()
    {
        CornerRadius = UiStyle.CardRadius;
        BorderColor = UiStyle.CardBorder;
        BackColor = UiStyle.WindowBackground;
        ShowShadow = true;
        // The card paints its own background; double buffering stops the flicker that
        // comes with repainting a custom surface on every layout pass.
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        // Paint the window colour first so the rounded corners blend into the page.
        using (var background = new SolidBrush(BackColor))
            e.Graphics.FillRectangle(background, ClientRectangle);

        if (ShowShadow)
            DrawShadow(e.Graphics); // currently a no-op; see the note below

        // Inset by one pixel so the hairline border is not half-clipped at the edges.
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        UiShapes.DrawRounded(e.Graphics, bounds, CornerRadius, Color.White, BorderColor, 1);
        base.OnPaint(e);
    }

    /// <summary>
    /// A soft shadow built from a few translucent rounded rectangles that grow outward and
    /// fade. WinForms has no shadow primitive, and a single translucent rectangle reads as
    /// a grey halo rather than depth.
    /// </summary>
    /// <summary>
    /// Reserved for card depth. A ring shadow was tried here and abandoned: drawn in a
    /// custom double-buffered Panel it never reached the screen, not even when painted in
    /// solid colour to prove the code ran. Depth is carried by the page-to-card contrast
    /// and the hairline border instead, which is what actually reads at this scale.
    /// </summary>
    private void DrawShadow(Graphics graphics)
    {
    }
}

/// <summary>A rounded host for native text boxes so fields match the rest of the UI.</summary>
public sealed class UiInputPanel : Panel
{
    public Color SurfaceColor { get; set; }

    public UiInputPanel()
    {
        SurfaceColor = UiStyle.CardBackground;
        BackColor = UiStyle.FieldBackground;
        Padding = new Padding(10, 7, 10, 5);
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var background = new SolidBrush(SurfaceColor))
            e.Graphics.FillRectangle(background, ClientRectangle);
        UiShapes.DrawRounded(
            e.Graphics,
            new Rectangle(0, 0, Width - 1, Height - 1),
            UiStyle.FieldRadius,
            UiStyle.FieldBackground,
            UiStyle.FieldBorder,
            1);
        base.OnPaint(e);
    }
}

/// <summary>A circular icon tile used by the four runtime facts and the log heading.</summary>
public sealed class UiInfoIcon : Control
{
    public UiIcon Icon { get; set; }
    public bool DrawCircle { get; set; }

    public UiInfoIcon()
    {
        DrawCircle = true;
        DoubleBuffered = true;
        BackColor = UiStyle.CardBackground;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var background = new SolidBrush(Parent == null ? UiStyle.CardBackground : Parent.BackColor))
            e.Graphics.FillRectangle(background, ClientRectangle);
        int side = Math.Min(Width, Height);
        var bounds = new Rectangle((Width - side) / 2, (Height - side) / 2, side - 1, side - 1);
        if (DrawCircle)
        {
            using (var brush = new SolidBrush(UiStyle.IconCircleBackground))
                e.Graphics.FillEllipse(brush, bounds);
        }
        int iconSize = Math.Max(18, (int)(side * 0.42));
        UiIconPainter.Draw(
            e.Graphics,
            Icon,
            new Rectangle((Width - iconSize) / 2, (Height - iconSize) / 2, iconSize, iconSize),
            DrawCircle ? UiStyle.InkBlue : UiStyle.Primary);
    }
}

/// <summary>
/// The brand mark: the application icon on a rounded gradient tile, the way the design
/// presents it. GDI+ has no gradient-rounded-rectangle primitive, so the shape is drawn.
/// </summary>
public sealed class UiBrandMark : Control
{
    /// <summary>The embedded multi-frame icon, added by build.ps1 with /resource.</summary>
    private const string ResourceName = "DeepSeekHarness.ico";

    private static Image source;
    private static bool sourceLoaded;

    public UiBrandMark()
    {
        DoubleBuffered = true;
        BackColor = UiStyle.WindowBackground;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var background = new SolidBrush(BackColor))
            e.Graphics.FillRectangle(background, ClientRectangle);

        // A square tile, centred in whatever space the layout gives it.
        int side = Math.Min(Width, Height);
        var tile = new Rectangle((Width - side) / 2, (Height - side) / 2, side - 1, side - 1);
        // A collapsed or minimized layout hands out an empty tile, and both the gradient
        // below and a gradient brush here would refuse it.
        if (tile.Width <= 0 || tile.Height <= 0)
            return;

        Image art = Source();
        if (art != null)
        {
            // The embedded icon already includes its own rounded blue tile, so drawing
            // another tile behind it would shrink the supplied artwork twice.
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            // TileFlipXY stops the downscale from sampling past the edge, which otherwise
            // leaves a faint light halo around the mark.
            using (var attributes = new System.Drawing.Imaging.ImageAttributes())
            {
                attributes.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                e.Graphics.DrawImage(
                    art, tile, 0, 0, art.Width, art.Height, GraphicsUnit.Pixel, attributes);
            }
            return;
        }

        using (System.Drawing.Drawing2D.GraphicsPath path = UiShapes.RoundedRect(tile, (int)(side * 0.27)))
        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
            tile, UiStyle.BrandFrom, UiStyle.BrandTo, System.Drawing.Drawing2D.LinearGradientMode.Vertical))
            e.Graphics.FillPath(brush, path);
    }

    /// <summary>
    /// The icon the mark is drawn from, loaded once.
    ///
    /// The mark used to come from Icon.ExtractAssociatedIcon, which only ever returns the
    /// 32 px frame: the tile stretched that ~3x into a soft, blurry logo. The frames of
    /// the embedded icon are decoded at their own size instead.
    ///
    /// The fallback is the associated icon, which is soft but correct, and a null result
    /// leaves the gradient tile rather than stopping the window.
    /// </summary>
    private static Image Source()
    {
        if (sourceLoaded)
            return source;
        sourceLoaded = true;
        try
        {
            using (Stream stream = typeof(UiBrandMark).Assembly.GetManifestResourceStream(ResourceName))
                source = IconFramePolicy.DecodeLargestFrame(ReadAllBytes(stream));
            if (source != null)
                return source;
        }
        catch (Exception)
        {
            // Fall through to the associated icon.
        }
        try
        {
            using (Icon fallback = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                source = fallback == null ? null : IconFramePolicy.Detach(fallback.ToBitmap());
        }
        catch (Exception)
        {
            source = null;
        }
        return source;
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream == null)
            return null;
        var buffer = new byte[stream.Length];
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk <= 0)
                break;
            read += chunk;
        }
        return read == buffer.Length ? buffer : null;
    }
}

/// <summary>
/// Picks and decodes the frame the brand mark is drawn from out of an .ico image.
///
/// Icon frames may be stored either as a DIB or, since Vista, as a whole PNG. The PNG
/// form is the one that bites: System.Drawing.Icon answers ToBitmap() with a bitmap that
/// still belongs to the icon's HICON, so the pixels turn into noise as soon as that icon
/// is disposed, and its frame selection stops at 128 px even though the asset carries
/// 256 px. The PNG frame is therefore handed to GDI+ as a PNG instead, and every frame
/// this returns owns its pixels.
/// </summary>
public static class IconFramePolicy
{
    /// <summary>
    /// The largest frame of an .ico image, or null when the bytes are not a usable icon.
    /// </summary>
    public static Image DecodeLargestFrame(byte[] iconBytes)
    {
        if (iconBytes == null || iconBytes.Length < 6)
            return null;

        int count = BitConverter.ToUInt16(iconBytes, 4);
        int bestWidth = -1;
        int bestLength = 0;
        int bestOffset = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + (i * 16);
            if (entry + 16 > iconBytes.Length)
                break;
            // The directory stores 256 as zero: one byte cannot hold it.
            int width = iconBytes[entry] == 0 ? 256 : iconBytes[entry];
            int length = BitConverter.ToInt32(iconBytes, entry + 8);
            int offset = BitConverter.ToInt32(iconBytes, entry + 12);
            if (width <= bestWidth || length <= 0 || offset < 0 || offset + length > iconBytes.Length)
                continue;
            bestWidth = width;
            bestLength = length;
            bestOffset = offset;
        }
        if (bestWidth < 0)
            return null;

        var frame = new byte[bestLength];
        Buffer.BlockCopy(iconBytes, bestOffset, frame, 0, bestLength);
        try
        {
            if (IsPng(frame))
            {
                using (var stream = new MemoryStream(frame))
                using (var decoded = new Bitmap(stream))
                    return new Bitmap(decoded, decoded.Width, decoded.Height);
            }
            using (var stream = new MemoryStream(iconBytes))
            using (Icon icon = new Icon(stream, new Size(bestWidth, bestWidth)))
            using (Bitmap drawn = icon.ToBitmap())
                return new Bitmap(drawn, drawn.Width, drawn.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A copy that owns its pixels, so the decoder it came from can be disposed.
    /// </summary>
    public static Bitmap Detach(Bitmap bitmap)
    {
        if (bitmap == null)
            return null;
        using (bitmap)
            return new Bitmap(bitmap, bitmap.Width, bitmap.Height);
    }

    /// <summary>The eight-byte PNG signature.</summary>
    public static bool IsPng(byte[] frame)
    {
        return frame != null && frame.Length > 8 &&
            frame[0] == 0x89 && frame[1] == 0x50 && frame[2] == 0x4E && frame[3] == 0x47 &&
            frame[4] == 0x0D && frame[5] == 0x0A && frame[6] == 0x1A && frame[7] == 0x0A;
    }
}

/// <summary>
/// A value shown in a rounded tinted chip with a status dot, the pattern the design uses
/// for the state fields. A Label cannot paint a rounded fill, so the shape is drawn.
/// </summary>
public sealed class UiValueChip : Label
{
    private Color fill = UiStyle.SuccessFill;
    private Color dot = UiStyle.Success;
    public bool UseFill { get; set; }

    public UiValueChip()
    {
        AutoSize = false;
        DoubleBuffered = true;
        BackColor = UiStyle.CardBackground;
        TextAlign = ContentAlignment.MiddleLeft;
        Font = UiStyle.BodyFont();
        Padding = new Padding(34, 0, 16, 0);
        UseFill = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    /// <summary>Sets the chip tint, the dot colour, and the text colour together.</summary>
    public void SetTone(Color chipFill, Color dotColor, Color textColor)
    {
        fill = chipFill;
        dot = dotColor;
        ForeColor = textColor;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var background = new SolidBrush(Parent == null ? UiStyle.CardBackground : Parent.BackColor))
            e.Graphics.FillRectangle(background, ClientRectangle);

        // The chip hugs its content rather than filling the cell, as in the design.
        Size textSize = TextRenderer.MeasureText(Text, Font);
        int width = Math.Min(Width, textSize.Width + Padding.Left + Padding.Right);
        var bounds = new Rectangle(0, 0, Math.Max(40, width) - 1, Height - 1);
        if (UseFill)
            UiShapes.DrawRounded(e.Graphics, bounds, UiStyle.FieldRadius, fill, fill, 0);

        const int dotSize = 8;
        int dotY = (Height - dotSize) / 2;
        int dotX = UseFill ? 14 : 0;
        using (var brush = new SolidBrush(dot))
            e.Graphics.FillEllipse(brush, dotX, dotY, dotSize, dotSize);

        int textX = UseFill ? Padding.Left : 20;

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            new Rectangle(textX, 0, Math.Max(1, bounds.Width - textX - 6), Height),
            ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// The icon names the panel draws. A name rather than a font glyph: these are drawn as
/// vector strokes, so they stay crisp at any size and do not depend on an icon font being
/// installed or on a network fetch.
/// </summary>
public enum UiIcon
{
    None,
    Install,
    Uninstall,
    Start,
    Restart,
    Stop,
    Update,
    OpenPage,
    Rescan,
    OpenFolder,
    ChevronUp,
    ChevronDown,
    Export,
    Clear,
    Search,
    InstallDirectory,
    Layers,
    Package,
    Port,
    Log
}

/// <summary>
/// Draws the panel's icons as vector strokes on a 0-24 design grid.
///
/// Every icon is described with the same vocabulary and drawn with one pen, so stroke
/// weight, round caps, and corner treatment cannot drift between icons. Coordinates are
/// normalized from the 24-unit grid onto whatever rectangle the caller needs, which is how
/// the same definition serves a 16px button icon and a larger one without a second asset.
/// </summary>
public static class UiIconPainter
{
    /// <summary>
    /// The stroke width used for an icon drawn at its display size. It is deliberately not
    /// scaled down with the icon: at 16px a proportional 1.2px stroke anti-aliases into a
    /// grey smear, so the panel uses the same absolute weight the mockup does.
    /// </summary>
    private const float DisplayStroke = 1.8f;

    private const float DesignSize = 24f;

    /// <summary>Whether a name has geometry behind it.</summary>
    public static bool HasIcon(UiIcon icon)
    {
        return icon != UiIcon.None;
    }

    /// <summary>
    /// The path grammar for one icon: polylines, an ellipse, a rounded rectangle, or a
    /// filled polygon. Kept deliberately small: the icons only need these four shapes, and
    /// a tiny grammar cannot drift the way a full path parser would.
    /// </summary>
    private static string Geometry(UiIcon icon)
    {
        switch (icon)
        {
            // Arrow into a tray.
            case UiIcon.Install:
                return "P 12,3 12,15;P 7,10 12,15 17,10;P 4,20 20,20";

            // Bin with a lid and two ribs.
            case UiIcon.Uninstall:
            case UiIcon.Clear:
                return "P 4,7 20,7;P 10,11 10,17;P 14,11 14,17;"
                     + "P 6,7 7,20 17,20 18,7;P 9,7 9,4 15,4 15,7";

            // Solid play triangle.
            case UiIcon.Start:
                return "F 7,4.5 19,12 7,19.5";

            // Power symbol supplied for restart.
            case UiIcon.Restart:
                return "P 12,4.3 12,11.3;"
                     + "P 8.2,6.1 6.4,7.2 5.1,8.8 4.3,10.7 4,12.7 4.4,14.8 5.4,16.8 7,18.5 9.3,19.7 12,20 14.7,19.7 17,18.5 18.6,16.8 19.6,14.8 20,12.7 19.7,10.7 18.9,8.8 17.6,7.2 15.8,6.1";

            // A rounded square, which reads as stop without a second colour.
            case UiIcon.Stop:
                return "R 6,6 12,12 2";

            // Circled upward arrow supplied for update checking.
            case UiIcon.Update:
                return "E 12,12 10.5,10.5;P 12,18 12,8.4;P 8.4,12 12,8.4 15.6,12";

            // Box with an arrow leaving it.
            case UiIcon.OpenPage:
                return "P 14,4 20,4 20,10;P 20,4 12,12;P 18,14 18,19 5,19 5,7 10,7";

            // Framed magnifier supplied for scanning.
            case UiIcon.Rescan:
                return "P 7.5,1.6 4.3,1.6 1.6,4.3 1.6,8.6;"
                     + "P 16.4,1.6 19.7,1.6 22.4,4.3 22.4,8.6;"
                     + "P 1.6,15.4 1.6,19.7 4.3,22.4 7.5,22.4;"
                     + "P 16.4,22.4 19.7,22.4 22.4,19.7 22.4,16.4;"
                     + "E 11.3,11.7 5.5,5.5;P 15.2,15.6 18.2,18.6";

            case UiIcon.Search:
                return "E 11,11 6.5,6.5;P 16,16 20.5,20.5";

            // Three-line list supplied for the open-directory action.
            case UiIcon.OpenFolder:
                return "P 2.5,5 4.5,5;P 7.5,5 21.5,5;"
                     + "P 2.5,12 4.5,12;P 7.5,12 21.5,12;"
                     + "P 2.5,19 4.5,19;P 7.5,19 21.5,19";

            // Folder silhouette supplied for the installation-directory metric.
            case UiIcon.InstallDirectory:
                return "P 2,20 2,3.4 7.8,3.4 9.6,6.4 22,6.4 22,20 2,20;P 2,9.6 22,9.6";

            case UiIcon.Layers:
                return "P 12,3 21,8 12,13 3,8 12,3;P 3,12 12,17 21,12;P 3,16 12,21 21,16";

            case UiIcon.Package:
                return "P 12,2 20,6.5 20,17.5 12,22 4,17.5 4,6.5 12,2;P 4,6.5 12,11 20,6.5;P 12,11 12,22";

            // Three stacked server bays supplied for the header port card.
            case UiIcon.Port:
                return "F 3.75,3.25 20.25,3.25 20.25,4.75 3.75,4.75;"
                     + "F 3.75,3.25 5.25,3.25 5.25,21.25 3.75,21.25;"
                     + "F 18.75,3.25 20.25,3.25 20.25,21.25 18.75,21.25;"
                     + "F 3.75,8.75 20.25,8.75 20.25,10.25 3.75,10.25;"
                     + "F 3.75,14.25 20.25,14.25 20.25,15.75 3.75,15.75;"
                     + "F 3.75,19.75 20.25,19.75 20.25,21.25 3.75,21.25;"
                     + "F 13.5,6 16.5,6 16.5,7.5 13.5,7.5;"
                     + "F 13.5,11.5 16.5,11.5 16.5,13 13.5,13;"
                     + "F 13.5,17 16.5,17 16.5,18.5 13.5,18.5";

            // Monitor with a chart line and a base bar, supplied for the log heading.
            case UiIcon.Log:
                return "R 1,1 22,17.6 2.6;"
                     + "P 5.5,11.2 9.8,7 13.5,10.8 17.4,6.7;"
                     + "P 5.4,22.2 18.6,22.2";

            case UiIcon.ChevronUp:
                return "P 6,15 12,9 18,15";

            case UiIcon.ChevronDown:
                return "P 6,9 12,15 18,9";

            // Wide tray with an upward arrow leaving it, supplied for the export action.
            case UiIcon.Export:
                return "P 1.8,7.6 7.6,7.6;P 15.8,7.6 21.9,7.6;"
                     + "P 1.8,7.6 1.8,22.6 21.9,22.6 21.9,7.6;"
                     + "P 11.9,16.6 11.9,2.6;P 7.4,5.4 11.9,1.9 16.4,5.4";

            default:
                return "";
        }
    }

    /// <summary>
    /// Draws an icon centred in the given rectangle, scaled to fit. A filled shape is
    /// filled; everything else is stroked with the shared pen settings.
    /// </summary>
    public static void Draw(Graphics graphics, UiIcon icon, Rectangle bounds, Color color)
    {
        if (!HasIcon(icon) || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        string geometry = Geometry(icon);
        if (String.IsNullOrEmpty(geometry))
            return;

        // Scale the 24-unit grid onto the target box, keeping the icon square so a wide
        // button does not stretch the artwork.
        int side = Math.Min(bounds.Width, bounds.Height);
        float scale = side / DesignSize;
        float offsetX = bounds.X + ((bounds.Width - side) / 2f);
        float offsetY = bounds.Y + ((bounds.Height - side) / 2f);

        System.Drawing.Drawing2D.SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        try
        {
            // Absolute weight up to the design size, proportional only when enlarged, so a
            // larger rendering does not end up with a hairline stroke.
            float stroke = scale >= 1f ? DisplayStroke * scale : DisplayStroke;
            using (var pen = new Pen(color, stroke))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;

                foreach (string segment in geometry.Split(';'))
                {
                    string piece = segment.Trim();
                    if (piece.Length == 0)
                        continue;
                    float[] numbers = ParseNumbers(piece);

                    if (piece.StartsWith("A", StringComparison.Ordinal))
                    {
                        // Arc: centre x, centre y, radius x, radius y. Drawn as a full
                        // ellipse and then masked away by the caller's next stroke, which is
                        // why the restart icon lists its gap as a separate polyline.
                        if (numbers.Length >= 4)
                        {
                            var box = ScaleRect(numbers[0] - numbers[2], numbers[1] - numbers[3],
                                numbers[2] * 2, numbers[3] * 2, scale, offsetX, offsetY);
                            graphics.DrawEllipse(pen, box);
                        }
                        continue;
                    }

                    if (piece.StartsWith("E", StringComparison.Ordinal))
                    {
                        // Ellipse: centre x, centre y, radius x, radius y.
                        if (numbers.Length >= 4)
                        {
                            var box = ScaleRect(numbers[0] - numbers[2], numbers[1] - numbers[3],
                                numbers[2] * 2, numbers[3] * 2, scale, offsetX, offsetY);
                            graphics.DrawEllipse(pen, box);
                        }
                        continue;
                    }

                    if (piece.StartsWith("R", StringComparison.Ordinal))
                    {
                        // Rounded rectangle: x, y, width, height, corner radius.
                        if (numbers.Length >= 5)
                        {
                            var box = ScaleRect(numbers[0], numbers[1], numbers[2], numbers[3], scale, offsetX, offsetY);
                            int radius = Math.Max(1, (int)Math.Round(numbers[4] * scale));
                            using (System.Drawing.Drawing2D.GraphicsPath path = UiShapes.RoundedRect(box, radius))
                                graphics.DrawPath(pen, path);
                        }
                        continue;
                    }

                    if (piece.StartsWith("F", StringComparison.Ordinal))
                    {
                        // Filled polygon: x,y pairs closed automatically.
                        var points = ParsePoints(numbers, scale, offsetX, offsetY);
                        if (points.Length >= 3)
                        {
                            using (var brush = new SolidBrush(color))
                                graphics.FillPolygon(brush, points);
                        }
                        continue;
                    }

                    // Default: polyline through the points.
                    var line = ParsePoints(numbers, scale, offsetX, offsetY);
                    if (line.Length >= 2)
                        graphics.DrawLines(pen, line);
                }
            }
        }
        finally
        {
            graphics.SmoothingMode = previous;
        }
    }

    private static Rectangle ScaleRect(float x, float y, float width, float height, float scale, float offsetX, float offsetY)
    {
        return new Rectangle(
            (int)Math.Round(offsetX + (x * scale)),
            (int)Math.Round(offsetY + (y * scale)),
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static PointF[] ParsePoints(float[] numbers, float scale, float offsetX, float offsetY)
    {
        int count = numbers.Length / 2;
        var points = new PointF[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = new PointF(
                offsetX + (numbers[i * 2] * scale),
                offsetY + (numbers[(i * 2) + 1] * scale));
        }
        return points;
    }

    /// <summary>
    /// Pulls the numbers out of one segment. Written by hand rather than with a regular
    /// expression so a malformed icon degrades to fewer points instead of throwing.
    /// </summary>
    private static float[] ParseNumbers(string piece)
    {
        var found = new List<float>();
        int index = 0;
        while (index < piece.Length)
        {
            if (piece[index] == '-' || piece[index] == '.' || Char.IsDigit(piece[index]))
            {
                int start = index;
                index++;
                while (index < piece.Length &&
                       (piece[index] == '.' || Char.IsDigit(piece[index])))
                    index++;
                float value;
                if (Single.TryParse(piece.Substring(start, index - start),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out value))
                    found.Add(value);
            }
            else
            {
                index++;
            }
        }
        return found.ToArray();
    }
}

/// <summary>
/// A flat button in the two styles the design uses: a solid primary action and a
/// bordered secondary one. Standard WinForms buttons cannot be restyled to this shape.
/// </summary>
public sealed class UiFlatButton : Button
{
    public bool IsPrimary { get; set; }
    public bool IsDanger { get; set; }

    /// <summary>The drawn icon, or None for a text-only button.</summary>
    public UiIcon Icon { get; set; }

    private bool hovered;
    private bool pressed;

    public UiFlatButton()
    {
        Icon = UiIcon.None;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        Font = UiStyle.ButtonFont();
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovered = false;
        pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        pressed = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        Color fill;
        Color text;
        Color border;

        if (!Enabled)
        {
            // The design shows a disabled action as a pale filled button with grey text.
            fill = UiStyle.DisabledFill;
            text = UiStyle.DisabledText;
            border = UiStyle.DisabledFill;
        }
        else if (IsPrimary)
        {
            fill = pressed ? UiStyle.PrimaryPressed : (hovered ? UiStyle.PrimaryHover : UiStyle.Primary);
            text = Color.White;
            border = fill;
        }
        else if (IsDanger)
        {
            fill = pressed ? UiStyle.DangerPressed : (hovered ? UiStyle.DangerFill : Color.White);
            text = UiStyle.DangerText;
            border = UiStyle.DangerBorder;
        }
        else
        {
            fill = pressed ? UiStyle.SecondaryPressed : (hovered ? UiStyle.SecondaryHover : Color.White);
            text = UiStyle.TextPrimary;
            border = UiStyle.SecondaryBorder;
        }

        // Let WinForms paint the real parent surface into the rounded corners. Filling with
        // Parent.BackColor fails when the layout panel itself is transparent and leaves the
        // button's default white buffer visible as a rectangular fringe.
        base.OnPaintBackground(e);

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        if (Enabled && IsPrimary)
        {
            var target = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            using (System.Drawing.Drawing2D.GraphicsPath path = UiShapes.RoundedRect(target, UiStyle.ButtonRadius))
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                target,
                UiStyle.PrimaryGradientFrom,
                UiStyle.PrimaryGradientTo,
                System.Drawing.Drawing2D.LinearGradientMode.Horizontal))
            using (var pen = new Pen(UiStyle.PrimaryGradientTo, 1))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
        }
        else
        {
            UiShapes.DrawRounded(e.Graphics, bounds, UiStyle.ButtonRadius, fill, border, 1);
        }

        // Icon and label are laid out as one centred group, as in the design.
        bool hasIcon = UiIconPainter.HasIcon(Icon);
        Size textSize = TextRenderer.MeasureText(Text, Font);
        int gap = hasIcon ? UiStyle.IconGap : 0;
        int iconWidth = hasIcon ? UiStyle.IconSize : 0;
        int totalWidth = iconWidth + gap + textSize.Width;
        int x = Math.Max(8, (Width - totalWidth) / 2);

        if (hasIcon)
        {
            UiIconPainter.Draw(
                e.Graphics,
                Icon,
                new Rectangle(x, (Height - iconWidth) / 2, iconWidth, iconWidth),
                text);
            x += iconWidth + gap;
        }

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            new Rectangle(x, 0, Math.Max(1, Width - x - 6), Height),
            text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// A status chip: a coloured dot plus text, optionally on a tinted pill. The design
/// uses this for the installed and running states.
/// </summary>
public sealed class UiStatusChip : Control
{
    private string chipText = "";
    private Color dotColor = UiStyle.Success;
    private Color fill = Color.Transparent;
    private Color textColor = UiStyle.TextPrimary;

    public UiStatusChip()
    {
        DoubleBuffered = true;
        Font = UiStyle.BodyFont();
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    /// <summary>Sets the label, the dot colour, and the pill tint together.</summary>
    public void SetStatus(string text, Color dot, Color background, Color foreground)
    {
        chipText = text ?? "";
        dotColor = dot;
        fill = background;
        textColor = foreground;
        Invalidate();
    }

    public string ChipText { get { return chipText; } }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var background = new SolidBrush(Parent == null ? UiStyle.CardBackground : Parent.BackColor))
            e.Graphics.FillRectangle(background, ClientRectangle);

        Size textSize = TextRenderer.MeasureText(chipText, Font);
        const int dotSize = 8;
        const int gap = 8;
        const int padX = 10;
        int contentWidth = dotSize + gap + textSize.Width;
        int width = fill == Color.Transparent ? contentWidth : contentWidth + (padX * 2);
        if (width > Width) width = Width;
        int height = Math.Min(Height, 26);
        var pill = new Rectangle(0, Math.Max(0, (Height - height) / 2), width, height);

        if (fill != Color.Transparent)
            UiShapes.DrawRounded(e.Graphics, pill, 13, fill, fill, 0);

        int dotX = pill.X + (fill == Color.Transparent ? 0 : padX);
        int dotY = pill.Y + (pill.Height - dotSize) / 2;
        using (var dot = new SolidBrush(dotColor))
            e.Graphics.FillEllipse(dot, dotX, dotY, dotSize, dotSize);

        TextRenderer.DrawText(
            e.Graphics,
            chipText,
            Font,
            new Rectangle(dotX + dotSize + gap, pill.Y, Math.Max(1, pill.Right - dotX - dotSize - gap - 4), pill.Height),
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// The control panel palette and metrics, taken from the approved design. Kept in one
/// class so a colour or a size is never guessed at a call site.
/// </summary>
public static class UiStyle
{
    // Three levels of surface, which is what keeps a card-based screen from reading as one
    // flat white sheet: the page sits behind, cards sit on it, and fields sit inside them.
    public static readonly Color WindowBackground = Color.FromArgb(0xF2, 0xF7, 0xFF);
    public static readonly Color CardBackground = Color.White;
    public static readonly Color CardBorder = Color.FromArgb(0xE6, 0xEE, 0xFA);
    public static readonly Color FieldBackground = Color.FromArgb(0xF8, 0xFB, 0xFF);
    public static readonly Color FieldBorder = Color.FromArgb(0xD8, 0xE2, 0xF1);
    public static readonly Color LogBackground = Color.FromArgb(0xF7, 0xFA, 0xFF);
    public static readonly Color Divider = Color.FromArgb(0xF0, 0xF1, 0xF4);
    public static readonly Color IconCircleBackground = Color.FromArgb(0xF0, 0xF5, 0xFC);
    public static readonly Color InkBlue = Color.FromArgb(0x08, 0x1B, 0x5A);

    /// <summary>The soft shadow under a card. Very light: it should suggest depth, not announce itself.</summary>
    public static readonly Color CardShadow = Color.FromArgb(0x5A, 0x1B, 0x2A, 0x40);

    // Text, in three weights of emphasis.
    public static readonly Color TextPrimary = Color.FromArgb(0x08, 0x18, 0x4D);
    public static readonly Color TextSecondary = Color.FromArgb(0x72, 0x80, 0xA0);
    public static readonly Color TextMuted = Color.FromArgb(0x9B, 0xA1, 0xAA);

    // Brand
    public static readonly Color BrandFrom = Color.FromArgb(0x4C, 0x8D, 0xF0);
    public static readonly Color BrandTo = Color.FromArgb(0x2B, 0x63, 0xD9);

    // Primary action
    public static readonly Color Primary = Color.FromArgb(0x3B, 0x7D, 0xE0);
    public static readonly Color PrimaryGradientFrom = Color.FromArgb(0x3D, 0x82, 0xFF);
    public static readonly Color PrimaryGradientTo = Color.FromArgb(0x14, 0x5F, 0xF3);
    public static readonly Color PrimaryHover = Color.FromArgb(0x33, 0x71, 0xD2);
    public static readonly Color PrimaryPressed = Color.FromArgb(0x2F, 0x6F, 0xCE);

    // Secondary action
    public static readonly Color SecondaryHover = Color.FromArgb(0xF7, 0xF8, 0xFA);
    public static readonly Color SecondaryPressed = Color.FromArgb(0xEF, 0xF1, 0xF4);
    public static readonly Color SecondaryBorder = Color.FromArgb(0xDD, 0xE1, 0xE6);
    public static readonly Color DisabledFill = Color.FromArgb(0xF4, 0xF5, 0xF7);
    public static readonly Color DisabledText = Color.FromArgb(0xB6, 0xBC, 0xC4);

    // Status
    public static readonly Color Success = Color.FromArgb(0x2F, 0xBF, 0x54);
    public static readonly Color SuccessFill = Color.FromArgb(0xE6, 0xF8, 0xEB);
    public static readonly Color SuccessText = Color.FromArgb(0x18, 0x7C, 0x39);
    public static readonly Color Warning = Color.FromArgb(0xE8, 0x94, 0x0A);
    public static readonly Color WarningFill = Color.FromArgb(0xFD, 0xF3, 0xD8);
    public static readonly Color WarningText = Color.FromArgb(0x9A, 0x4A, 0x08);
    public static readonly Color Danger = Color.FromArgb(0xE5, 0x3E, 0x3E);
    public static readonly Color DangerFill = Color.FromArgb(0xFD, 0xE7, 0xE7);
    public static readonly Color DangerText = Color.FromArgb(0xA8, 0x1C, 0x1C);
    public static readonly Color DangerBorder = Color.FromArgb(0xF0, 0xB8, 0xB8);
    public static readonly Color DangerPressed = Color.FromArgb(0xFA, 0xD6, 0xD6);
    public static readonly Color Neutral = Color.FromArgb(0x9B, 0xA1, 0xAA);
    public static readonly Color NeutralFill = Color.FromArgb(0xF2, 0xF3, 0xF5);

    // Metrics. The gaps are tighter than a first pass would suggest: at 22/24 the three
    // cards read as three separate slabs rather than one screen.
    public const int OuterMargin = 22;
    public const int CardGap = 22;
    public const int CardPadding = 14;
    public const int CardRadius = 18;
    public const int ButtonRadius = 12;
    public const int ButtonHeight = 58;
    public const int FieldHeight = 42;
    public const int FieldRadius = 9;
    public const int ToolButtonHeight = 52;
    public const int IconSize = 20;

    /// <summary>Space between the icon and its label inside a button.</summary>
    public const int IconGap = 7;

    /// <summary>Horizontal padding inside an action button, split either side.</summary>
    public const int ButtonPadding = 24;

    /// <summary>How far a card's shadow extends past its bounds, per side.</summary>
    public const int ShadowSpread = 6;

    /// <summary>The tallest the log region grows before it scrolls instead.</summary>
    public const int LogMaxHeight = 320;

    /// <summary>The shortest the log region is allowed to become.</summary>
    public const int LogMinHeight = 150;

    // Font sizes are converted from the design's CSS pixels to GDI+ points. The two units
    // are not interchangeable: 1px is 0.75pt at 96 DPI, so writing a pixel size straight
    // into a Font renders it a third too large and overflows every box.
    public static Font TitleFont()
    {
        return new Font("Microsoft YaHei UI", 22.5F, FontStyle.Bold);
    }

    public static Font SubtitleFont()
    {
        return new Font("Microsoft YaHei UI", 13.5F, FontStyle.Regular);
    }

    public static Font CardTitleFont()
    {
        return new Font("Microsoft YaHei UI", 16.5F, FontStyle.Bold);
    }

    public static Font BodyFont()
    {
        return new Font("Microsoft YaHei UI", 12F, FontStyle.Regular);
    }

    public static Font ButtonFont()
    {
        return new Font("Microsoft YaHei UI", 11.25F, FontStyle.Regular);
    }

    public static Font LabelFont()
    {
        return new Font("Microsoft YaHei UI", 11.25F, FontStyle.Regular);
    }

    /// <summary>
    /// The log area's font. Monospaced as the design specifies, so the timestamp column
    /// lines up without relying on tabs.
    /// </summary>
    public static Font LogFont()
    {
        return new Font("Consolas", 11.25F, FontStyle.Regular);
    }
}

/// <summary>
/// The handful of Win32 calls the panel needs: enumerating top-level windows to find
/// its own already-running instance, and rendering a window for diagnostics.
/// </summary>
public static class NativeMethods
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

}

/// <summary>
/// Single-instance rules. Two panels would otherwise drive the same port and
/// overwrite each other's .dsh-manager-state.json, so the second launch must hand
/// control back to the first instead of starting a rival.
/// </summary>
public static class SingleInstancePolicy
{
    /// <summary>
    /// The class name WinForms assigns every window in this process, which is how a
    /// second launch recognises the first one's window without any IPC channel.
    /// </summary>
    public static bool IsPanelWindowClass(string className)
    {
        return !String.IsNullOrEmpty(className) &&
            className.StartsWith("WindowsForms10.Window.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a discovered window is the panel's main window: right process, right
    /// class, right title, and big enough to be the real window rather than a hidden
    /// helper such as the .NET broadcast window.
    /// </summary>
    public static bool IsMainPanelWindow(string className, string title, int width, int height)
    {
        if (!IsPanelWindowClass(className))
            return false;
        if (String.IsNullOrEmpty(title) || !title.StartsWith("DeepSeek Harness 控制面板", StringComparison.Ordinal))
            return false;
        return width > 400 && height > 300;
    }

    /// <summary>
    /// Shown when a second launch finds the first one already running. Takes the port
    /// because the message names the shared resource at stake.
    /// </summary>
    public static string BuildAlreadyRunningMessage(int port)
    {
        return "DeepSeek Harness 控制面板已经在运行。\r\n\r\n" +
            "同一个面板不能重复启动：两个实例会同时管理 " + port + " 端口和安装状态，导致状态彼此覆盖。\r\n" +
            "已为你切换到正在运行的窗口。";
    }
}

/// <summary>
/// The panel's own version, and the conversions an installer needs.
///
/// Kept in one place because the executable's version resource, the installer's
/// version, and the "Apps and features" entry must agree; a mismatch there is how a
/// package ends up unable to upgrade itself.
/// </summary>
public static class PanelVersionPolicy
{
    /// <summary>
    /// The panel's version. Bump this when releasing; everything else derives from it.
    /// </summary>
    public const string Version = "0.1.2";

    /// <summary>
    /// A four-part numeric version for the Win32 version resource and the installer.
    /// A suffix such as -rc.1 is dropped: those fields are numeric only, and a
    /// non-numeric value makes the resource invalid rather than merely ugly.
    /// </summary>
    public static string ToNumericVersion(string version)
    {
        if (String.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("版本号为空。");

        // Drop any pre-release or build metadata.
        string core = version.Trim();
        int suffix = core.IndexOfAny(new[] { '-', '+' });
        if (suffix >= 0)
            core = core.Substring(0, suffix);

        string[] parts = core.Split('.');
        var numeric = new List<int>();
        foreach (string part in parts)
        {
            int value;
            if (!Int32.TryParse(part, out value) || value < 0)
                throw new InvalidOperationException("版本号包含非数字部分：" + version);
            numeric.Add(value);
        }
        if (numeric.Count == 0)
            throw new InvalidOperationException("版本号为空。");

        // The resource wants exactly four fields.
        while (numeric.Count < 4)
            numeric.Add(0);
        if (numeric.Count > 4)
            numeric.RemoveRange(4, numeric.Count - 4);

        return String.Join(".", numeric.ConvertAll(value => value.ToString()).ToArray());
    }

    /// <summary>
    /// The file name an installer package should use. Timestamp-free so a release
    /// replaces its predecessor rather than piling up.
    /// </summary>
    public static string BuildInstallerFileName(string version)
    {
        if (String.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("版本号为空。");
        return "DeepSeekHarnessControlPanel-" + version.Trim() + "-setup.exe";
    }
}

public enum TrayCloseAction
{
    /// <summary>Keep running in the tray; the service should stay available.</summary>
    MinimizeToTray,
    /// <summary>Actually exit the panel.</summary>
    Exit
}

/// <summary>
/// What closing the window means once the panel owns a tray icon.
///
/// The default is to keep running: a management panel that exits on close makes the
/// tray icon pointless, and the service it supervises stays up either way. Exiting
/// remains available from the tray menu and from the close prompt.
/// </summary>
public static class TrayClosePolicy
{
    /// <summary>
    /// Whether the close prompt should be shown. Asking every single time is noise, so
    /// the user's answer is remembered and the prompt is skipped afterwards.
    /// </summary>
    public static bool ShouldAskOnClose(bool alreadyAnswered)
    {
        return !alreadyAnswered;
    }

    /// <summary>
    /// The close prompt's title and the two answers, which are also its button labels.
    ///
    /// A MessageBox cannot label its buttons, and that was this prompt's defect: the text
    /// offered "最小化到托盘" and "退出" while the buttons read 是 and 否, so the two
    /// choices on screen were not the two the sentence named, and 是 meant the drastic
    /// one. The answers are exported so the dialog and the wording cannot drift apart.
    /// </summary>
    public const string ClosePromptTitle = "关闭控制面板";
    public const string ClosePromptTrayAnswer = "最小化到托盘";
    public const string ClosePromptExitAnswer = "退出面板";

    public static string BuildClosePrompt(int port)
    {
        return "关闭窗口不会停止 Harness 服务，它仍在 " + port + " 端口监听。" + Environment.NewLine + Environment.NewLine +
            "面板本身要怎么处理？此选择会被记住，以后关闭不再询问。" + Environment.NewLine +
            "· " + ClosePromptTrayAnswer + "：面板继续运行，可从托盘图标打开。" + Environment.NewLine +
            "· " + ClosePromptExitAnswer + "：完全关闭面板，Harness 不受影响。";
    }

    /// <summary>The remembered answer, as it is stored in the settings file.</summary>
    public const string TraySettingValue = "tray";
    public const string ExitSettingValue = "exit";

    public static string ToSettingValue(TrayCloseAction action)
    {
        return action == TrayCloseAction.Exit ? ExitSettingValue : TraySettingValue;
    }

    /// <summary>
    /// Reads the remembered answer. A missing, older, or hand-edited value means the
    /// question has not been answered yet, which is the safe reading: the panel asks
    /// rather than guessing on the user's behalf.
    /// </summary>
    public static bool TryParseSettingValue(string stored, out TrayCloseAction action)
    {
        action = TrayCloseAction.MinimizeToTray;
        if (String.IsNullOrEmpty(stored))
            return false;
        string candidate = stored.Trim();
        if (String.Equals(candidate, ExitSettingValue, StringComparison.OrdinalIgnoreCase))
        {
            action = TrayCloseAction.Exit;
            return true;
        }
        if (String.Equals(candidate, TraySettingValue, StringComparison.OrdinalIgnoreCase))
        {
            action = TrayCloseAction.MinimizeToTray;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The tray menu labels. Kept together so the menu and its tests agree.
    /// </summary>
    public const string MenuShow = "打开控制面板";
    public const string MenuOpenPage = "打开 Harness 页面";
    public const string MenuStart = "启动 Harness";
    public const string MenuStop = "停止 Harness";
    public const string MenuExit = "退出";

    /// <summary>
    /// The hover tooltip. Carries the live state so the tray is informative without
    /// opening the window.
    /// </summary>
    public static string BuildTooltip(bool running, int port, string version)
    {
        string state = running ? "正在运行" : "未运行";
        string suffix = String.IsNullOrWhiteSpace(version) ? "" : "  " + version;
        string text = "DeepSeek Harness 控制面板 — " + state + "（端口 " + port + "）" + suffix;
        // NotifyIcon truncates beyond 63 characters, so keep it inside that budget.
        return text.Length <= 63 ? text : text.Substring(0, 60) + "...";
    }
}

/// <summary>
/// How the installed Harness version is shown. The panel displays a bare version number
/// with a leading v, while the value it reads from package.json has no prefix.
/// </summary>
public static class HarnessVersionText
{
    /// <summary>
    /// The display form, or an empty string when the version is unknown. An unknown
    /// version is left blank rather than shown as "v未知", which would look like a
    /// version number that does not exist.
    /// </summary>
    public static string ForDisplay(string version)
    {
        if (String.IsNullOrWhiteSpace(version))
            return "";
        string trimmed = version.Trim();
        // Never double the prefix if a future source already carries one.
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return "v" + trimmed;
    }
}

/// <summary>
/// The port Harness listens on. It used to be the literal 3080 in sixteen places,
/// which meant changing it required editing every message, probe, and URL as well.
/// </summary>
public static class HarnessPortPolicy
{
    /// <summary>The port the panel uses when nothing else is configured.</summary>
    public const int DefaultPort = 3080;

    /// <summary>Ports below this are privileged and would need elevation.</summary>
    public const int MinimumPort = 1024;
    public const int MaximumPort = 65535;

    /// <summary>
    /// Whether a port can be used. Ports under 1024 are rejected on purpose: binding
    /// one requires an elevated process, and the panel deliberately installs per-user
    /// without ever asking for administrator rights.
    /// </summary>
    public static bool IsValid(int port)
    {
        return port >= MinimumPort && port <= MaximumPort;
    }

    /// <summary>
    /// Parses a user-typed port. Returns false rather than throwing so a dialog can
    /// report the problem in its own words.
    /// </summary>
    public static bool TryParse(string text, out int port, out string error)
    {
        port = DefaultPort;
        error = "";
        string candidate = (text ?? "").Trim();
        if (candidate.Length == 0)
        {
            error = "端口不能为空。";
            return false;
        }

        int parsed;
        if (!Int32.TryParse(candidate, out parsed))
        {
            error = "端口必须是数字。";
            return false;
        }
        if (parsed < MinimumPort || parsed > MaximumPort)
        {
            error = "端口需要在 " + MinimumPort + " 到 " + MaximumPort + " 之间。" +
                Environment.NewLine +
                "小于 " + MinimumPort + " 的端口需要管理员权限，控制面板不会为此请求提权。";
            return false;
        }
        port = parsed;
        return true;
    }

    /// <summary>
    /// The local web address for a port, used for the readiness probe and as the
    /// fallback address when the official ready line has not arrived.
    /// </summary>
    public static string BuildLocalWebUri(int port)
    {
        if (!IsValid(port))
            throw new ArgumentOutOfRangeException("port");
        return "http://127.0.0.1:" + port + "/";
    }

    /// <summary>
    /// The extra argument that makes Harness listen on a chosen port. Passed through
    /// to the web launch alongside the existing --no-open.
    /// </summary>
    public static string BuildPortArgument(int port)
    {
        if (!IsValid(port))
            throw new ArgumentOutOfRangeException("port");
        return "--port " + port;
    }
}

/// <summary>
/// Decides what the panel should say about update availability. Kept separate from
/// the network and UI so the wording rules are testable.
/// </summary>
public static class HarnessUpdatePolicy
{
    /// <summary>
    /// A commit is a 40-character hex digest. Anything else (empty, truncated, or a
    /// partially written state file) must not be compared as if it were a version.
    /// </summary>
    public static bool IsCommitId(string value)
    {
        return Regex.IsMatch(value ?? "", "^[0-9a-fA-F]{40}$");
    }

    /// <summary>
    /// Whether the two commits differ. An unknown local commit cannot be compared,
    /// so it never counts as "an update is available" — claiming an update on a
    /// value we could not read would be a false alarm.
    /// </summary>
    public static bool HasUpdate(string localCommit, string remoteCommit)
    {
        if (!IsCommitId(localCommit) || !IsCommitId(remoteCommit))
            return false;
        return !String.Equals(localCommit, remoteCommit, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Short form of a commit for display, or an explicit unknown marker.</summary>
    public static string ShortCommit(string commit)
    {
        if (!IsCommitId(commit))
            return "未知";
        return commit.Substring(0, 7);
    }

    /// <summary>
    /// The line the panel shows in the status area after an automatic check.
    ///
    /// Only an available update is worth a status line. Being up to date is the normal
    /// state and the installed version is already on the version row, so repeating
    /// "already up to date" is noise that pushes the actual status text aside.
    /// </summary>
    public static string DescribeAvailability(string localCommit, string remoteCommit)
    {
        if (!IsCommitId(remoteCommit))
            return "";
        if (!IsCommitId(localCommit))
            return "";
        if (HasUpdate(localCommit, remoteCommit))
            return "发现新版本（本机 " + ShortCommit(localCommit) + " → 上游 " + ShortCommit(remoteCommit) + "）";
        return "";
    }

    /// <summary>
    /// The line written to the log after an automatic check. Unlike the status area this
    /// always says what was found, including "up to date", because the log is the record
    /// of what the panel actually checked.
    /// </summary>
    public static string DescribeAvailabilityForLog(string localCommit, string remoteCommit)
    {
        if (!IsCommitId(remoteCommit))
            return "";
        if (!IsCommitId(localCommit))
            return "上游最新提交 " + ShortCommit(remoteCommit) + "（本机版本未知，无法比较）";
        if (HasUpdate(localCommit, remoteCommit))
            return "发现新版本，可更新（本机 " + ShortCommit(localCommit) + " → 上游 " + ShortCommit(remoteCommit) + "）";
        return "已是最新版本（" + ShortCommit(localCommit) + "）";
    }
}

/// <summary>
/// Exporting log text. Kept free of WinForms so the export layout is testable.
/// </summary>
public static class LogExportPolicy
{
    /// <summary>
    /// A default export file name. Timestamped so repeated exports do not silently
    /// overwrite each other, which matters when collecting evidence across attempts.
    /// </summary>
    public static string BuildDefaultFileName(DateTime timestamp)
    {
        return "dsh-control-panel-" + timestamp.ToString("yyyyMMdd-HHmmss") + ".log";
    }

    /// <summary>
    /// The text written by an export. Records when it was taken and from where, so a
    /// log pasted into a report carries its own context.
    /// </summary>
    public static string BuildExportText(string logText, string installRoot, DateTime timestamp)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# DeepSeek Harness 控制面板日志");
        builder.AppendLine("# 导出时间: " + timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine("# 安装目录: " + (String.IsNullOrEmpty(installRoot) ? "(未设置)" : installRoot));
        builder.AppendLine();
        builder.Append(logText ?? "");
        // A trailing newline keeps the last entry from merging with whatever follows.
        if (!String.IsNullOrEmpty(logText) && !logText.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            builder.AppendLine();
        return builder.ToString();
    }
}

/// <summary>
/// Cancellation rules for long operations. A cancelled run must be reported as a
/// cancellation rather than a failure, and the child process tree must actually be
/// stopped instead of being left orphaned.
/// </summary>
public static class OperationCancellationPolicy
{
    /// <summary>
    /// What the install button shows while an operation runs. The same button becomes
    /// the escape hatch, so no separate control is needed.
    /// </summary>
    public const string CancelButtonText = "取消";

    public const string CancelledLogLine = "操作已取消。";

    /// <summary>
    /// Whether an operation that finished should be reported as cancelled rather than
    /// failed.
    ///
    /// Both inputs are needed. A cancellation request can be observed before the
    /// action notices it, and a faulted action can still be a cancellation when the
    /// failing step is the one that observed the token; reporting those as errors
    /// would show a scary dialog for a deliberate user action.
    /// </summary>
    public static bool IsCancellation(bool cancellationRequested, bool taskCanceled)
    {
        return cancellationRequested || taskCanceled;
    }

    /// <summary>Whether the operation outcome should be shown as an error dialog.</summary>
    public static bool ShouldReportAsFailure(bool cancellationRequested, bool taskFaulted)
    {
        return taskFaulted && !cancellationRequested;
    }

    /// <summary>
    /// Whether a failed kill should be reported as an error. A process that already
    /// exited between the check and the kill raises InvalidOperationException, which
    /// is the ordinary race and not worth alarming the user about.
    /// </summary>
    public static bool IsExpectedKillRace(Exception error)
    {
        return error is InvalidOperationException;
    }

    /// <summary>
    /// Quotes a pid for taskkill. The pid is numeric, so this exists to keep the
    /// argument shape in one place and to reject anything that is not a pid.
    /// </summary>
    public static string BuildTaskkillArguments(int processId)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException("processId");
        return "/PID " + processId + " /T /F";
    }
}

/// <summary>
/// A point-in-time view of the panel's state. One snapshot is computed per refresh
/// and shared by the labels, the buttons, and the change detector, so a refresh no
/// longer recomputes the same port and process facts three separate times.
/// </summary>
public sealed class HarnessStatusSnapshot
{
    public bool Installed { get; private set; }
    public bool Ready { get; private set; }
    public bool PortBusy { get; private set; }
    public bool Running { get; private set; }
    public bool MultipleInstalls { get; private set; }
    public string Version { get; private set; }

    public HarnessStatusSnapshot(
        bool installed,
        bool ready,
        bool portBusy,
        bool running,
        bool multipleInstalls,
        string version)
    {
        Installed = installed;
        Ready = ready;
        PortBusy = portBusy;
        Running = running;
        MultipleInstalls = multipleInstalls;
        Version = version ?? "";
    }

    /// <summary>"已安装" / "未安装" label.</summary>
    public string StatusText
    {
        get
        {
            if (MultipleInstalls)
                return "发现多个安装";
            if (Installed && !Ready)
                return "安装不完整（需要修复）";
            return Installed ? "已安装" : "未安装";
        }
    }

    /// <summary>"正在运行" / "未运行" / "端口被其他程序占用" label.</summary>
    public string RunningText
    {
        get
        {
            if (Running)
                return "正在运行";
            return PortBusy ? "端口被其他程序占用" : "未运行";
        }
    }

    /// <summary>
    /// The fields that define a meaningful change. Version is derived from the same
    /// inputs, so a separate check would double-report a single transition.
    /// </summary>
    public bool DiffersFrom(HarnessStatusSnapshot other)
    {
        if (other == null)
            return true;
        return Installed != other.Installed
            || Ready != other.Ready
            || PortBusy != other.PortBusy
            || Running != other.Running
            || MultipleInstalls != other.MultipleInstalls
            || !String.Equals(Version, other.Version, StringComparison.Ordinal);
    }
}

/// <summary>
/// Turns two snapshots into the single line the log shows when something actually
/// changed. The panel polls every few seconds, so describing an unchanged state
/// would flood the log and bury the events that matter.
/// </summary>
public static class HarnessStatusChangePolicy
{
    /// <summary>
    /// A description of the transition, or an empty string when nothing changed.
    /// The wording names both sides so the log reads as an event, not a state dump.
    /// </summary>
    public static string DescribeChange(int port, HarnessStatusSnapshot previous, HarnessStatusSnapshot current)
    {
        if (previous == null || current == null)
            return "";

        // Process lifecycle is the event the user most needs to notice.
        if (previous.Running && !current.Running)
        {
            if (current.PortBusy)
                return "Harness 已停止，但 " + port + " 端口仍被其他程序占用。";
            return "Harness 进程已退出，服务不再监听 " + port + "。";
        }
        if (!previous.Running && current.Running)
            return "检测到 Harness 已开始运行。";

        // The port changing hands while Harness is not the listener.
        if (!previous.PortBusy && current.PortBusy && !current.Running)
            return port + " 端口被其他程序占用。";
        if (previous.PortBusy && !current.PortBusy && !current.Running)
            return port + " 端口已被释放。";

        if (!previous.Installed && current.Installed)
            return "检测到 Harness 安装。";
        if (previous.Installed && !current.Installed)
            return "Harness 安装目录已不可用（可能被移动或删除）。";

        if (previous.Installed && previous.Ready && !current.Ready)
            return "Harness 安装不再完整，需要修复。";
        if (previous.Installed && !previous.Ready && current.Ready)
            return "Harness 安装已恢复完整。";

        if (!previous.MultipleInstalls && current.MultipleInstalls)
            return "发现多个 Harness 安装，操作已暂停。";
        if (previous.MultipleInstalls && !current.MultipleInstalls)
            return "安装数量已恢复为单个。";

        // Anything else is not worth a log line.
        return "";
    }
}

public static class HarnessLifecyclePolicy
{
    public const int StartupTimeoutSeconds = 120;
    public const int StopTimeoutMilliseconds = 30000;
    public const int PollIntervalMilliseconds = 250;
    public const int EndpointProbeIntervalMilliseconds = 1000;
    public const int EndpointProbeTimeoutMilliseconds = 3000;

    /// <summary>
    /// How often the panel re-checks whether Harness is still running. Three seconds
    /// is frequent enough to notice an exit promptly and rare enough that the probe
    /// cost stays irrelevant.
    /// </summary>
    public const int StatePollIntervalMilliseconds = 3000;

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

    public static string StartupFailureMessage(int port, bool portObserved, bool endpointObserved)
    {
        if (!portObserved)
            return "Harness 服务尚未监听 " + port + " 端口。请检查日志中的 Node.js、依赖或端口占用错误。";
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

/// <summary>
/// Identifies whether a process is Harness, and how much work that answer costs.
///
/// The panel used to query the command line and parent pid for up to eight
/// ancestor processes on every state refresh, which measured at 689ms per refresh
/// and 854ms per completed operation because both SetButtons and RefreshState ran
/// it. In practice the port owner is the Harness process itself, so the answer is
/// available from the first hop; the parent walk stays as insurance for a wrapper
/// process but is only paid for when the first hop does not match.
/// </summary>
public static class HarnessProcessIdentityPolicy
{
    /// <summary>How far up the parent chain to look when the owner itself does not match.</summary>
    public const int MaxAncestorHops = 8;

    /// <summary>
    /// The original rule, unchanged: a command line matches when it names the Harness
    /// installation or runs the web entry point.
    /// </summary>
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

    /// <summary>
    /// Walks from <paramref name="pid"/> toward its ancestors until a command line
    /// matches, reporting how many lookups that took.
    ///
    /// The lookups are injected so the walk can be tested without real processes, and
    /// so the caller keeps ownership of the expensive WMI calls.
    /// </summary>
    public static HarnessIdentityResult Identify(
        int pid,
        Func<int, string> commandLineLookup,
        Func<int, int> parentPidLookup)
    {
        if (commandLineLookup == null)
            throw new ArgumentNullException("commandLineLookup");
        if (parentPidLookup == null)
            throw new ArgumentNullException("parentPidLookup");

        int commandLineLookups = 0;
        int parentLookups = 0;
        int current = pid;
        for (int depth = 0; depth < MaxAncestorHops && current > 0; depth++)
        {
            commandLineLookups++;
            if (IsHarnessCommandLine(commandLineLookup(current)))
                return new HarnessIdentityResult(true, current, depth, commandLineLookups, parentLookups);

            parentLookups++;
            int parent = parentPidLookup(current);
            if (parent == current)
                break;
            current = parent;
        }
        return new HarnessIdentityResult(false, 0, -1, commandLineLookups, parentLookups);
    }
}

/// <summary>The outcome of a process identity walk, including what it cost.</summary>
public sealed class HarnessIdentityResult
{
    public bool IsHarness { get; private set; }

    /// <summary>The pid whose command line matched, or 0 when nothing matched.</summary>
    public int MatchedPid { get; private set; }

    /// <summary>How many ancestors up the match was found, or -1 when it was not.</summary>
    public int MatchedDepth { get; private set; }

    public int CommandLineLookups { get; private set; }
    public int ParentLookups { get; private set; }

    public HarnessIdentityResult(
        bool isHarness,
        int matchedPid,
        int matchedDepth,
        int commandLineLookups,
        int parentLookups)
    {
        IsHarness = isHarness;
        MatchedPid = matchedPid;
        MatchedDepth = matchedDepth;
        CommandLineLookups = commandLineLookups;
        ParentLookups = parentLookups;
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
    private readonly UiValueChip statusLabel = new UiValueChip();
    private readonly UiValueChip runningLabel = new UiValueChip();
    private readonly Label versionLabel = new Label();
    private readonly RichTextBox logBox = new RichTextBox();
    private readonly Button installButton = new UiFlatButton();
    private readonly Button startButton = new UiFlatButton();
    private readonly Button restartButton = new UiFlatButton();
    private readonly Button stopButton = new UiFlatButton();
    private readonly Button updateButton = new UiFlatButton();
    private readonly Button openButton = new UiFlatButton();
    private readonly Button rescanButton = new UiFlatButton();
    private readonly Button openFolderButton = new UiFlatButton();
    private readonly Button uninstallButton = new UiFlatButton();
    /// <summary>The fixed Harness port shown in the header.</summary>
    private readonly Label portLabel = new Label();

    private readonly Button logExportButton = new UiFlatButton();
    private readonly Button logClearButton = new UiFlatButton();

    /// <summary>Header brand mark: the app icon on a gradient tile.</summary>
    private readonly UiBrandMark brandMark = new UiBrandMark();

    /// <summary>The single column that holds the header and the three cards.</summary>
    private TableLayoutPanel rootLayout;
    private readonly HttpClient http = new HttpClient();
    private readonly object gate = new object();
    private bool busy;

    /// <summary>The last snapshot applied to the UI, used to detect real changes.</summary>
    private HarnessStatusSnapshot lastSnapshot;

    /// <summary>
    /// Cancels the operation currently running, if that operation opted in. Null
    /// between operations.
    /// </summary>
    private CancellationTokenSource operationCancellation;

    /// <summary>
    /// The child process currently being waited on, so a cancellation can stop it
    /// instead of leaving an orphaned pnpm or node build behind.
    /// </summary>
    private Process runningToolProcess;

    /// <summary>The title of the operation in flight, used for the cancellation log line.</summary>
    private string runningOperationTitle = "";

    /// <summary>
    /// The token the running operation should observe. <see cref="CancellationToken.None"/>
    /// for operations that did not opt in, so callers never need a null check.
    /// </summary>
    private CancellationToken operationToken = CancellationToken.None;

    /// <summary>
    /// Polls the running state so a stopped Harness is noticed on its own. Fully
    /// qualified because System.Threading is also imported and its Timer would
    /// marshal the tick onto a pool thread instead of the UI thread.
    /// </summary>
    private readonly System.Windows.Forms.Timer stateTimer = new System.Windows.Forms.Timer();

    /// <summary>
    /// Keeps the panel reachable while its window is closed. Created in the constructor
    /// because the close behaviour depends on it existing.
    /// </summary>
    private readonly NotifyIcon trayIcon = new NotifyIcon();

    /// <summary>Whether the user asked to exit, as opposed to closing the window.</summary>
    private bool exitRequested;

    /// <summary>
    /// What closing the window does, and whether the user has already said so. The answer
    /// is read from the settings file at startup and written back on the first close, so
    /// the question is asked once in the panel's life instead of once per launch.
    /// </summary>
    private TrayCloseAction closeAction = TrayCloseAction.MinimizeToTray;
    private bool closeAnswerKnown;

    private Process server;
    private List<string> discoveredRoots = new List<string>();
    private string selectedNodeDirectory = "";
    private string selectedPnpm = "";
    private string selectedCorepack = "";
    private string selectedSourceCommit = "";
    private string nodeHelperPath = "";

    /// <summary>Guards against two overlapping automatic update checks.</summary>
    private bool updateCheckRunning;

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
        // Sized to the design: the info card needs four columns side by side, and the
        // action row needs all nine buttons on one line.
        Width = 1518;
        Height = 1036;
        MinimumSize = new Size(1100, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepSeekHarnessManager/1.0");
        http.Timeout = TimeSpan.FromMinutes(20);

        BuildUi();
        pathBox.Text = LoadConfiguredRoot();
        RefreshState();

        // Whether closing the window minimises to the tray or exits is remembered from the
        // first time the user was asked; see OnFormClosing.
        closeAnswerKnown = TrayClosePolicy.TryParseSettingValue(LoadSetting("closeBehavior"), out closeAction);

        // Check for an upstream release once the window is up. Runs after the first
        // paint and never blocks or alerts: a failed check is a normal condition.
        Shown += OnShown;

        // Poll the running state. Without this the panel showed "正在运行" forever
        // after Harness exited, until the user happened to press something.
        stateTimer.Interval = HarnessLifecyclePolicy.StatePollIntervalMilliseconds;
        stateTimer.Tick += delegate { PollState(); };
        stateTimer.Start();
        FormClosed += delegate { stateTimer.Stop(); stateTimer.Dispose(); };

        BuildTrayIcon();

        // Start invisible; RevealWhenIdle puts it on screen once every control has painted.
        Opacity = 0;
        Application.Idle += RevealWhenIdle;

    }

    /// <summary>
    /// Creates the tray icon and its menu. Built once; the menu items read live state
    /// when opened rather than being rebuilt on every poll.
    /// </summary>
    private void BuildTrayIcon()
    {
        try
        {
            trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception)
        {
            // A missing icon must not stop the panel from having a tray presence.
        }
        trayIcon.Text = TrayClosePolicy.BuildTooltip(false, Port, "");
        trayIcon.Visible = true;
        trayIcon.DoubleClick += delegate { ShowFromTray(); };

        var menu = new ContextMenuStrip();
        menu.Items.Add(TrayClosePolicy.MenuShow, null, delegate { ShowFromTray(); });
        menu.Items.Add(TrayClosePolicy.MenuOpenPage, null, delegate { OpenClick(null, EventArgs.Empty); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(TrayClosePolicy.MenuStart, null, delegate { StartClick(null, EventArgs.Empty); });
        menu.Items.Add(TrayClosePolicy.MenuStop, null, delegate { StopClick(null, EventArgs.Empty); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(TrayClosePolicy.MenuExit, null, delegate { ExitFromTray(); });
        trayIcon.ContextMenuStrip = menu;

        FormClosing += OnFormClosing;
    }

    /// <summary>
    /// Closing the window keeps the panel running in the tray unless the user chose to
    /// exit. The prompt explains that, and its answer is remembered after the first time.
    /// </summary>
    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (exitRequested || e.CloseReason == CloseReason.WindowsShutDown)
            return;

        if (TrayClosePolicy.ShouldAskOnClose(closeAnswerKnown))
        {
            bool answered;
            closeAction = AskCloseAction(out answered);
            if (!answered)
            {
                // The user backed out of the question, so nothing is decided and the
                // window stays where it is.
                e.Cancel = true;
                return;
            }
            closeAnswerKnown = true;
            RememberCloseAction(closeAction);
        }

        e.Cancel = true;
        if (closeAction == TrayCloseAction.Exit)
            ExitFromTray();
        else
            HideToTray();
    }

    /// <summary>
    /// Asks how closing the window should behave, with the two answers as buttons.
    /// Closing the dialog itself reports answered = false: the question stays unanswered
    /// and the panel stays open, which is the only reading that changes nothing.
    /// </summary>
    private TrayCloseAction AskCloseAction(out bool answered)
    {
        // The dialog's measurements, kept together because the client size is derived from
        // them: 14 px of page around a card, 26 px of card inside its edges, and the two
        // 44 px answer buttons.
        const int DialogPadding = 14;
        const int CardPaddingX = 26;
        const int CardPaddingTop = 24;
        const int CardPaddingBottom = 22;
        const int MessageWidth = 540;
        const int ButtonHeight = 44;
        const int ButtonWidth = 158;

        TrayCloseAction choice = TrayCloseAction.MinimizeToTray;
        // An out parameter cannot be touched from the click handlers, so the answer is
        // collected here and handed back after the dialog closes.
        bool confirmed = false;

        using (var dialog = new Form())
        {
            dialog.Text = TrayClosePolicy.ClosePromptTitle;
            dialog.Icon = Icon;
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
            dialog.ShowInTaskbar = false;
            dialog.Font = UiStyle.BodyFont();
            // The page colour, so the strip around the card is right from the first frame
            // rather than flashing the default control grey.
            dialog.BackColor = UiStyle.WindowBackground;

            // The same page-under-card arrangement the panel itself uses: the gradient is
            // the page, the white rounded card holds everything. A stock dialog with its
            // own spacing and button sizes reads as a different program.
            dialog.Paint += delegate(object sender, PaintEventArgs e)
            {
                UiBackground.Paint(e.Graphics, dialog.ClientRectangle);
            };

            var card = new UiCardPanel();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0);
            dialog.Padding = new Padding(DialogPadding);
            dialog.Controls.Add(card);

            // Laid out rather than placed at fixed coordinates: the dialog's client size
            // is not what the constructor asked for once Windows has had its say about
            // scaling, and fixed positions clipped the last line of the message.
            var layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Margin = new Padding(0);
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.BackColor = Color.Transparent;
            layout.Padding = new Padding(CardPaddingX, CardPaddingTop, CardPaddingX, CardPaddingBottom);
            // Without an explicit column style the single column sizes itself and the
            // message gets a narrower box than it was measured against.
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ButtonHeight));
            card.Controls.Add(layout);

            var message = new Label();
            message.Text = TrayClosePolicy.BuildClosePrompt(Port);
            message.Font = UiStyle.BodyFont();
            message.ForeColor = UiStyle.TextPrimary;
            message.BackColor = Color.Transparent;
            // Measured before the dialog is sized: the text wraps to a different number of
            // lines than the source suggests, and a dialog sized by eye either clips the
            // last line or leaves a gap where nothing is.
            message.AutoSize = true;
            // Measured a little narrower than the label ends up, so the wrap that is
            // measured is at least as tall as the wrap that is drawn; measuring at the
            // exact width came out one line short and clipped the last bullet.
            message.MaximumSize = new Size(MessageWidth - 16, 0);
            int messageHeight = message.PreferredSize.Height + 8;
            message.AutoSize = false;
            message.Dock = DockStyle.Fill;
            message.Margin = new Padding(0);
            message.TextAlign = ContentAlignment.TopLeft;
            layout.Controls.Add(message, 0, 0);

            dialog.ClientSize = new Size(
                MessageWidth + (CardPaddingX * 2) + (DialogPadding * 2),
                messageHeight + CardPaddingTop + CardPaddingBottom + ButtonHeight + (DialogPadding * 2));

            var buttons = new TableLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0);
            buttons.ColumnCount = 3;
            buttons.RowCount = 1;
            buttons.BackColor = Color.Transparent;
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ButtonWidth));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ButtonWidth));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(buttons, 0, 1);

            var keepRunning = new UiFlatButton();
            keepRunning.Text = TrayClosePolicy.ClosePromptTrayAnswer;
            keepRunning.IsPrimary = true;
            keepRunning.Dock = DockStyle.Fill;
            keepRunning.Margin = new Padding(0, 0, 6, 0);
            keepRunning.Click += delegate
            {
                choice = TrayCloseAction.MinimizeToTray;
                confirmed = true;
                dialog.Close();
            };
            buttons.Controls.Add(keepRunning, 1, 0);

            var quit = new UiFlatButton();
            quit.Text = TrayClosePolicy.ClosePromptExitAnswer;
            quit.Dock = DockStyle.Fill;
            quit.Margin = new Padding(6, 0, 0, 0);
            quit.Click += delegate
            {
                choice = TrayCloseAction.Exit;
                confirmed = true;
                dialog.Close();
            };
            buttons.Controls.Add(quit, 2, 0);

            dialog.AcceptButton = keepRunning;
            dialog.ShowDialog(this);
        }
        answered = confirmed;
        return choice;
    }

    /// <summary>
    /// Stores the answer, so the question is asked once in the panel's life rather than
    /// once per launch. Failing to save only costs a repeated question, so it is logged
    /// and not treated as fatal.
    /// </summary>
    private void RememberCloseAction(TrayCloseAction action)
    {
        try
        {
            SaveSetting("closeBehavior", TrayClosePolicy.ToSettingValue(action));
        }
        catch (Exception ex)
        {
            Log("保存关闭方式失败: " + ex.Message);
        }
    }

    /// <summary>Hides the window and says so, so the panel is not simply "missing".</summary>
    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        Log("已最小化到托盘。双击托盘图标可重新打开。");
    }

    private void ShowFromTray()
    {
        Show();
        Reveal();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Real exit. Warns when an operation is in flight, because exiting mid-build would
    /// orphan the child process the cancellation logic exists to stop.
    /// </summary>
    private void ExitFromTray()
    {
        if (busy)
        {
            DialogResult answer = MessageBox.Show(
                this,
                "当前有操作正在进行。退出会中断它，并可能留下未完成的安装。" + Environment.NewLine + Environment.NewLine +
                "建议先点击“取消”等待操作停止。仍要退出吗？",
                "DeepSeek Harness",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
                return;
            CancelOperation();
        }

        exitRequested = true;
        trayIcon.Visible = false;
        Close();
    }


    private void OnShown(object sender, EventArgs e)
    {
        Shown -= OnShown;
        if (!IsInstalled())
            return;
        Task.Run(delegate { return AutoCheckForUpdatesAsync(); });
    }

    /// <summary>
    /// Automatic update check. Deliberately quiet: no modal dialog, no busy state,
    /// and no effect on the action buttons. The manual "检查 Harness 更新" button
    /// remains the place that asks before updating.
    /// </summary>
    private async Task AutoCheckForUpdatesAsync()
    {
        if (updateCheckRunning)
            return;
        updateCheckRunning = true;
        try
        {
            string branch = await GetDefaultBranchAsync();
            string remote = await GetRemoteCommitAsync(branch);
            string local = LocalCommit();

            // The log records every check, including "up to date". The status area stays
            // quiet either way: an available update is not worth a permanent marker on a
            // panel whose job is to report the service's state, and the log is where the
            // check belongs.
            string forLog = HarnessUpdatePolicy.DescribeAvailabilityForLog(local, remote);
            if (!String.IsNullOrEmpty(forLog))
                Log("自动检查更新：" + forLog +
                    (HarnessUpdatePolicy.HasUpdate(local, remote) ? "。点击“检查 Harness 更新”即可升级。" : "。"));
        }
        catch (Exception)
        {
            // Offline, rate limited, or a proxy fault is not worth interrupting the
            // user at startup; the manual button still reports failures.
        }
        finally
        {
            updateCheckRunning = false;
        }
    }

    private void BuildUi()
    {
        BackColor = UiStyle.WindowBackground;
        Font = UiStyle.BodyFont();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        rootLayout = new TableLayoutPanel();
        rootLayout.Dock = DockStyle.Fill;
        // Room for the cards' shadows: a shadow drawn inside the cell would be clipped, so
        // the layout leaves the spread as margin and each card sits inside its cell.
        rootLayout.Padding = new Padding(UiStyle.OuterMargin, UiStyle.OuterMargin, UiStyle.OuterMargin, UiStyle.OuterMargin);
        rootLayout.ColumnCount = 1;
        rootLayout.RowCount = 4;
        rootLayout.BackColor = Color.Transparent;
        // Tight rhythm: the header, then two cards sized to their content, then the log.
        // The gaps are deliberately small so the three cards read as one screen.
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.HeaderHeight()));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.InfoCardHeight() + UiStyle.CardGap));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.ActionCardHeight() + UiStyle.CardGap));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(rootLayout);

        rootLayout.Controls.Add(BuildHeader(), 0, 0);
        rootLayout.Controls.Add(BuildInfoCard(), 0, 1);
        rootLayout.Controls.Add(BuildActionCard(), 0, 2);
        Control logCard = BuildLogCard();
        logCard.Margin = new Padding(UiStyle.ShadowSpread, 0, UiStyle.ShadowSpread, 0);
        rootLayout.Controls.Add(logCard, 0, 3);

    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // ClientRectangle is empty while the window is minimized, and the erase message
        // still arrives: the guard lives in UiBackground.Paint.
        paintedOnce = true;
        UiBackground.Paint(e.Graphics, ClientRectangle);
    }

    private bool revealed;
    private bool paintedOnce;

    /// <summary>
    /// Puts the window on screen once it has something to show.
    ///
    /// Windows makes a window visible the moment it is shown and lets it paint afterwards,
    /// which is why the panel used to open as a half-built frame: the desktop, then a
    /// window with only its background, then the controls arriving area by area.
    ///
    /// The trigger is the first idle moment after the first paint: an empty message queue
    /// means every control has finished drawing. Neither of the obvious alternatives works.
    /// The form's Shown event arrives before the log area has painted, and a Windows timer
    /// is starved for as long as paint messages keep coming, which on the machine this was
    /// written for meant it never fired at all.
    /// </summary>
    private void RevealWhenIdle(object sender, EventArgs e)
    {
        if (!paintedOnce || !Visible)
            return;
        Reveal();
    }

    /// <summary>
    /// Ends the invisible start. Opening from the tray also calls this: a panel that was
    /// never revealed must not stay transparent when the user asks to see it.
    /// </summary>
    private void Reveal()
    {
        if (revealed)
            return;
        revealed = true;
        Application.Idle -= RevealWhenIdle;
        Opacity = 1;
    }


    /// <summary>
    /// Brand mark, product name, tagline, and the read-only port.
    /// </summary>
    private Control BuildHeader()
    {
        var header = new TableLayoutPanel();
        header.Dock = DockStyle.Fill;
        header.Margin = new Padding(0);
        header.ColumnCount = 2;
        header.RowCount = 1;
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.BackColor = Color.Transparent;
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));

        var brand = new TableLayoutPanel();
        brand.Dock = DockStyle.Fill;
        brand.Margin = new Padding(0);
        brand.ColumnCount = 2;
        brand.RowCount = 2;
        brand.BackColor = Color.Transparent;
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        brand.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        brand.RowStyles.Add(new RowStyle(SizeType.Percent, 40));

        brandMark.Dock = DockStyle.Fill;
        brandMark.Margin = new Padding(4, 14, 14, 14);
        brand.Controls.Add(brandMark, 0, 0);
        brand.SetRowSpan(brandMark, 2);

        var title = new Label();
        title.Text = "DeepSeek Harness";
        title.Font = UiStyle.TitleFont();
        title.ForeColor = UiStyle.TextPrimary;
        title.Dock = DockStyle.Fill;
        title.AutoSize = false;
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.BackColor = Color.Transparent;
        brand.Controls.Add(title, 1, 0);

        var tagline = new Label();
        tagline.Text = "轻量 · 高效 · 稳定";
        tagline.Font = UiStyle.SubtitleFont();
        tagline.ForeColor = UiStyle.TextSecondary;
        tagline.Dock = DockStyle.Fill;
        tagline.AutoSize = false;
        tagline.TextAlign = ContentAlignment.MiddleLeft;
        tagline.BackColor = Color.Transparent;
        brand.Controls.Add(tagline, 1, 1);
        header.Controls.Add(brand, 0, 0);

        var portPanel = new UiCardPanel();
        portPanel.Dock = DockStyle.Fill;
        portPanel.Margin = new Padding(8, 26, 0, 26);
        portPanel.ShowShadow = false;
        portPanel.CornerRadius = 18;

        var portLayout = new TableLayoutPanel();
        portLayout.Dock = DockStyle.Fill;
        portLayout.Margin = new Padding(0);
        portLayout.Padding = new Padding(8, 0, 8, 0);
        portLayout.ColumnCount = 4;
        portLayout.RowCount = 1;
        portLayout.BackColor = Color.Transparent;
        portLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        portLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        portLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        portLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        portLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var linkIcon = new UiInfoIcon();
        linkIcon.Icon = UiIcon.Port;
        linkIcon.Dock = DockStyle.Fill;
        linkIcon.Margin = new Padding(8, 8, 8, 8);
        portLayout.Controls.Add(linkIcon, 0, 0);

        var portCaption = new Label();
        portCaption.Text = "端口";
        portCaption.Font = UiStyle.BodyFont();
        portCaption.ForeColor = UiStyle.TextSecondary;
        portCaption.Dock = DockStyle.Fill;
        portCaption.TextAlign = ContentAlignment.MiddleCenter;
        portCaption.BackColor = Color.Transparent;
        portLayout.Controls.Add(portCaption, 1, 0);

        var divider = new Panel();
        divider.Dock = DockStyle.Fill;
        divider.Margin = new Padding(0, 14, 0, 14);
        divider.BackColor = UiStyle.FieldBorder;
        portLayout.Controls.Add(divider, 2, 0);

        portLabel.Text = Port.ToString();
        // The number is a value of the "端口" caption, so it inherits that caption's size
        // and colour instead of reading as a second, louder heading.
        portLabel.Font = UiStyle.BodyFont();
        portLabel.ForeColor = UiStyle.TextSecondary;
        portLabel.BackColor = UiStyle.CardBackground;
        portLabel.TextAlign = ContentAlignment.MiddleCenter;
        portLabel.Dock = DockStyle.Fill;
        portLabel.Margin = new Padding(8, 10, 4, 10);
        portLayout.Controls.Add(portLabel, 3, 0);
        portPanel.Controls.Add(portLayout);

        header.Controls.Add(portPanel, 1, 0);

        return header;
    }

    /// <summary>
    /// A card with room around it for its shadow. The margin is what the shadow is drawn
    /// into: a card that filled its cell would have the shadow clipped at the cell edge and
    /// the depth would disappear.
    /// </summary>
    private static UiCardPanel NewCard()
    {
        var card = new UiCardPanel();
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(
            UiStyle.ShadowSpread,
            0,
            UiStyle.ShadowSpread,
            UiStyle.CardGap);
        // The shadow is drawn outward from the card's edge, so the cell must be wider than
        // the card by half the spread on each side for it to be visible at all.
        card.Padding = new Padding(0);
        return card;
    }

    private static Control BuildInfoMetric(string caption, Control value, UiIcon icon)
    {
        var metric = new TableLayoutPanel();
        metric.Dock = DockStyle.Fill;
        metric.Margin = new Padding(0);
        metric.ColumnCount = 2;
        metric.RowCount = 1;
        metric.BackColor = Color.Transparent;
        metric.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
        metric.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        metric.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var iconTile = new UiInfoIcon();
        iconTile.Icon = icon;
        iconTile.Dock = DockStyle.Fill;
        iconTile.Margin = new Padding(12, 20, 12, 20);
        metric.Controls.Add(iconTile, 0, 0);

        var text = new TableLayoutPanel();
        text.Dock = DockStyle.Fill;
        text.Margin = new Padding(0, 18, 8, 18);
        text.ColumnCount = 1;
        text.RowCount = 2;
        text.BackColor = Color.Transparent;
        text.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
        text.RowStyles.Add(new RowStyle(SizeType.Percent, 54));

        var label = new Label();
        label.Text = caption;
        label.Font = UiStyle.LabelFont();
        label.ForeColor = UiStyle.TextSecondary;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.BottomLeft;
        label.BackColor = Color.Transparent;
        text.Controls.Add(label, 0, 0);

        value.Dock = DockStyle.Fill;
        value.Margin = new Padding(0);
        text.Controls.Add(value, 0, 1);
        metric.Controls.Add(text, 1, 0);
        return metric;
    }

    /// <summary>
    /// Card title with the small accent bar the design puts to its left.
    /// </summary>
    private static Control BuildCardTitle(string text)
    {
        var row = new TableLayoutPanel();
        row.Dock = DockStyle.Top;
        row.Margin = new Padding(0);
        row.Height = 26;
        row.ColumnCount = 2;
        row.RowCount = 1;
        row.BackColor = UiStyle.CardBackground;
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var accent = new Panel();
        accent.Dock = DockStyle.Fill;
        accent.Margin = new Padding(0, 6, 0, 6);
        accent.BackColor = UiStyle.Primary;
        // Without a cap the panel keeps its default height and the AutoSize row grows to
        // fit it, which is why the accent rendered as a block instead of a bar.
        accent.MaximumSize = new Size(3, 14);
        row.Controls.Add(accent, 0, 0);

        var label = new Label();
        label.Text = text;
        label.Font = UiStyle.CardTitleFont();
        label.ForeColor = UiStyle.TextPrimary;
        label.Dock = DockStyle.Fill;
        label.AutoSize = false;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.BackColor = UiStyle.CardBackground;
        row.Controls.Add(label, 1, 0);
        return row;
    }

    /// <summary>
    /// A caption plus its value, the pattern the design uses for every fact.
    ///
    /// Laid out with absolute positions inside a plain Panel rather than a table: a
    /// two-row table kept sizing its value row to the label's own height and pushing the
    /// text below the card, and this shape has two fixed heights so a table buys nothing.
    /// </summary>
    private static Control BuildField(string caption, Control value, int captionHeight, int valueHeight)
    {
        var field = new Panel();
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(0);
        field.BackColor = UiStyle.CardBackground;

        var captionLabel = new Label();
        captionLabel.Text = caption;
        captionLabel.Font = UiStyle.LabelFont();
        captionLabel.ForeColor = UiStyle.TextSecondary;
        captionLabel.AutoSize = false;
        captionLabel.TextAlign = ContentAlignment.MiddleLeft;
        captionLabel.BackColor = UiStyle.CardBackground;
        captionLabel.Bounds = new Rectangle(0, 0, 400, captionHeight);
        captionLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        field.Controls.Add(captionLabel);

        value.Bounds = new Rectangle(0, captionHeight + UiMetrics.CaptionGap, 400, valueHeight);
        value.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        field.Controls.Add(value);

        // Stretch both controls to the panel width once it has one; anchoring handles
        // resizing after that.
        field.Layout += delegate
        {
            int width = Math.Max(40, field.ClientSize.Width);
            captionLabel.Width = width;
            value.Width = width;
        };
        return field;
    }

    /// <summary>
    /// The runtime facts: directory, state, running state, version.
    /// </summary>
    private Control BuildInfoCard()
    {
        var card = NewCard();
        var cells = new TableLayoutPanel();
        cells.Dock = DockStyle.Fill;
        cells.Margin = new Padding(0);
        cells.Padding = new Padding(8, 0, 8, 0);
        cells.ColumnCount = 7;
        cells.RowCount = 1;
        cells.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        cells.BackColor = Color.Transparent;
        cells.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cells.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        cells.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cells.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        cells.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cells.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        cells.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        pathBox.TextAlign = ContentAlignment.MiddleLeft;
        pathBox.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        pathBox.AutoSize = false;
        pathBox.BackColor = UiStyle.CardBackground;
        pathBox.ForeColor = UiStyle.InkBlue;

        versionLabel.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        versionLabel.AutoSize = false;
        versionLabel.BackColor = UiStyle.CardBackground;
        versionLabel.ForeColor = UiStyle.InkBlue;
        versionLabel.TextAlign = ContentAlignment.MiddleLeft;
        versionLabel.AutoEllipsis = true;
        statusLabel.UseFill = false;
        statusLabel.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        runningLabel.UseFill = false;
        runningLabel.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);

        cells.Controls.Add(BuildInfoMetric("安装目录", pathBox, UiIcon.InstallDirectory), 0, 0);
        cells.Controls.Add(NewInfoDivider(), 1, 0);
        cells.Controls.Add(BuildInfoMetric("状态", statusLabel, UiIcon.Layers), 2, 0);
        cells.Controls.Add(NewInfoDivider(), 3, 0);
        cells.Controls.Add(BuildInfoMetric("运行状态", runningLabel, UiIcon.Start), 4, 0);
        cells.Controls.Add(NewInfoDivider(), 5, 0);
        cells.Controls.Add(BuildInfoMetric("Harness 版本", versionLabel, UiIcon.Package), 6, 0);
        card.Controls.Add(cells);
        return card;
    }

    private static Control NewInfoDivider()
    {
        var divider = new Panel();
        divider.Dock = DockStyle.Fill;
        divider.Margin = new Padding(0, 24, 0, 24);
        divider.BackColor = UiStyle.FieldBorder;
        return divider;
    }

    /// <summary>
    /// The action buttons, split into the primary install action and the rest.
    /// </summary>
    private Control BuildActionCard()
    {
        var buttons = new TableLayoutPanel();
        buttons.Dock = DockStyle.Fill;
        buttons.Margin = new Padding(UiStyle.ShadowSpread, 0, UiStyle.ShadowSpread, UiStyle.CardGap);
        buttons.ColumnCount = 9;
        buttons.RowCount = 1;
        buttons.BackColor = Color.Transparent;
        int[] widths = { 13, 12, 9, 9, 9, 11, 11, 12, 14 };
        foreach (int width in widths)
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        StyleActionButton(installButton, "安装", UiIcon.Install, true, InstallClick);
        StyleActionButton(uninstallButton, "卸载", UiIcon.Uninstall, false, UninstallClick);
        StyleActionButton(startButton, "启动", UiIcon.Start, false, StartClick);
        StyleActionButton(stopButton, "停止", UiIcon.Stop, false, StopClick);
        StyleActionButton(restartButton, "重启", UiIcon.Restart, false, RestartClick);
        StyleActionButton(updateButton, "检查更新", UiIcon.Update, false, UpdateClick);
        StyleActionButton(openButton, "打开页面", UiIcon.OpenPage, false, OpenClick);
        StyleActionButton(openFolderButton, "打开目录", UiIcon.OpenFolder, false, OpenFolderClick);
        StyleActionButton(rescanButton, "扫描", UiIcon.Rescan, false, RescanClick);
        ((UiFlatButton)uninstallButton).IsDanger = false;

        buttons.Controls.Add(installButton, 0, 0);
        buttons.Controls.Add(uninstallButton, 1, 0);
        buttons.Controls.Add(startButton, 2, 0);
        buttons.Controls.Add(stopButton, 3, 0);
        buttons.Controls.Add(restartButton, 4, 0);
        buttons.Controls.Add(updateButton, 5, 0);
        buttons.Controls.Add(openButton, 6, 0);
        buttons.Controls.Add(openFolderButton, 7, 0);
        buttons.Controls.Add(rescanButton, 8, 0);
        return buttons;
    }

    /// <summary>
    /// Replaces a Button with the flat design and wires its click handler.
    ///
    /// The fields are declared as Button, so a UiFlatButton instance is assigned through
    /// the base type and the designer-only properties are set on it directly.
    /// </summary>
    private static void StyleActionButton(
        Button button,
        string text,
        UiIcon icon,
        bool primary,
        EventHandler handler)
    {
        var flat = button as UiFlatButton;
        if (flat == null)
            throw new InvalidOperationException("Action buttons must be created as UiFlatButton.");
        flat.Text = text;
        flat.Icon = icon;
        flat.IsPrimary = primary;
        flat.AutoSize = false;
        flat.Dock = DockStyle.Fill;
        flat.Margin = new Padding(0, 1, 10, 1);
        flat.Click += handler;
    }

    /// <summary>
    /// The log card: title, the search and export controls, then the log itself.
    /// </summary>
    private Control BuildLogCard()
    {
        var card = NewCard();

        var inside = new TableLayoutPanel();
        inside.Dock = DockStyle.Fill;
        inside.Padding = new Padding(12, 10, 12, 12);
        inside.ColumnCount = 1;
        inside.RowCount = 2;
        inside.BackColor = Color.Transparent;
        inside.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        inside.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var heading = new TableLayoutPanel();
        heading.Dock = DockStyle.Fill;
        heading.Margin = new Padding(0);
        heading.ColumnCount = 2;
        heading.RowCount = 1;
        heading.BackColor = Color.Transparent;
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 350));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new TableLayoutPanel();
        title.Dock = DockStyle.Fill;
        title.Margin = new Padding(0);
        title.ColumnCount = 2;
        title.RowCount = 1;
        title.BackColor = Color.Transparent;
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var titleIcon = new UiInfoIcon();
        titleIcon.Icon = UiIcon.Log;
        titleIcon.DrawCircle = false;
        titleIcon.Dock = DockStyle.Fill;
        titleIcon.Margin = new Padding(2, 14, 8, 14);
        title.Controls.Add(titleIcon, 0, 0);

        var titleLabel = new Label();
        titleLabel.Text = "运行日志";
        // Matches the runtime-fact captions to its left: the heading is a section label
        // here, not a second page title, and the larger face crowded the toolbar.
        titleLabel.Font = UiStyle.LabelFont();
        titleLabel.ForeColor = UiStyle.InkBlue;
        titleLabel.Dock = DockStyle.Fill;
        titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        titleLabel.BackColor = Color.Transparent;
        title.Controls.Add(titleLabel, 1, 0);
        heading.Controls.Add(title, 0, 0);
        heading.Controls.Add(BuildLogToolbar(), 1, 0);
        inside.Controls.Add(heading, 0, 0);

        logBox.ReadOnly = true;
        logBox.ScrollBars = RichTextBoxScrollBars.Vertical;
        logBox.WordWrap = true;
        logBox.HideSelection = false;
        logBox.Dock = DockStyle.Fill;
        logBox.BorderStyle = BorderStyle.None;
        logBox.BackColor = UiStyle.LogBackground;
        logBox.Font = UiStyle.LogFont();
        // Two aligned columns: the timestamp then the message, as the design shows.
        logBox.SelectionTabs = new[] { 76, 320, 560 };
        var logHost = new UiInputPanel();
        logHost.Dock = DockStyle.Fill;
        logHost.Margin = new Padding(0);
        logHost.Padding = new Padding(12, 8, 8, 8);
        logHost.Controls.Add(logBox);
        inside.Controls.Add(logHost, 0, 1);
        card.Controls.Add(inside);
        return card;
    }

    private Control BuildLogToolbar()
    {
        var toolbar = new TableLayoutPanel();
        toolbar.Dock = DockStyle.Fill;
        toolbar.Margin = new Padding(0);
        toolbar.ColumnCount = 3;
        toolbar.RowCount = 1;
        toolbar.BackColor = Color.Transparent;
        // Only the two actions that are actually used remain. The first column takes the
        // slack so the buttons sit against the right edge of the card.
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        StyleToolbarButton(logExportButton, "导出", UiIcon.Export, LogExportClick);
        StyleToolbarButton(logClearButton, "清空", UiIcon.Clear, LogClearClick);

        toolbar.Controls.Add(logExportButton, 1, 0);
        toolbar.Controls.Add(logClearButton, 2, 0);
        return toolbar;
    }

    private static void StyleToolbarButton(Button button, string text, UiIcon icon, EventHandler handler)
    {
        var flat = button as UiFlatButton;
        if (flat == null)
            throw new InvalidOperationException("Log toolbar buttons must be created as UiFlatButton.");
        flat.Text = text;
        flat.Icon = icon;
        flat.IsPrimary = false;
        // The width comes from the cell, and the label is sized to fit it: a fixed width
        // wider than the column clipped longer labels to "导出...".
        flat.AutoSize = false;
        flat.Dock = DockStyle.Fill;
        flat.Margin = new Padding(0, 2, 4, 2);
        flat.Click += handler;
    }

    /// <summary>
    /// Whether a click on an action button should cancel the running operation instead

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

    /// <summary>
    /// Writes the log to a file the user chooses. The export carries the timestamp and
    /// install root so the file explains itself when it is shared.
    /// </summary>
    private void LogExportClick(object sender, EventArgs e)
    {
        DateTime now = DateTime.Now;
        using (var dialog = new SaveFileDialog())
        {
            dialog.Title = "导出控制面板日志";
            dialog.Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
            dialog.FileName = LogExportPolicy.BuildDefaultFileName(now);
            dialog.InitialDirectory = Directory.Exists(Root)
                ? Root
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                string text = LogExportPolicy.BuildExportText(logBox.Text, Root, now);
                File.WriteAllText(dialog.FileName, text, new UTF8Encoding(false));
                Log("日志已导出: " + dialog.FileName);
            }
            catch (Exception ex)
            {
                Log("导出日志失败: " + ex.Message);
                MessageBox.Show(this, "导出日志失败：" + Environment.NewLine + ex.Message,
                    "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    /// <summary>
    /// Clears the log after confirming, because the previous lines are the only record
    /// of what happened. Exporting first is offered rather than silently discarding.
    /// </summary>
    private void LogClearClick(object sender, EventArgs e)
    {
        if (logBox.TextLength == 0)
            return;
        DialogResult answer = MessageBox.Show(
            this,
            "清空后当前日志将不再显示。如果需要保留，请先导出。" + Environment.NewLine + Environment.NewLine +
            "确定清空吗？",
            "DeepSeek Harness",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
            return;
        logBox.Clear();
    }

    /// <summary>
    /// Whether a click on an action button should cancel the running operation instead
    /// of starting a new one. The install and update buttons are the visible cancel
    /// affordance, so their normal handler stands down while a cancellable operation
    /// is in flight.
    /// </summary>
    private bool TryCancelFromButtonClick()
    {
        if (operationCancellation == null)
            return false;
        CancelOperation();
        return true;
    }

    private void InstallClick(object sender, EventArgs e)
    {
        if (TryCancelFromButtonClick())
            return;
        if (IsInstalled() && !IsInstallationReady())
        {
            string prompt = "检测到当前 Harness 安装不完整，无法启动。" + Environment.NewLine +
                "将重新下载官方源码并修复安装，保留 Harness 专用运行环境、日志和你的用户配置。" + Environment.NewLine +
                "确定开始修复吗？";
            if (Ask(prompt, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                RunAsync("正在修复 DeepSeek Harness", RepairAsync, true);
            return;
        }
        if (!ChooseInstallRoot())
            return;
        RunAsync("正在安装 DeepSeek Harness", InstallAsync, true);
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
        if (TryCancelFromButtonClick())
            return;
        // The download and rebuild inside this flow can run for minutes.
        RunAsync("正在检查 DeepSeek Harness 更新", CheckUpdateAsync, true);
    }

    private void OpenClick(object sender, EventArgs e)
    {
        string readyUrl = StoredWebUrl();
        if (String.IsNullOrEmpty(readyUrl))
        {
            Log("当前运行实例没有可用的认证地址，请点击“重启”生成新的访问地址。");
            return;
        }
        try
        {
            Process.Start(readyUrl);
        }
        catch (Exception ex)
        {
            // No registered browser handler, or a blocked shell association. The
            // address is still usable, so hand it over instead of failing silently.
            Log("无法自动打开浏览器：" + ex.Message);
            Log("请在浏览器中手动打开：" + readyUrl);
            MessageBox.Show(
                this,
                "无法自动打开浏览器。" + Environment.NewLine + Environment.NewLine +
                "请在浏览器中手动打开这个地址：" + Environment.NewLine + readyUrl,
                "DeepSeek Harness",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
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

    private void UninstallClick(object sender, EventArgs e)
    {
        if (!IsInstalled())
            return;

        List<UninstallTarget> targets = GetUninstallTargets();
        List<UninstallTarget> selected;
        using (var dialog = new UninstallSelectionForm(targets))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            selected = dialog.SelectedTargets;
        }

        string summary = UninstallSelectionPolicy.DescribeSelection(
            targets,
            delegate(UninstallTarget target) { return selected.Contains(target); });
        string warning = "将删除以下内容：" +
            Environment.NewLine + Environment.NewLine +
            summary +
            Environment.NewLine + Environment.NewLine +
            UninstallSelectionPolicy.BuildWarning(
                targets,
                delegate(UninstallTarget target) { return selected.Contains(target); }) +
            Environment.NewLine +
            "系统全局 Node、npm、pnpm 和其他项目不会被删除。" +
            Environment.NewLine + Environment.NewLine +
            "确定继续吗？";
        if (Ask(warning, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        pendingUninstallTargets = selected;
        RunAsync("正在卸载所选内容", UninstallAsync);
    }

    /// <summary>
    /// The targets the user confirmed. Set by the selection dialog and consumed by the
    /// uninstall run, so the deletion can never touch something that was not shown.
    /// </summary>
    private List<UninstallTarget> pendingUninstallTargets;

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

    /// <summary>
    /// Runs a long operation with the UI locked.
    ///
    /// <paramref name="cancellable"/> decides whether the same button doubles as a
    /// cancel action. The install, update, and repair flows are cancellable because
    /// their child builds can run for minutes; a start or stop is not, because
    /// interrupting it halfway leaves the service in an unknown state.
    /// </summary>
    private void RunAsync(string title, Func<Task> action, bool cancellable = false)
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

        CancellationTokenSource cancellation = null;
        if (cancellable)
        {
            cancellation = new CancellationTokenSource();
            operationCancellation = cancellation;
            operationToken = cancellation.Token;
        }
        else
        {
            operationToken = CancellationToken.None;
        }
        runningOperationTitle = title;
        runningToolProcess = null;

        SetButtons(false, lastSnapshot ?? ComputeStatusSnapshot());
        Log(title + "...");
        Task.Run(action).ContinueWith(t =>
        {
            BeginInvoke((Action)delegate
            {
                bool wasCancelled = OperationCancellationPolicy.IsCancellation(
                    cancellation != null && cancellation.IsCancellationRequested,
                    t.IsCanceled);

                busy = false;
                runningToolProcess = null;
                operationToken = CancellationToken.None;
                operationCancellation = null;

                if (wasCancelled)
                {
                    Log(OperationCancellationPolicy.CancelledLogLine);
                }
                else if (t.IsFaulted)
                {
                    string message = t.Exception == null ? "未知错误" : t.Exception.GetBaseException().Message;
                    Log("失败摘要：" + title + "未完成。原因：" + message);
                    MessageBox.Show(this, message, "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    Log("完成。");
                }

                if (cancellation != null)
                    cancellation.Dispose();

                RefreshState();
            });
        });
    }

    /// <summary>
    /// Stops the running operation: signals the token and terminates the child process
    /// tree, so a multi-minute build does not keep consuming the machine after the user
    /// has asked it to stop.
    /// </summary>
    private void CancelOperation()
    {
        CancellationTokenSource cancellation = operationCancellation;
        if (cancellation == null)
            return;

        Log("正在取消" + (String.IsNullOrEmpty(runningOperationTitle) ? "" : "：" + runningOperationTitle) + "...");
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Process child = runningToolProcess;
        if (child != null)
            KillProcessTree(child);
    }

    /// <summary>
    /// Terminates a child and its descendants. taskkill is used rather than
    /// Process.Kill(true) because the compiler's reference assemblies predate the
    /// whole-tree overload, and /T is what stops pnpm's node grandchild.
    /// </summary>
    private void KillProcessTree(Process child)
    {
        try
        {
            if (child.HasExited)
                return;
            int processId = child.Id;
            RunTool("taskkill.exe", OperationCancellationPolicy.BuildTaskkillArguments(processId), Root);
            Log("已结束子进程树（PID " + processId + "）。");
        }
        catch (Exception ex)
        {
            // The process exiting between the check and the kill is the ordinary race,
            // not a failure worth alarming about.
            if (!OperationCancellationPolicy.IsExpectedKillRace(ex))
                Log("结束子进程时出错：" + ex.Message);
        }
    }

    /// <summary>
    /// Applies button availability from an already-computed snapshot. Taking the
    /// snapshot as a parameter is what removed the duplicate port and process probing
    /// this method used to repeat on every call.
    /// </summary>
    private void SetButtons(bool enabled, HarnessStatusSnapshot snapshot)
    {
        bool cancellable = operationCancellation != null;

        installButton.Text = cancellable
            ? OperationCancellationPolicy.CancelButtonText
            : (snapshot.Installed && !snapshot.Ready ? "修复安装" : "安装");
        updateButton.Text = cancellable
            ? OperationCancellationPolicy.CancelButtonText
            : "检查更新";

        // Every other action is disabled first, so no state can leave one of them live
        // during an operation. That also makes the early return below safe.
        startButton.Enabled = false;
        restartButton.Enabled = false;
        stopButton.Enabled = false;
        openButton.Enabled = false;
        rescanButton.Enabled = false;
        openFolderButton.Enabled = false;
        uninstallButton.Enabled = false;

        if (cancellable)
        {
            // While a cancellable operation runs, the two buttons that could have
            // started it stay live as the way to stop it.
            installButton.Enabled = true;
            updateButton.Enabled = true;
            return;
        }

        if (!enabled)
        {
            installButton.Enabled = false;
            updateButton.Enabled = false;
            return;
        }

        installButton.Enabled = (!snapshot.Installed || !snapshot.Ready) && !snapshot.MultipleInstalls;
        updateButton.Enabled = snapshot.Ready && !snapshot.MultipleInstalls;
        startButton.Enabled = snapshot.Ready && !snapshot.PortBusy && !snapshot.MultipleInstalls;
        restartButton.Enabled = snapshot.Ready && snapshot.Running && !snapshot.MultipleInstalls;
        stopButton.Enabled = snapshot.Running && !snapshot.MultipleInstalls;
        openButton.Enabled = snapshot.Running;
        rescanButton.Enabled = true;
        openFolderButton.Enabled = Directory.Exists(Root);
        uninstallButton.Enabled = snapshot.Installed && !snapshot.MultipleInstalls;
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
            logBox.SelectionColor = UiStyle.Primary;
            logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "    ");
            logBox.SelectionColor = UiStyle.InkBlue;
            logBox.AppendText(LogLevelText(formatted.Kind).PadRight(7) + "  ");
            logBox.SelectionColor = LogColor(formatted.Kind);
            logBox.AppendText(formatted.Text + Environment.NewLine);
        }
        logBox.SelectionColor = logBox.ForeColor;
        logBox.ScrollToCaret();
    }

    private static string LogLevelText(LogMessageKind kind)
    {
        switch (kind)
        {
            case LogMessageKind.Error:
                return "[ERROR]";
            case LogMessageKind.Warning:
                return "[WARN]";
            case LogMessageKind.Command:
                return "[CMD]";
            default:
                return "[INFO]";
        }
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

    /// <summary>
    /// Recomputes everything the panel displays, once, and updates the labels and
    /// buttons from that single snapshot.
    /// </summary>
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

        HarnessStatusSnapshot snapshot = ComputeStatusSnapshot();
        if (snapshot.Installed && !IsConfiguredRoot())
            SaveConfiguredRoot(Root);
        ApplySnapshot(snapshot);
        lastSnapshot = snapshot;
        SetButtons(!busy, snapshot);
    }

    /// <summary>
    /// Reads the current state once. Every fact the labels and buttons need comes
    /// from here, so a refresh probes the port and the process a single time.
    /// </summary>
    private HarnessStatusSnapshot ComputeStatusSnapshot()
    {
        bool installed = IsInstalled();
        bool ready = installed && IsInstallationReady();
        bool portBusy = IsPortOpen(Port);
        int portPid = portBusy ? FindPortOwner(Port) : 0;
        bool running = portBusy && portPid > 0 && IsLikelyHarnessProcess(portPid);
        return new HarnessStatusSnapshot(
            installed,
            ready,
            portBusy,
            running,
            discoveredRoots.Count > 1,
            LocalVersion());
    }

    /// <summary>Applies a snapshot to the three state labels.</summary>
    private void ApplySnapshot(HarnessStatusSnapshot snapshot)
    {
        statusLabel.Text = snapshot.StatusText;
        runningLabel.Text = snapshot.RunningText;
        versionLabel.Text = snapshot.Installed && !snapshot.MultipleInstalls
            ? HarnessVersionText.ForDisplay(snapshot.Version)
            : "";

        // Tint each state field to match its meaning, the way the design shows a green
        // chip for a healthy state and a warning colour for a foreign port.
        TintState(statusLabel, snapshot.StatusText);
        TintState(runningLabel, snapshot.RunningText);

        try
        {
            trayIcon.Text = TrayClosePolicy.BuildTooltip(snapshot.Running, Port, snapshot.Version);
        }
        catch (Exception)
        {
            // A tooltip longer than the platform limit throws; the panel must survive it.
        }
    }

    /// <summary>
    /// Colours a state field from its own wording, so the labels and the colours can
    /// never disagree about what the state is.
    /// </summary>
    private static void TintState(UiValueChip chip, string text)
    {
        string value = text ?? "";
        if (value.IndexOf("多个", StringComparison.Ordinal) >= 0 ||
            value.IndexOf("不完整", StringComparison.Ordinal) >= 0)
        {
            chip.SetTone(UiStyle.WarningFill, UiStyle.Warning, UiStyle.WarningText);
            return;
        }
        if (value.IndexOf("占用", StringComparison.Ordinal) >= 0)
        {
            chip.SetTone(UiStyle.DangerFill, UiStyle.Danger, UiStyle.DangerText);
            return;
        }
        if (value.IndexOf("已安装", StringComparison.Ordinal) >= 0 ||
            value.IndexOf("正在运行", StringComparison.Ordinal) >= 0)
        {
            chip.SetTone(UiStyle.SuccessFill, UiStyle.Success, UiStyle.SuccessText);
            return;
        }
        // "未安装" and "未运行" are neutral, not failures.
        chip.SetTone(UiStyle.NeutralFill, UiStyle.Neutral, UiStyle.TextSecondary);
    }

    /// <summary>
    /// Polls the state so a crashed or stopped Harness is noticed without the user
    /// pressing anything. In-flight operations own the labels and buttons, so the
    /// poll stands down while busy and resumes from a fresh baseline afterwards.
    /// </summary>
    private void PollState()
    {
        if (busy)
            return;
        HarnessStatusSnapshot current = ComputeStatusSnapshot();
        string change = HarnessStatusChangePolicy.DescribeChange(Port, lastSnapshot, current);
        // Only touch the labels when something actually changed: re-applying an
        // unchanged snapshot every few seconds would overwrite the update-available
        // hint with the plain version.
        if (lastSnapshot == null || lastSnapshot.DiffersFrom(current))
        {
            ApplySnapshot(current);
            lastSnapshot = current;
        }
        SetButtons(true, current);
        if (!String.IsNullOrEmpty(change))
            Log(change);
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
            operationToken.ThrowIfCancellationRequested();
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

        // Delete exactly what the user confirmed, never a freshly recomputed list: the
        // targets were shown with their sizes and the user agreed to those.
        List<UninstallTarget> targets = pendingUninstallTargets ?? new List<UninstallTarget>();
        pendingUninstallTargets = null;
        if (targets.Count == 0)
        {
            Log("没有选中任何要删除的项目。");
            return;
        }

        var failures = new List<string>();
        foreach (UninstallTarget target in targets)
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

        // Only clear the configured root when the program tree itself was removed;
        // keeping user data means the install root may still hold it.
        bool removedProgram = false;
        foreach (UninstallTarget target in targets)
        {
            if (target.Kind == UninstallTargetKind.ProgramFiles)
                removedProgram = true;
        }
        if (removedProgram)
            pathBox.Text = "";

        Log("已删除所选内容。未勾选的项目保留在原处。");
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
            throw new InvalidOperationException("尚未安装 Harness，请先点击“安装”。");
        if (!IsInstallationReady())
            throw new InvalidOperationException("Harness 安装不完整，请点击“修复安装”恢复缺失的官方文件。");
        selectedSourceCommit = LocalCommit();
        await EnsureNodeAsync();
        LogProfileStartupHint();
        if (IsPortOpen(Port))
        {
            int owner = FindPortOwner(Port);
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
            throw new InvalidOperationException(Port + " 端口正被其他程序占用，请先释放端口后再启动 Harness。");
        }
        Stopwatch startupTime = Stopwatch.StartNew();
        TaskCompletionSource<string> webReady = new TaskCompletionSource<string>();
        ProcessStartInfo psi = await NewHarnessWebProcessAsync();
        server = new Process { StartInfo = psi, EnableRaisingEvents = true };
        server.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
        {
            if (String.IsNullOrEmpty(e.Data))
                return;
            string readyUrl = HarnessStartupPolicy.GetWebReadyUrl(e.Data, Port);
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
            bool portOpen = IsPortOpen(Port);
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
                string readyUrl = officialReadyLog ? webReady.Task.Result : HarnessPortPolicy.BuildLocalWebUri(Port);
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
            HarnessLifecyclePolicy.StartupFailureMessage(Port, portObserved, endpointObserved));
    }

    private async Task<bool> IsHarnessEndpointReadyAsync()
    {
        try
        {
            using (var cancellation = new CancellationTokenSource(HarnessLifecyclePolicy.EndpointProbeTimeoutMilliseconds))
            using (HttpResponseMessage response = await http.GetAsync(
                HarnessPortPolicy.BuildLocalWebUri(Port),
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
        string arguments = HarnessStartupPolicy.BuildWebArguments(Port);
        if (HarnessStartupPolicy.SelectLaunchMode(File.Exists(builtCli)) == HarnessLaunchMode.BuiltCli)
        {
            Log("启动器：使用已构建 CLI。");
            Log("监听端口: " + Port + "。");
            return NewNodeProcess(QuoteArgument(builtCli) + " " + arguments, Source);
        }
        Log("启动器：未找到已构建 CLI，使用兼容启动模式。");
        Log("监听端口: " + Port + "。");
        await PreparePnpmAsync();
        return NewPnpmProcess("dsh " + arguments, Source);
    }

    private async Task StopAsync()
    {
        int recordedPid = ParseInt(ReadStateValue("pid"));
        int portPid = FindPortOwner(Port);
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
            throw new InvalidOperationException(Port + " 端口由其他程序占用，管理器不会结束该进程。" + detail);
        }
        if (resolution.Kind == StopTargetKind.HarnessProcess)
        {
            int pid = resolution.ProcessId;
            RunTool("taskkill.exe", "/PID " + pid + " /T /F", Root);
            WriteState(ReadStateValue("commit"), "", "");
            for (int i = 0; i < HarnessLifecyclePolicy.StopWaitAttempts(
                HarnessLifecyclePolicy.StopTimeoutMilliseconds,
                HarnessLifecyclePolicy.PollIntervalMilliseconds) && IsPortOpen(Port); i++)
            {
                await Task.Delay(HarnessLifecyclePolicy.PollIntervalMilliseconds);
            }
            if (IsPortOpen(Port))
            {
                int remainingPid = FindPortOwner(Port);
                throw new InvalidOperationException("已等待 30 秒，但 " + Port + " 端口仍被占用。" +
                    (remainingPid > 0 ? "占用进程 PID: " + remainingPid + "。" : ""));
            }
            Log("已停止 Harness 进程树并释放 " + Port + " 端口。");
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
            throw new InvalidOperationException("尚未安装 Harness，请先点击“安装”。");
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
        operationToken.ThrowIfCancellationRequested();
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
        try
        {
            ZipFile.ExtractToDirectory(zip, extract);
            string extracted = null;
            foreach (string dir in Directory.GetDirectories(extract, "node-v*-win-x64"))
                extracted = dir;
            if (String.IsNullOrEmpty(extracted))
                throw new InvalidOperationException("Node.js 压缩包内容不符合预期。");
            if (Directory.Exists(Runtime)) DeleteDirectoryTree(Runtime);
            CopyDirectory(extracted, Path.Combine(runtimeRoot, "node"));
            selectedNodeDirectory = Runtime;
        }
        finally
        {
            // Without this the extraction directory is stranded whenever anything in
            // the block above fails, which is exactly the case a repair run hits.
            TryDeleteDirectory(extract);
        }
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
            // Stop before starting another attempt once the user has asked to cancel.
            operationToken.ThrowIfCancellationRequested();
            Log("> " + command + (attempt == 1 ? "" : "（第 2 次尝试）"));
            ProcessStartInfo psi = NewToolProcess(command, workingDirectory);
            ProcessExecutionResult result = await RunProcessDetailedAsync(psi);
            if (result.ExitCode == 0)
                return;

            // A process killed by the cancellation also returns non-zero; report the
            // cancellation rather than dressing it up as a build failure.
            operationToken.ThrowIfCancellationRequested();

            if (BuildRetryPolicy.ShouldRetry(command, attempt))
            {
                Log("构建第一次失败，正在自动重试；这通常是首次生成依赖或缓存并发造成的临时错误。");
                await Task.Delay(1000, operationToken);
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
        // Publish the handle so a cancellation can stop this tree rather than orphan it.
        runningToolProcess = process;
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
        runningToolProcess = null;
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

            // A killed download reports a non-zero exit; surface the cancellation as
            // such instead of blaming the network.
            operationToken.ThrowIfCancellationRequested();

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
        string configured = LoadSetting("installRoot");
        if (!String.IsNullOrEmpty(configured))
            return configured;
        return DefaultInstallRoot();
    }

    /// <summary>One value from the settings file, or an empty string when it is not there.</summary>
    private string LoadSetting(string key)
    {
        if (!File.Exists(SettingsFile))
            return "";
        return ReadJsonValue(File.ReadAllText(SettingsFile), key);
    }

    /// <summary>
    /// The fixed default port Harness is launched on and probed at.
    /// </summary>
    private int Port { get { return HarnessPortPolicy.DefaultPort; } }

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

    /// <summary>
    /// Writes one setting without discarding the others. The previous version replaced
    /// the whole file, so adding a second key would have been silently erased by the
    /// next install-root write.
    /// </summary>
    private void SaveSetting(string key, string value)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(SettingsFile))
        {
            string text = File.ReadAllText(SettingsFile);
            // Only the keys named here are carried over, so a new setting has to be added
            // to this list or the first write of any other key silently drops it.
            foreach (string preserved in new[] { "installRoot", "closeBehavior" })
            {
                string current = ReadJsonValue(text, preserved);
                if (!String.IsNullOrEmpty(current))
                    settings[preserved] = current;
            }
        }
        settings[key] = value ?? "";

        var builder = new StringBuilder();
        builder.Append("{");
        bool first = true;
        foreach (KeyValuePair<string, string> pair in settings)
        {
            if (!first)
                builder.Append(",");
            builder.Append("\"").Append(JsonEscape(pair.Key)).Append("\":\"")
                   .Append(JsonEscape(pair.Value)).Append("\"");
            first = false;
        }
        builder.Append("}");
        File.WriteAllText(SettingsFile, builder.ToString());
    }

    private void SaveConfiguredRoot(string root)
    {
        SaveSetting("installRoot", root);
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

    /// <summary>
    /// Whether the given process is Harness. Delegates the walk to the policy so the
    /// cost is measured and the parent traversal only runs when the process itself
    /// does not match, which removes the per-hop WMI storm the state refresh used to
    /// pay on every pass.
    /// </summary>
    private bool IsLikelyHarnessProcess(int pid)
    {
        HarnessIdentityResult result = HarnessProcessIdentityPolicy.Identify(
            pid,
            GetProcessCommandLine,
            GetParentProcessId);
        return result.IsHarness;
    }

    private string StoredWebUrl()
    {
        string value = StateSecretProtection.Unprotect(ReadStateValue("url"));
        return HarnessStartupPolicy.GetWebReadyUrl("dsh web: " + value, Port);
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
/// <summary>
/// Asks which removal targets to delete. WinForms has no checked-list dialog, so this
/// builds one: each target gets a checkbox, its path, and its measured size.
///
/// User data starts unchecked. That is a deliberate change from removing everything
/// unconditionally, because one stray click in a single confirmation dialog used to
/// destroy API keys and conversation history that no reinstall can restore.
/// </summary>
public sealed class UninstallSelectionForm : Form
{
    private readonly List<UninstallTarget> targets;
    private readonly List<CheckBox> boxes = new List<CheckBox>();
    private readonly Label warningLabel = new Label();

    public UninstallSelectionForm(List<UninstallTarget> targets)
    {
        this.targets = targets ?? new List<UninstallTarget>();

        Text = "选择要删除的内容";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Width = 720;
        Height = 420;
        MinimumSize = new Size(640, 360);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        ShowInTaskbar = false;

        BuildUi();
    }

    /// <summary>The targets the user chose. Empty when the dialog was cancelled.</summary>
    public List<UninstallTarget> SelectedTargets { get; private set; }

    private void BuildUi()
    {
        var main = new TableLayoutPanel();
        main.Dock = DockStyle.Fill;
        main.Padding = new Padding(14);
        main.ColumnCount = 1;
        main.RowCount = 3;
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        Controls.Add(main);

        var heading = new Label();
        heading.Dock = DockStyle.Fill;
        heading.Text = "勾选要删除的项目。" + Environment.NewLine +
            "未勾选的项目会被完整保留。";
        main.Controls.Add(heading, 0, 0);

        var list = new FlowLayoutPanel();
        list.Dock = DockStyle.Fill;
        list.FlowDirection = FlowDirection.TopDown;
        list.WrapContents = false;
        list.AutoScroll = true;
        foreach (UninstallTarget target in targets)
        {
            long bytes = UninstallSelectionPolicy.MeasureSizeBytes(target.Path);
            var box = new CheckBox();
            box.AutoSize = true;
            box.MaximumSize = new Size(650, 0);
            box.Checked = target.SelectedByDefault;
            box.Text = target.Description + "   (" + UninstallSelectionPolicy.DescribeSize(bytes) + ")" +
                Environment.NewLine + "  " + target.Path;
            box.Tag = target;
            box.CheckedChanged += delegate { UpdateWarning(); };
            boxes.Add(box);
            list.Controls.Add(box);
        }
        main.Controls.Add(list, 0, 1);

        var footer = new TableLayoutPanel();
        footer.Dock = DockStyle.Fill;
        footer.ColumnCount = 2;
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        warningLabel.Dock = DockStyle.Fill;
        warningLabel.ForeColor = Color.Firebrick;
        warningLabel.TextAlign = ContentAlignment.MiddleLeft;
        footer.Controls.Add(warningLabel, 0, 0);

        var buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Fill;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        var confirm = new Button { Text = "删除所选", AutoSize = true, Height = 30 };
        confirm.Click += ConfirmClick;
        var cancel = new Button { Text = "取消", AutoSize = true, Height = 30, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(confirm);
        buttons.Controls.Add(cancel);
        footer.Controls.Add(buttons, 1, 0);
        main.Controls.Add(footer, 0, 2);

        AcceptButton = confirm;
        CancelButton = cancel;
        UpdateWarning();
    }

    private bool IsSelected(UninstallTarget target)
    {
        foreach (CheckBox box in boxes)
        {
            var tagged = box.Tag as UninstallTarget;
            if (tagged == target)
                return box.Checked;
        }
        return false;
    }

    private int SelectedCount()
    {
        int count = 0;
        foreach (CheckBox box in boxes)
        {
            if (box.Checked)
                count++;
        }
        return count;
    }

    private void UpdateWarning()
    {
        if (SelectedCount() == 0)
        {
            warningLabel.Text = "没有勾选任何项目。";
            return;
        }

        bool removesUserData = false;
        foreach (UninstallTarget target in targets)
        {
            if (target.Kind == UninstallTargetKind.UserData && IsSelected(target))
                removesUserData = true;
        }
        warningLabel.Text = removesUserData
            ? "将删除用户数据：API Key、会话和附件无法找回。"
            : "删除后无法恢复。";
    }

    private void ConfirmClick(object sender, EventArgs e)
    {
        var selected = new List<UninstallTarget>();
        foreach (CheckBox box in boxes)
        {
            var target = box.Tag as UninstallTarget;
            if (target != null && box.Checked)
                selected.Add(target);
        }

        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请至少勾选一个要删除的项目，或点击“取消”。",
                "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // A second explicit confirmation for the irreversible case, on top of the
        // checkbox, because this is the only step that cannot be undone.
        bool removesUserData = false;
        foreach (UninstallTarget target in selected)
        {
            if (target.Kind == UninstallTargetKind.UserData)
                removesUserData = true;
        }
        if (removesUserData)
        {
            DialogResult answer = MessageBox.Show(
                this,
                "你将删除 Harness 用户数据。" + Environment.NewLine + Environment.NewLine +
                "其中的 API Key、会话记录和附件会被永久删除，重新安装 Harness 也无法找回。" + Environment.NewLine + Environment.NewLine +
                "确定继续吗？",
                "确认删除用户数据",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
                return;
        }

        SelectedTargets = selected;
        DialogResult = DialogResult.OK;
        Close();
    }
}

public static class Program
{
    /// <summary>Guards against a cascade of dialogs when faults repeat.</summary>
    private static int reported;

    /// <summary>
    /// Set once the CLR has announced a terminating fault. A modal dialog during
    /// shutdown can hang the process, so the second handler only writes a file.
    /// </summary>
    private static bool terminating;

    /// <summary>
    /// Process-wide single-instance gate. Held for the life of the process; the
    /// "Local\" prefix scopes it to the session so a second user can still run one.
    /// </summary>
    private const string MutexName = @"Local\DeepSeekHarnessControlPanel.SingleInstance";

    [STAThread]
    public static void Main()
    {
        // Install the safety net first: every later feature runs inside a panel that
        // must never disappear silently.
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        bool createdNew;
        using (var instanceGate = new Mutex(true, MutexName, out createdNew))
        {
            if (!createdNew)
            {
                ActivateRunningInstance();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ManagerForm());
        }
    }

    /// <summary>
    /// Brings an already-running panel to the front, then tells the user why this
    /// launch did not open a second window.
    /// </summary>
    private static void ActivateRunningInstance()
    {
        bool focused = TryFocusExistingPanel();
        MessageBox.Show(
            SingleInstancePolicy.BuildAlreadyRunningMessage(HarnessPortPolicy.DefaultPort),
            "DeepSeek Harness 控制面板",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        if (!focused)
        {
            // Nothing more to do, but never fail silently about it.
            WriteReport(
                Path.GetTempPath(),
                "单实例激活",
                new InvalidOperationException("未找到正在运行的控制面板窗口，无法自动切换。"));
        }
    }

    /// <summary>
    /// Finds the other instance's main window by class and title, then restores and
    /// focuses it. Uses the window enumeration API because the two processes share no
    /// IPC channel.
    /// </summary>
    private static bool TryFocusExistingPanel()
    {
        IntPtr found = IntPtr.Zero;
        try
        {
            NativeMethods.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                var className = new StringBuilder(256);
                NativeMethods.GetClassName(hWnd, className, className.Capacity);
                var title = new StringBuilder(512);
                NativeMethods.GetWindowText(hWnd, title, title.Capacity);

                NativeMethods.RECT rect;
                NativeMethods.GetWindowRect(hWnd, out rect);
                if (!SingleInstancePolicy.IsMainPanelWindow(
                        className.ToString(),
                        title.ToString(),
                        rect.Right - rect.Left,
                        rect.Bottom - rect.Top))
                    return true;

                found = hWnd;
                return false;
            }, IntPtr.Zero);
        }
        catch (Exception)
        {
            return false;
        }

        if (found == IntPtr.Zero)
            return false;

        try
        {
            if (NativeMethods.IsIconic(found))
                NativeMethods.ShowWindow(found, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(found);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Unhandled exception on the UI thread.</summary>
    private static void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
    {
        Report("UI 线程（Application.ThreadException）", e == null ? null : e.Exception, true);
    }

    /// <summary>Unhandled exception on a background thread, which terminates the process.</summary>
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        terminating = true;
        Report("后台线程（AppDomain.UnhandledException）", e == null ? null : e.ExceptionObject as Exception, true);
    }

    /// <summary>
    /// Writes the report next to the executable and, unless the process is already
    /// terminating, shows it. Returns the path when a log file was written.
    /// </summary>
    public static string Report(string source, Exception error, bool showDialog)
    {
        string directory = Path.GetDirectoryName(Application.ExecutablePath) ?? ".";
        string path = WriteReport(directory, source, error);

        if (showDialog && !terminating && System.Threading.Interlocked.Increment(ref reported) <= 3)
        {
            try
            {
                string report = UnexpectedErrorReport.Format(source, error);
                MessageBox.Show(
                    report + (String.IsNullOrEmpty(path) ? "" : Environment.NewLine + "已写入: " + path),
                    "DeepSeek Harness 控制面板 — 未预期的错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception)
            {
                // Nothing left to report with.
            }
        }
        return path;
    }

    /// <summary>
    /// Appends the report to <c>dsh-control-panel-error.log</c> in
    /// <paramref name="directory"/>, returning the path or an empty string when the
    /// write failed. The directory is a parameter so the behaviour is testable.
    /// </summary>
    public static string WriteReport(string directory, string source, Exception error)
    {
        if (String.IsNullOrWhiteSpace(directory))
            return "";
        string path = Path.Combine(directory, ErrorLogFileName);
        try
        {
            File.AppendAllText(
                path,
                "===== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " =====" + Environment.NewLine +
                UnexpectedErrorReport.Format(source, error) + Environment.NewLine,
                Encoding.UTF8);
            return path;
        }
        catch (Exception)
        {
            // The crash path must not throw.
            return "";
        }
    }

    public const string ErrorLogFileName = "dsh-control-panel-error.log";
}
