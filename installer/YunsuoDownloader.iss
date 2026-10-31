; ============================================================================
;  云梭下载器 —— Inno Setup 安装脚本
;  编译：  ISCC.exe installer\YunsuoDownloader.iss
;  产物：  artifacts\YunsuoDownloader-Setup-<版本>.exe
;
;  依赖：  artifacts\publish\ 里必须是 scripts\build.ps1 的发布产物
;          installer\ChineseSimplified.isl（简体中文语言包，随仓库分发）
;          installer\app.ico（由 installer\make_icon.py 生成）
; ============================================================================

#define AppName        "云梭下载器"
#define AppNameCN      "云梭下载器"
#define AppVersion     "1.1.0"
#define AppPublisher   "Yunsuo Downloader Project"
#define AppExeName     "YunsuoDownloader.exe"
#define SourceDir      "..\artifacts\publish"
#define InstallerDir   "."

[Setup]
; AppId 必须保持不变，升级安装才能识别为同一个软件
AppId={{9C4E1D72-3B5A-4F0E-9B7C-2A6D8E5F1C33}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/zxcfdcopud/Yunsuo-downloader
AppSupportURL=https://github.com/zxcfdcopud/Yunsuo-downloader
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
VersionInfoCompany={#AppPublisher}

; 固定按当前用户安装：双击 → 下一步 → 完成，不弹 UAC、也不弹"选择安装模式"。
; 如果要全机器安装（Program Files），把这两行改成：
;     PrivilegesRequired=admin
;     PrivilegesRequiredOverridesAllowed=
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes

; 系统要求：Windows 10 1809 及以上 / x64
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 检测正在运行的实例（与 App 里的单实例互斥体同名），避免覆盖被占用的文件
AppMutex=YunsuoDownloader.SingleInstance.v1
CloseApplications=yes
RestartApplications=no

; 安装前展示用户协议；安装前/后各一页说明
LicenseFile=..\docs\EULA.txt
InfoBeforeFile={#InstallerDir}\安装前必读.txt
InfoAfterFile={#InstallerDir}\安装后请看这里.txt

OutputDir=..\artifacts
OutputBaseFilename=YunsuoDownloader-Setup-{#AppVersion}
SetupIconFile={#InstallerDir}\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppNameCN} {#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
AllowUNCPath=no
; 安装包默认用简体中文，但保留英文以便非中文用户
; （ChineseSimplified.isl 来自 Inno Setup 官方源码仓库 Files/Languages/）

[Languages]
Name: "cn"; MessagesFile: "{#InstallerDir}\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："
Name: "startup";     Description: "开机自动启动（写入当前用户启动项，可随时在设置里关掉）"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
; 整个发布目录（含 tools 三件套、extension 浏览器插件、docs 文档）
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"

[Icons]
Name: "{autoprograms}\{#AppName}\{#AppName}";        Filename: "{app}\{#AppExeName}"; Comment: "启动下载器"
Name: "{autoprograms}\{#AppName}\浏览器插件目录";    Filename: "{app}\extension"; Comment: "chrome://extensions 里加载这个目录"
Name: "{autoprograms}\{#AppName}\使用手册";          Filename: "{app}\docs\使用手册.txt"
Name: "{autoprograms}\{#AppName}\用户协议";          Filename: "{app}\docs\EULA.txt"
Name: "{autoprograms}\{#AppName}\开源项目清单";      Filename: "{app}\docs\第三方开源项目清单.txt"
Name: "{autoprograms}\{#AppName}\卸载 {#AppName}";   Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";                    Filename: "{app}\{#AppExeName}"; Tasks: desktopicon; Comment: "启动下载器"

[Registry]
; 开机自启（仅当前用户，不需要管理员）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "YunsuoDownloader"; ValueData: """{app}\{#AppExeName}"" --tray"; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即运行 {#AppName}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\extension"; Description: "打开浏览器插件目录（待会儿要在 chrome://extensions 里加载它）"; \
    Flags: postinstall shellexec skipifsilent unchecked

[UninstallDelete]
; 只清理安装目录内的临时产物；用户数据（%AppData%\YunsuoDownloader）与下载的文件一律不动
Type: filesandordirs; Name: "{app}\logs"

[Messages]
; 自定义几处向导文案，让安装过程更像中文软件
cn.WelcomeLabel2=即将在您的电脑上安装 [name/ver]。%n%n本程序是本地下载工具：不提供、不托管任何受版权保护的内容，也不绕过任何付费或 DRM 保护。%n%n安装前请阅读并同意用户协议。
en.WelcomeLabel2=This will install [name/ver] on your computer.%n%n云梭下载器 is a local download tool. It does not host or provide any copyrighted content, and does not bypass paywalls or DRM.%n%nPlease read and accept the license agreement before installing.
cn.FinishedLabel=安装完成。%n%n接下来建议做两件事：%n  1) 在浏览器里打开 chrome://extensions，用「加载已解压的扩展程序」选择刚才那个 extension 目录；%n  2) 打开程序后到「设置 → 浏览器插件」复制连接令牌，填进插件设置里。%n%n使用手册在开始菜单的「{#AppName}」文件夹里。
en.FinishedLabel=Setup has finished installing [name].%n%nTwo recommended next steps:%n  1) Open chrome://extensions and load the "extension" folder via "Load unpacked";%n  2) In the app, copy the connection token from Settings -> Browser extension into the extension options page.%n%nThe user manual is in the Start Menu group "{#AppName}".
