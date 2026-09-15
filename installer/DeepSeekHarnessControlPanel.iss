; DeepSeek Harness 控制面板 — Inno Setup 安装脚本
;
; 目标：免 UAC 的 per-user 安装。安装到 %LOCALAPPDATA%\Programs\... ，因此整个流程
; 不需要管理员权限，也不会弹 UAC。
;
; 编译（需要 Inno Setup 6 的 ISCC.exe）：
;   ISCC.exe /DAppVersion=0.1.0 installer\DeepSeekHarnessControlPanel.iss
; 或直接运行 scripts\build-installer.ps1，它会从源码读出版本号再调用 ISCC。

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "DeepSeek Harness 控制面板"
#define AppPublisher "DeepSeek Harness"
#define AppExeName "DeepSeekHarnessControlPanel.exe"
; AppId 一旦发布就不能改：它决定升级时能否识别出旧版本。
; 开头的 { 必须写成 {{：单个 { 会被 Inno 当成参数展开（如 {app}），而不是 AppId 的一部分。
; 结尾的 } 不需要再转义，多写一个会原样进入注册表键名（卸载项显示为 ...}}_is1）。
#define AppId "{{8F3C1D42-7B6A-4E21-9C4D-2A5E7F1B0C93}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} 安装程序

; per-user 安装，永不提权。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={localappdata}\Programs\DeepSeekHarnessControlPanel
DisableDirPage=no
DisableProgramGroupPage=yes
AllowNoIcons=yes

; 只允许安装到当前用户可见的位置。
DefaultGroupName=DeepSeek Harness
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

OutputDir=..\bin\installer
OutputBaseFilename=DeepSeekHarnessControlPanel-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 安装包自身也带产品图标：默认的 Inno 图标会让"这是个什么程序"变得含糊。
SetupIconFile=..\assets\DeepSeekHarness.ico

; 面板持有这个互斥量时提示用户先退出，避免覆盖正在运行的程序文件。
; 名字必须与源码里的 Program.MutexName 一致。
AppMutex=Local\DeepSeekHarnessControlPanel.SingleInstance
SetupMutex=Local\DeepSeekHarnessControlPanel.Setup

[Languages]
; Inno Setup 6 不自带简体中文，Default.isl 是英文，所以向导正文是英文。
; 要显示中文，需要把社区翻译 ChineseSimplified.isl 放进 installer\ 再指向它。
; 这里的中文只覆盖本脚本自带的说明与提示（见 [Tasks] 和 [Code]）。
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; 默认不勾选：未经同意就自启容易招人反感，也容易被安全软件针对。
Name: "autostart"; Description: "开机时自动启动控制面板"; GroupDescription: "附加选项:"; Flags: unchecked
; 桌面快捷方式同样默认不勾选。这条任务必须存在：[Icons] 里的桌面项引用了它，
; 缺了它整个脚本无法编译（"unknown task"）。
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项:"; Flags: unchecked

[Files]
Source: "..\bin\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; No isreadme flag: Inno displays such a file with ShellExecute, and a machine with no
; association for .md answers that with the Windows "choose an app" chooser - which the
; user then blames on the panel, because the postinstall entry launches it at the same
; moment. The readme is still installed; [Run] below offers it through Notepad instead.
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; 只写当前用户的启动项，不碰 HKLM，因此整个安装流程不需要管理员权限。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "DeepSeekHarnessControlPanel"; ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent
; Notepad, not the file's association: this must work on a machine where .md opens nothing,
; and it stays unchecked so nobody gets a document they did not ask for.
Filename: "notepad.exe"; Parameters: """{app}\README.md"""; Description: "查看使用说明"; \
    Flags: postinstall nowait skipifsilent unchecked

[UninstallDelete]
; 只清理安装程序自己创建的目录，且仅当它为空时才真的删除。
; 注意这里没有、也永远不该有 Harness 的任何路径。
Type: dirifempty; Name: "{app}"
Type: dirifempty; Name: "{localappdata}\Programs\DeepSeekHarnessControlPanel"

[Code]
// ---------------------------------------------------------------------------
// 卸载边界（重要）
//
// 这个安装包只负责控制面板自身。以下内容一律不删、不碰：
//   * Harness 安装目录（例如 C:\dsh）及其 .dsh-runtime、logs
//   * %USERPROFILE%\.dsh —— 里面有 API Key、会话记录和附件，删掉无法恢复
//   * Harness 安装目录内的 .dsh-manager-state.json
//
// 面板内的"彻底卸载"按钮才是删除 Harness 的地方，而且它现在默认不勾选用户数据。
// 安装包的卸载器与它必须严格分开，否则用户卸载一个几百 KB 的面板时，
// 会连带丢掉无法恢复的凭据和会话。
// ---------------------------------------------------------------------------

// 卸载时确认用户明白卸载范围。
function InitializeUninstall(): Boolean;
begin
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // 卸载完成后提示 Data 未被触碰，避免用户以为 Harness 也一起删了。
    if not UninstallSilent then
      MsgBox('控制面板已卸载。' + #13#10 + #13#10 +
             'DeepSeek Harness 本身及其用户数据（配置、API Key、会话、附件）' + #13#10 +
             '没有被删除，仍保留在原处。' + #13#10 + #13#10 +
             '如需删除 Harness，请重新安装控制面板并使用其中的“彻底卸载”。',
             mbInformation, MB_OK);
  end;
end;
