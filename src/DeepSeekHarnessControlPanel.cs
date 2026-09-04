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

    public static string BuildDependencyInstallCommand()
    {
        return "pnpm install " + FrozenLockfileSwitch;
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

    public static string BuildInstallFailureHint(string command, string output, bool fsExtDeclaredInLockfile)
    {
        if (!Regex.IsMatch(command ?? "", "\\bpnpm\\s+install\\b", RegexOptions.IgnoreCase))
            return "";
        string text = output ?? "";
        if (text.IndexOf("node-gyp", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("fs-ext", StringComparison.OrdinalIgnoreCase) < 0)
            return "";
        if (text.IndexOf("fs-ext", StringComparison.OrdinalIgnoreCase) >= 0 && !fsExtDeclaredInLockfile)
            return "依赖安装触发了 fs-ext 原生模块编译，但官方锁文件没有声明 fs-ext。已停止本次安装；更新流程会保留旧版本并自动回滚，请不要安装 Visual Studio，先查看完整日志。";
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
    private readonly HttpClient http = new HttpClient();
    private readonly object gate = new object();
    private bool busy;
    private Process server;
    private List<string> discoveredRoots = new List<string>();
    private string selectedNodeDirectory = "";
    private string selectedPnpm = "";
    private string selectedCorepack = "";
    private string selectedSourceCommit = "";

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
            await EnsureNodeAsync();
            await PreparePnpmAsync();
            await RunToolAsync(HarnessInstallPolicy.BuildDependencyInstallCommand(), "install");
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
            await EnsureNodeAsync();
            await PreparePnpmAsync();
            await RunToolAsync(HarnessInstallPolicy.BuildDependencyInstallCommand(), "update-install");
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
        string json = await http.GetStringAsync(NodeIndex);
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
        await DownloadFileAsync(url, zip);
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
            await DownloadFileAsync(String.Format(RepoZipTemplate, Uri.EscapeDataString(branch)), zip);
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
            throw new InvalidOperationException("官方源码缺少 pnpm-lock.yaml，已停止更新以避免安装未锁定的依赖。");

        string[] bundledDependencies = Directory.GetDirectories(root, "node_modules", SearchOption.AllDirectories);
        if (bundledDependencies.Length > 0)
            throw new InvalidOperationException("官方源码包中包含预装 node_modules，已停止更新以避免混入非官方依赖。");
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

    private async Task DownloadFileAsync(string url, string path)
    {
        try
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
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("无法建立 HTTPS 连接，请检查网络代理、证书或防火墙设置。" + Environment.NewLine + FlattenException(ex));
        }
    }

    private async Task<string> GetDefaultBranchAsync()
    {
        string json;
        try
        {
            using (var response = await http.GetAsync(RepoInfoApi))
            {
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("GitHub 返回 " + (int)response.StatusCode + " " + response.ReasonPhrase + "。");
                json = await response.Content.ReadAsStringAsync();
            }
        }
        catch (HttpRequestException ex)
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
            using (var response = await http.GetAsync(String.Format(RepoApiTemplate, Uri.EscapeDataString(branch))))
            {
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("GitHub 返回 " + (int)response.StatusCode + " " + response.ReasonPhrase + "。");
                json = await response.Content.ReadAsStringAsync();
            }
        }
        catch (HttpRequestException ex)
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
