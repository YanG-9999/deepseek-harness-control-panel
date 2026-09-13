# DeepSeek Harness 控制面板

用于 Windows 10/11 x64 的 DeepSeek Harness 管理工具。它可检测本机安装、安装官方 Harness、启动、重启、停止，以及检查 Harness 更新。

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

## 说明

此工具通过官方 GitHub 仓库下载 DeepSeek Harness，并使用本机的 Node.js/pnpm；缺失时会在 Harness 安装目录准备私有运行环境。它不是 DeepSeek 官方发布的桌面客户端。

### 网络传输

面板默认使用 .NET Framework 的 `HttpClient`。在某些代理配置下（例如 TUN + fake-IP 把所有域名解析到 `198.18.0.0/15`），.NET 的 SCHANNEL 无法完成 TLS 握手，报“未能创建 SSL/TLS 安全通道”，而 Node 的 OpenSSL 栈可以正常连接。此时面板会自动改用内置的 Node 取回脚本继续请求，并在日志中说明切换原因。该回退是被动的：.NET 正常工作时不会启用，也不会在 `%TEMP%` 留下任何文件。

本地 Harness 页面（`127.0.0.1:3080`）的探测始终走 .NET，不受影响。
