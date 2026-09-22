namespace Pulse.App;

/// <summary>
/// UI copy for the rail, tray, hover card, settings and spend windows.
/// Chinese is the product default; English is the alternate. Numeric figures
/// stay as numbers — only the words around them change.
/// </summary>
public static class Ui
{
    private static bool Zh => UiLanguage.IsChinese;

    // --- tray ---
    public static string ShowRail => Zh ? "显示工具条" : "Show rail";
    public static string TokenSpendMenu => Zh ? "Token 消耗" : "Token Spend";
    public static string SettingsMenu => Zh ? "设置" : "Settings";
    public static string ExitMenu => Zh ? "退出" : "Exit";
    public static string UpdateAvailable(string tag) => Zh
        ? $"有新版本 — {tag}"
        : $"Update available — {tag}";
    public static string AlreadyRunning => Zh
        ? "Pulse 已在运行。请查看通知区域。"
        : "Pulse is already running. Check the notification area.";
    public static string UpdateReady(string tag) => Zh
        ? $"{tag} 已可用"
        : $"{tag} is available";
    public static string UpToDate => Zh ? "当前已是最新版本。" : "You are up to date.";
    public static string UpdateCheckFailed(string message) => Zh
        ? $"检查更新失败：{message}"
        : $"Update check failed: {message}";

    // --- rail figures ---
    public static string Spent => Zh ? "已用尽" : "Spent";
    public static string PercentLeft(double pct) => Zh
        ? $"剩 {pct}%"
        : $"{pct}% left";
    public static string PercentUsed(double pct) => Zh
        ? $"{pct}%"
        : $"{pct}%";
    public static string NoFigure => "—";

    // --- hover card ---
    public static string UsageTitle(string name) => Zh
        ? $"{name} 用量"
        : $"{name} Usage";
    public static string NoReading => Zh ? "暂无读数" : "No reading";
    public static string CachedReading => Zh ? "缓存读数" : "Cached reading";
    public static string UsedLine(double pct) => Zh
        ? $"已用 {pct}%"
        : $"{pct}% used";
    public static string UsedLeftLine(double used, double left) => Zh
        ? $"已用 {used}% · 剩 {left}%"
        : $"{used}% used · {left}% left";
    public static string ResetsToday(string time) => Zh
        ? $"重置于 {time}"
        : $"Resets {time}";
    public static string ResetsTomorrow(string time) => Zh
        ? $"明日 {time} 重置"
        : $"Resets tomorrow {time}";
    public static string ResetsOn(string when) => Zh
        ? $"重置于 {when}"
        : $"Resets {when}";
    public static string RunsOutInMinutes(int minutes) => Zh
        ? $"约 {minutes} 分钟后耗尽"
        : $"Runs out in {minutes} min";
    public static string WontLastWindow => Zh
        ? "撑不过本窗口"
        : "Won't last the window";

    public static string LimitFiveHour => Zh ? "5 小时限额" : "5-hour limit";
    public static string LimitWeekly => Zh ? "每周限额" : "Weekly limit";
    public static string LimitMonthly => Zh ? "每月限额" : "Monthly limit";
    public static string LimitDaily => Zh ? "每日限额" : "Daily limit";
    public static string LimitSpend => Zh ? "消费限额" : "Spend limit";
    public static string LimitBalance => Zh ? "余额" : "Balance";
    public static string LimitMessages => Zh ? "消息额度" : "Messages";
    public static string LimitOther => Zh ? "限额" : "Limit";

    // --- health ---
    public static string CredentialRefused => Zh ? "凭据被拒绝" : "Credential refused";
    public static string NotConfigured => Zh ? "未配置" : "Not configured";
    public static string RateLimited => Zh ? "请求受限" : "Rate limited";
    public static string RouteChanged => Zh ? "接口已变" : "Route changed";
    public static string Unavailable => Zh ? "不可用" : "Unavailable";

    // --- settings chrome ---
    public static string WindowTitle => Zh ? "Pulse 设置" : "Pulse";
    public static string BrandCaption => Zh ? "本机用量" : "Usage on this PC";
    public static string RailNavLabel => Zh ? "工具条" : "Rail";
    public static string RailNavHint => Zh
        ? "每个已启用的 Provider 一圈。颜色表示离限额多近。"
        : "A ring for each provider that is on. The colour is how close the limit is.";
    public static string RailNavHint2 => Zh
        ? "右键工具条可打开本窗口、Token 消耗或退出。"
        : "Right-click the rail for this window, token spend, or to quit.";
    public static string ProvidersTitle => Zh ? "服务" : "Providers";
    public static string ProvidersHint => Zh
        ? "关掉的服务会离开工具条。仅当 Pulse 找不到本地登录时才需要密钥。"
        : "Turn one off and it leaves the rail. A key is only needed when Pulse cannot find a local login.";
    public static string StatusLineTitle => Zh ? "Claude Code 状态栏" : "Claude Code status line";
    public static string VaultNote => Zh
        ? "已保存的密钥仅存于本机，按你的 Windows 用户加密。"
        : "Saved keys stay on this PC, encrypted for your Windows user.";
    public static string Close => Zh ? "关闭" : "Close";
    public static string Enable => Zh ? "启用" : "Enable";
    public static string Disable => Zh ? "禁用" : "Disable";
    public static string SectionAccounts => Zh ? "账号与密钥" : "Accounts & keys";
    public static string SectionMoreAccounts => Zh ? "更多账号" : "More accounts";
    public static string SectionGeneral => Zh ? "通用" : "General";
    public static string MoreAccountsHint => Zh
        ? "Claude、Codex、Grok、Grok Bot、Antigravity 可在现有登录之外再加一个账号。Antigravity 添加的是另一路 language server 连接（端口 + CSRF），不是 OAuth 登录。"
        : "Claude, Codex, Grok, Grok Bot, and Antigravity can each keep another login beside the one already in use. Antigravity adds a second language-server connection (port + CSRF), not an OAuth sign-in.";
    public static string LaunchAtStartup => Zh ? "开机时启动 Pulse" : "Launch Pulse at startup";
    public static string AlertsLabel => Zh ? "提醒" : "Alerts";
    public static string AlertOff => Zh ? "关闭" : "Off";
    public static string Alert80 => Zh ? "用量达 80%" : "At 80% used";
    public static string Alert95 => Zh ? "用量达 95%" : "At 95% used";
    public static string ReadSpend => Zh ? "从本地日志读取 Token 消耗" : "Read token spend from local logs";
    public static string GlobalHotkey => Zh
        ? "全局快捷键 Ctrl+Alt+P 显示/隐藏工具条"
        : "Global hotkey Ctrl+Alt+P toggles the rail";
    public static string SignInCopilot => Zh ? "登录 GitHub Copilot" : "Sign in to GitHub Copilot";
    public static string LanguageLabel => Zh ? "界面语言" : "Language";
    public static string ThemeLabel => Zh ? "外观" : "Theme";
    public static string ThemeLight => Zh ? "浅色" : "Light";
    public static string ThemeDark => Zh ? "深色" : "Dark";
    public static string ThemeSystem => Zh ? "跟随系统" : "System";
    public static string NetworkLabel => Zh ? "网络" : "Network";
    public static string FollowSystem => Zh ? "跟随系统" : "Follow system";
    public static string ManualProxy => Zh ? "手动代理" : "Manual proxy";
    public static string SaveProxy => Zh ? "保存代理" : "Save proxy";
    public static string ProxyHint => Zh
        ? "主机与端口需一并保存。主机为空或端口不在 1–65535 时保留原配置。"
        : "Host and port save together. Empty host or port outside 1–65535 keeps the previous endpoint.";
    public static string ProxySavedEndpoint(string host, int port) => Zh
        ? $"已保存：{host}:{port}"
        : $"Saved: {host}:{port}";
    public static string ProxySavedSystem => Zh ? "已保存：跟随系统" : "Saved: follow system.";
    public static string ProxyPortInvalid => Zh
        ? "端口须为 1–65535 的整数。已保留原配置。"
        : "Port must be a whole number from 1 to 65535. Previous endpoint kept.";
    public static string ProxyHostRequired => Zh
        ? "手动代理需要主机名。已保留原配置。"
        : "Host is required for a manual proxy. Previous endpoint kept.";
    public static string VersionLabel => Zh ? "版本" : "Version";
    public static string CheckForUpdates => Zh ? "检查更新" : "Check for updates";
    public static string SaveCredential => Zh ? "保存" : "Save";
    public static string AddAccount => Zh ? "添加账号" : "Add account";
    public static string RenameAccount => Zh ? "重命名" : "Rename";
    public static string SignInAgain => Zh ? "重新登录" : "Sign in again";
    public static string RemoveAccount => Zh ? "移除" : "Remove";
    public static string Cancel => Zh ? "取消" : "Cancel";
    public static string Ok => Zh ? "确定" : "OK";
    public static string Add => Zh ? "添加" : "Add";
    public static string PrimaryOnly => Zh ? "1 个账号（主）" : "1 account (primary)";
    public static string PrimaryPlus(int n) => Zh
        ? $"主账号 + {n} 个附加"
        : $"primary + {n} added";
    public static string CredentialSaved(string name) => Zh
        ? $"{name} 凭据已保存。"
        : $"{name} credential saved.";
    public static string SignInFailed(string message) => Zh
        ? $"登录失败：{message}"
        : $"Sign-in failed: {message}";
    public static string SignInTimeout => Zh
        ? "登录未在时限内完成。"
        : "Sign-in did not complete in time.";
    public static string SignInBusy => Zh
        ? "已有登录在进行。"
        : "Another sign-in is already running.";
    public static string RemoveConfirm(string label) => Zh
        ? $"移除「{label}」？其凭据将从保险库删除。"
        : $"Remove “{label}”? Its credential is deleted from the vault.";
    public static string AccountRemoved => Zh
        ? "该账号已删除，未写入任何内容。"
        : "That account was removed; nothing was written.";
    public static string RenameTitle => Zh ? "重命名账号" : "Rename account";
    public static string LabelField => Zh ? "名称" : "Label";
    public static string AntigravityAddTitle => Zh ? "添加 Antigravity 账号" : "Add Antigravity account";
    public static string AntigravityHint => Zh
        ? "Antigravity 配额仅在对应登录的 language server 运行时可用。请粘贴另一实例的端口与 CSRF token（命令行 --csrf_token）。"
        : "Antigravity quota is only available while that login's language server is running. Paste the port(s) and CSRF token from the other Antigravity instance (command line flag --csrf_token).";
    public static string PortsField => Zh ? "端口（逗号分隔）" : "Port(s), comma-separated";
    public static string CsrfField => Zh ? "CSRF token" : "CSRF token";

    // --- token spend ---
    public static string SpendTitle => Zh ? "Token 消耗" : "Token spend";
    public static string SpendSubtitle => Zh ? "每个数据源一行" : "One row per source";
    public static string EmptyRail => Zh ? "启用服务后在此显示用量" : "Enable a provider to see usage";
    public static string EmptySpend => Zh ? "暂无本地用量记录" : "No local usage records";
    public static string Loading => Zh ? "加载中…" : "Loading…";
}
