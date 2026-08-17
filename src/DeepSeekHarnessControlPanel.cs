using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Management;
using Microsoft.Win32;

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
    private readonly TextBox logBox = new TextBox();
    private readonly Button installButton = new Button();
    private readonly Button startButton = new Button();
    private readonly Button restartButton = new Button();
    private readonly Button stopButton = new Button();
    private readonly Button updateButton = new Button();
    private readonly Button openButton = new Button();
    private readonly Button rescanButton = new Button();
    private readonly Button openFolderButton = new Button();
    private readonly HttpClient http = new HttpClient();
    private readonly object gate = new object();
    private bool busy;
    private Process server;
    private List<string> discoveredRoots = new List<string>();
    private string selectedNodeDirectory = "";
    private string selectedPnpm = "";
    private string selectedCorepack = "";

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
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
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
        buttons.WrapContents = false;
        buttons.AutoScroll = true;
        AddButton(buttons, installButton, "一键安装", InstallClick);
        AddButton(buttons, startButton, "启动", StartClick);
        AddButton(buttons, restartButton, "重启", RestartClick);
        AddButton(buttons, stopButton, "停止", StopClick);
        AddButton(buttons, updateButton, "检查 Harness 更新", UpdateClick);
        AddButton(buttons, openButton, "打开页面", OpenClick);
        AddButton(buttons, rescanButton, "重新扫描", RescanClick);
        AddButton(buttons, openFolderButton, "打开目录", OpenFolderClick);
        main.Controls.Add(buttons, 0, 4);

        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
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
        Process.Start("http://127.0.0.1:3080");
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
                    Log("失败: " + message);
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
        int portPid = FindPortOwner(3080);
        bool portBusy = IsPortOpen(3080);
        bool running = portBusy && portPid > 0 && IsLikelyHarnessProcess(portPid);
        bool multiple = discoveredRoots.Count > 1;
        installButton.Enabled = enabled && !installed && !multiple;
        startButton.Enabled = enabled && installed && !portBusy && !multiple;
        restartButton.Enabled = enabled && installed && running && !multiple;
        stopButton.Enabled = enabled && running && !multiple;
        updateButton.Enabled = enabled && installed && !multiple;
        openButton.Enabled = enabled && running;
        rescanButton.Enabled = enabled;
        openFolderButton.Enabled = enabled && Directory.Exists(Root);
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)delegate { Log(message); });
            return;
        }
        logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + StripAnsiSequences(message) + Environment.NewLine);
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
            Directory.CreateDirectory(Path.Combine(installRoot, "logs"));
            await EnsureNodeAsync();
            await PreparePnpmAsync();
            await RunToolAsync("pnpm install", "install");
            await RunToolAsync("pnpm run build", "build");
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

    private async Task StartAsync(bool openBrowser)
    {
        if (!IsInstalled())
            throw new InvalidOperationException("尚未安装 Harness，请先点击“一键安装”。");
        await EnsureNodeAsync();
        await PreparePnpmAsync();
        if (IsPortOpen(3080))
        {
            int owner = FindPortOwner(3080);
            if (owner > 0 && IsLikelyHarnessProcess(owner))
            {
                Log("Harness 已经在运行。");
                if (openBrowser)
                    Process.Start("http://127.0.0.1:3080");
                return;
            }
            throw new InvalidOperationException("3080 端口正被其他程序占用，请先释放端口后再启动 Harness。");
        }
        var psi = NewPnpmProcess("dsh web", Source);
        server = new Process { StartInfo = psi, EnableRaisingEvents = true };
        server.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (!String.IsNullOrEmpty(e.Data)) Log(e.Data); };
        server.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (!String.IsNullOrEmpty(e.Data)) Log(e.Data); };
        server.Start();
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        WriteState(ReadStateValue("commit"), server.Id.ToString());
        for (int i = 0; i < 60; i++)
        {
            if (IsPortOpen(3080))
            {
                Log(openBrowser ? "Harness 已启动，浏览器页面已打开。" : "Harness 已启动。");
                if (openBrowser)
                    Process.Start("http://127.0.0.1:3080");
                return;
            }
            if (server.HasExited)
                throw new InvalidOperationException("Harness 启动失败，进程已退出，退出码 " + server.ExitCode + "。请查看下方日志。");
            await Task.Delay(1000);
        }
        throw new InvalidOperationException("启动超时，请查看下方日志。");
    }

    private async Task StopAsync()
    {
        int pid = ParseInt(ReadStateValue("pid"));
        int portPid = FindPortOwner(3080);
        if (portPid > 0)
        {
            if (pid <= 0 || !IsProcessAlive(pid))
                pid = portPid;
        }
        if (pid > 0)
        {
            string commandLine = GetProcessCommandLine(pid);
            string detail = String.IsNullOrEmpty(commandLine) ? "" : Environment.NewLine + commandLine;
            if (portPid > 0 && !IsLikelyHarnessProcess(pid))
                throw new InvalidOperationException("3080 端口由其他程序占用，管理器不会结束该进程。" + detail);
            RunTool("taskkill.exe", "/PID " + pid + " /T /F", Root);
            WriteState(ReadStateValue("commit"), "");
            for (int i = 0; i < 20 && IsPortOpen(3080); i++)
                await Task.Delay(250);
            if (IsPortOpen(3080))
                throw new InvalidOperationException("进程已结束，但 3080 端口仍被占用。");
            Log("已停止 Harness 进程树并释放 3080 端口。");
        }
        else if (IsPortOpen(3080))
        {
            throw new InvalidOperationException("无法识别 3080 端口的占用进程。");
        }
        else
        {
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
        await ApplyUpdateAsync(remote);
    }

    private async Task ApplyUpdateAsync(string remote)
    {
        await StopAsync();
        string stage = Root + ".dsh-update";
        if (Directory.Exists(stage)) DeleteDirectoryTree(stage);
        string backup = Root + ".dsh-backup";
        if (Directory.Exists(backup)) DeleteDirectoryTree(backup);
        await DownloadSourceAsync(stage);
        Directory.Move(Root, backup);
        try
        {
            Directory.Move(stage, Root);
            CopyPersistentDirectory(backup, Root, ".dsh-runtime");
            CopyPersistentDirectory(backup, Root, "logs");
            await EnsureNodeAsync();
            await PreparePnpmAsync();
            await RunToolAsync("pnpm install", "update-install");
            await RunToolAsync("pnpm run build", "update-build");
            WriteState(remote);
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
        await DownloadFileAsync(String.Format(RepoZipTemplate, Uri.EscapeDataString(branch)), zip);
        string extract = Path.Combine(Path.GetTempPath(), "dsh-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extract);
        ZipFile.ExtractToDirectory(zip, extract);
        string root = Directory.GetDirectories(extract)[0];
        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        CopyDirectory(root, destination);
        Directory.Delete(extract, true);
        return await GetRemoteCommitAsync(branch);
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
            DeleteDirectoryTreeWithRobocopy(path);
        }
        catch (UnauthorizedAccessException)
        {
            DeleteDirectoryTreeWithRobocopy(path);
        }
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

    private void DeleteDirectoryTreeWithRobocopy(string path)
    {
        string empty = Path.Combine(Path.GetTempPath(), "dsh-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var psi = NewProcess("robocopy.exe", QuoteArgument(empty) + " " + QuoteArgument(path) + " /MIR /NFL /NDL /NJH /NJS /NP /R:0 /W:0", Path.GetDirectoryName(path), false);
            int exitCode;
            using (var process = Process.Start(psi))
            {
                process.WaitForExit();
                exitCode = process.ExitCode;
            }
            if (exitCode > 7)
                throw new InvalidOperationException("清理长路径目录失败，Robocopy 退出码 " + exitCode + "。");
            Directory.Delete(path, false);
        }
        finally
        {
            if (Directory.Exists(empty))
                Directory.Delete(empty, false);
        }
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
        Log("> " + command);
        string[] parts = command.Split(new[] { ' ' }, 2);
        ProcessStartInfo psi;
        if (parts[0] == "corepack")
        {
            string corepack = FindExecutable("corepack.cmd");
            if (String.IsNullOrEmpty(corepack))
                throw new InvalidOperationException("未找到 Corepack。");
            psi = NewProcess(corepack, parts[1], workingDirectory);
        }
        else
        {
            psi = NewPnpmProcess(command.Substring(5), workingDirectory);
        }
        int code = await RunProcessAsync(psi);
        if (code != 0)
            throw new InvalidOperationException(command + " 失败，退出码 " + code + "。");
    }

    private async Task<int> RunProcessAsync(ProcessStartInfo psi)
    {
        var process = new Process { StartInfo = psi };
        process.Start();
        Task output = Task.Run(async delegate
        {
            string line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null) Log(line);
        });
        Task error = Task.Run(async delegate
        {
            string line;
            while ((line = await process.StandardError.ReadLineAsync()) != null) Log(line);
        });
        await Task.Run(delegate { process.WaitForExit(); });
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }

    private ProcessStartInfo NewPnpmProcess(string args, string workingDirectory)
    {
        if (!String.IsNullOrEmpty(selectedPnpm))
            return NewProcess(selectedPnpm, args, workingDirectory);
        if (!String.IsNullOrEmpty(selectedCorepack))
            return NewProcess(selectedCorepack, "pnpm " + args, workingDirectory);
        throw new InvalidOperationException("未找到可用的 Node.js、Corepack 或 pnpm 运行环境。");
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
        int managedPid = ParseInt(ReadStateValue("pid"));
        int current = pid;
        for (int depth = 0; depth < 8 && current > 0; depth++)
        {
            if (managedPid > 0 && current == managedPid)
                return true;
            string commandLine = GetProcessCommandLine(current).ToLowerInvariant();
            if (commandLine.Contains("deepseek-harness") ||
                (commandLine.Contains("apps/cli/src/bin.ts") && commandLine.Contains("web")) ||
                (commandLine.Contains("apps\\cli\\src\\bin.ts") && commandLine.Contains("web")) ||
                (commandLine.Contains("dsh") && commandLine.Contains("web")))
                return true;
            int parent = GetParentProcessId(current);
            if (parent == current)
                break;
            current = parent;
        }
        return false;
    }

    private void WriteState(string commit, string pid = null)
    {
        Directory.CreateDirectory(Root);
        string json = "{\"commit\":\"" + (commit ?? "") + "\",\"pid\":\"" + (pid ?? ReadStateValue("pid")) + "\",\"updated\":\"" + DateTime.UtcNow.ToString("o") + "\"}";
        File.WriteAllText(StateFile, json);
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
