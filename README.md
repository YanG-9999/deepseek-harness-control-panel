# DeepSeek Harness 控制面板

用于 Windows 10/11 x64 的 DeepSeek Harness 管理工具。它可检测本机安装、安装官方 Harness、启动、重启、停止、检查 Harness 更新，以及管理 profile 插件。

## 包含内容

- `src/DeepSeekHarnessControlPanel.cs`：Windows Forms 源码
- `assets/DeepSeekHarness.ico`：蓝鲸程序图标
- `scripts/build.ps1`：本地构建脚本
- `scripts/test.ps1`：策略层测试脚本
- `tests/`：策略类测试（纯逻辑，不依赖网络与 Harness 安装）

本仓库不包含 API Key、用户配置、Harness 安装目录、依赖缓存或测试日志。

## 构建

在 Windows PowerShell 中运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

生成的程序位于 `bin\DeepSeekHarnessControlPanel.exe`。

面板正在运行时 `bin\DeepSeekHarnessControlPanel.exe` 会被占用，此时重建会以 `CS1567` 失败。用 `-OutputDirectory` 把产物输出到别处即可在不关闭面板的情况下编译：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 -OutputDirectory .\bin-staging
```

`scripts\test.ps1` 接受同样的参数：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1 -OutputDirectory .\bin-staging
```

## 使用

运行生成的控制面板后：

1. 在未安装 Harness 的电脑上点击“一键安装”并选择空目录。
2. 安装完成后使用“启动”“重启”“停止”控制服务。
3. “检查 Harness 更新”仅检查官方 `deepseek-ai/deepseek-harness` 仓库的更新，不更新此控制面板。
4. “插件市场”管理该 Harness 的 profile 插件（见下）。

## 插件市场

“插件市场”窗口把 `dsh plugin` 包上一层图形界面，用于搜索、安装和卸载插件。它**不自己实现安装逻辑**：所有变更都通过 `dsh plugin --profile web <add|remove> <spec>` 完成，由 dsh CLI 负责维护 profile 的组合包层列表。

### 插件是什么

插件是一个在 `package.json` 中声明了 `dsh.bundle` 的 npm 包。安装后它会被加入 `$DSH_HOME/profiles/web/package.json` 的 `dsh.profile.bundles` 层列表，并贡献它自己的 `cordis.patch.yml` 配置层。

### 窗口里的三块内容

- **精选清单**：仓库内人工确认过的插件（官方 provider 与已验证的第三方）。带搜索框，输入即过滤。
- **GitHub 仓库搜索**：勾选“在 GitHub 搜索仓库”后按关键词检索。默认关键词 `"dsh.bundle"` 命中的是声明了组合包清单字段的仓库，也就是可安装的那一批。搜索结果显示的仓库是**未经审核**的第三方代码。
- **详情面板**：显示所选条目的 spec、来源、当前安装状态，以及安装风险说明。

### 内置组合包与外部组合包

在 `dsh.profile.bundles` 中出现、但不在 `dependencies` 中的条目是**内置组合包**，由 dsh 安装目录本身提供（例如 `@deepseek-ai/dsh-base`），pnpm 并不拥有它们，因此市场不会提供卸载。市场会明确标注每一项属于哪一类。

### 装完要重启

组合包成员的变化只在 Harness 启动时生效。安装或卸载后需要回到主窗口点击“重启”。如果插件提供工具，还需要在 agent preset 中单独启用对应工具行，agent 才能看到它。

### 安装来源与风险

市场接受四种 spec，风险不同：

| 来源 | 示例 | 安装时会执行构建脚本吗 |
| --- | --- | --- |
| npm 包 | `dsh-knowledge` | 否（安装的是已构建内容） |
| Git 仓库 | `github:owner/repo` | **是**，且不在任何沙箱内 |
| 本地目录 | `.\my-plugin` | 否 |
| 本地压缩包 | `.\plugin-0.1.0.tgz` | 否 |

对 Git 来源，pnpm ≥10 会先阻止其 `prepare` 脚本，`dsh` 会打印需要加入 profile 的 `pnpm-workspace.yaml` 中 `allowBuilds` 的包键。市场的日志区会重复这条提示。**这项授权等于允许该包代码在你的机器上执行**，请只对可信来源授权，并尽量在 spec 上锁定 commit。

## 说明

此工具通过官方 GitHub 仓库下载 DeepSeek Harness，并使用本机的 Node.js/pnpm；缺失时会在 Harness 安装目录准备私有运行环境。它不是 DeepSeek 官方发布的桌面客户端。

### 网络传输

面板默认使用 .NET Framework 的 `HttpClient`。在某些代理配置下（例如 TUN + fake-IP 把所有域名解析到 `198.18.0.0/15`），.NET 的 SCHANNEL 无法完成 TLS 握手，报“未能创建 SSL/TLS 安全通道”，而 Node 的 OpenSSL 栈可以正常连接。此时面板会自动改用内置的 Node 取回脚本继续请求，并在日志中说明切换原因。该回退是被动的：.NET 正常工作时不会启用，也不会在 `%TEMP%` 留下任何文件。

只有本地 Harness 页面（`127.0.0.1:3080`）的探测始终走 .NET，不受影响。

