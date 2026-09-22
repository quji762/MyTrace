namespace Pulse.App;

/// <summary>
/// UI copy. Chinese is the product default; English is the alternate. Code-built
/// surfaces (rail, hover card, tray) read these; XAML windows set the same keys
/// in their loaded handler so one source of truth stays in code.
/// </summary>
public static class UiStrings
{
    private static bool Zh => UiLanguage.IsChinese;

    // --- tray ---
    public static string ShowRail => Zh ? "显示工具条" : "Show rail";
    public static string TokenSpend => Zh ? "Token 消耗" : "Token Spend";
    public static string Settings => Zh ? "设置" : "Settings";
    public static string Exit => Zh ? "退出" : "Exit";
    public static string UpdateAvailable(string tag) =>
        Zh ? $"有新版本 — {tag}" : $"Update available — {tag}";
    public static string AlreadyRunning => Zh
        ? "Pulse 已在运行。请查看通知区域。"
        : "Pulse is already running. Check the notification area.";
    public static string UpdateReady(string tag) => Zh
        ? $"{tag} 已发布"
        : $"{tag} is available";
    public static string UpToDate => Zh ? "已是最新版本。" : "You are up to date.";
    public static string UpdateCheckFailed(string message) => Zh
        ? $"检查更新失败：{message}"
        : $"Update check failed: {message}";

    // --- rail figures ---
    public static string Spent => Zh ? "已用尽" : "Spent";
    public static string PercentUsed(double pct) => Zh ? $"{pct}%" : $"{pct}%";
    public static string PercentLeft(double pct) => Zh ? $"剩 {pct}%" : $"{pct}% left";
    public static string Dash => "—";

    // --- hover card ---
    public static string UsageTitle(string name) => Zh ? $"{name} 用量" : $"{name} Usage";
    public static string NoReading => Zh ? "暂无读数" : "No reading";
    public static string CachedReading => Zh ? "缓存读数" : "Cached reading";
    public static string UsedLine(double used) => Zh ? $"已用 {used}%" : $"{used}% used";
    public static string UsedLeftLine(double used, double left) => Zh
        ? $"已用 {used}% · 剩 {left}%"
        : $"{used}% used · {left}% left";
    public static string ResetsToday(string time) => Zh ? $"重置于 {time}" : $"Resets {time}";
    public static string ResetsTomorrow(string time) => Zh ? $"明天 {time} 重置" : $"Resets tomorrow {time}";
    public static string ResetsOn(string when) => Zh ? $"重置于 {when}" : $"Resets {when}";
    public static string RunsOutInMinutes(int minutes) => Zh
        ? $"约 {minutes} 分钟后耗尽"
        : $"Runs out in {minutes} min";
    public static string WontLastWindow => Zh ? "窗口期内不够用" : "Won't last the window";

    public static string LimitFiveHour => Zh ? "5 小时限额" : "5-hour limit";
    public static string LimitWeekly => Zh ? "每周限额" : "Weekly limit";
    public static string LimitMonthly => Zh ? "每月限额" : "Monthly limit";
    public static string LimitDaily => Zh ? "每日限额" : "Daily limit";
    public static string LimitSpend => Zh ? "消费限额" : "Spend limit";
    public static string LimitBalance => Zh ? "余额" : "Balance";
    public static string LimitMessages => Zh ? "消息数" : "Messages";
    public static string LimitOther => Zh ? "限额" : "Limit";

    // --- health ---
    public static string CredentialRefused => Zh ? "凭据被拒绝" : "Credential refused";
    public static string NotConfigured => Zh ? "未配置" : "Not configured";
    public static string RateLimited => Zh ? "请求受限" : "Rate limited";
    public static string RouteChanged => Zh ? "接口已变更" : "Route changed";
    public static string Unavailable => Zh ? "不可用" : "Unavailable";

    // --- settings ---
    public static string WindowTitle => Zh ? "Pulse 设置" : "Pulse";
    public static string SectionAccounts => Zh ? "账号与密钥" : "Accounts & keys";
    public static string SectionMoreAccounts => Zh ? "更多账号" : "More accounts";
    public static string SectionGeneral => Zh ? "通用" : "General";
    public static string SectionStatusLine => Zh ? "Claude Code 状态栏" : "Claude Code status line";
    public static string MoreAccountsHint => Zh
        ? "Claude、Codex、Grok、Grok Bot、Antigravity 可在现有登录之外再加一个账号。Antigravity 添加的是另一路 language server 连接（端口 + CSRF），不是 OAuth 登录。"
        : "Claude, Codex, Grok, Grok Bot, and Antigravity can each keep another login beside the one already in use. Antigravity adds a second language-server connection (port + CSRF), not an OAuth sign-in.";
    public static string LaunchAtStartup => Zh ? "开机启动 Pulse" : "Launch Pulse at startup";
    public static string Alerts => Zh ? "提醒" : "Alerts";
    public static string AlertOff => Zh ? "关闭" : "Off";
    public static string AlertEighty => Zh ? "用量达 80%" : "At 80% used";
    public static string AlertNinetyFive => Zh ? "用量达 95%" : "At 95% used";
    public static string ReadTokenSpend => Zh ? "从本地日志读取 Token 消耗" : "Read token spend from local logs";
    public static string GlobalHotkey => Zh ? "全局快捷键 Ctrl+Alt+P 显示/隐藏工具条" : "Global hotkey Ctrl+Alt+P toggles the rail";
    public static string SignInCopilot => Zh ? "登录 GitHub Copilot" : "Sign in to GitHub Copilot";
    public static string Network => Zh ? "网络" : "Network";
    public static string FollowSystem => Zh ? "跟随系统" : "Follow system";
    public static string ManualProxy => Zh ? "手动代理" : "Manual proxy";
    public static string SaveProxy => Zh ? "保存代理" : "Save proxy";
    public static string ProxyHint => Zh
        ? "主机与端口需一并保存。主机为空或端口不在 1–65535 时保留原配置。"
        : "Host and port save together. Empty host or port outside 1–65535 keeps the previous endpoint.";
    public static string ProxySavedEndpoint(string host, int port) => Zh
        ? $"已保存：{host}:{port}"
        : $"Saved: {host}:{port}";
    public static string ProxySavedSystem => Zh ? "已保存：跟随系统。" : "Saved: follow system.";
    public static string ProxyPortInvalid => Zh
        ? "端口须为 1–65535 的整数。已保留原配置。"
        : "Port must be a whole number from 1 to 65535. Previous endpoint kept.";
    public static string ProxyHostRequired => Zh
        ? "手动代理需要主机名。已保留原配置。"
        : "Host is required for a manual proxy. Previous endpoint kept.";
    public static string Version => Zh ? "版本" : "Version";
    public static string CheckForUpdates => Zh ? "检查更新" : "Check for updates";
    public static string Language => Zh ? "界面语言" : "Language";
    public static string Theme => Zh ? "外观" : "Theme";
    public static string ThemeLight => Zh ? "浅色" : "Light";
    public static string ThemeDark => Zh ? "深色" : "Dark";
    public static string ThemeSystem => Zh ? "跟随系统" : "System";
    public static string Save => Zh ? "保存" : "Save";
    public static string AddAccount => Zh ? "添加账号" : "Add account";
    public static string Rename => Zh ? "重命名" : "Rename";
    public static string SignInAgain => Zh ? "重新登录" : "Sign in again";
    public static string Remove => Zh ? "移除" : "Remove";
    public static string Cancel => Zh ? "取消" : "Cancel";
    public static string Ok => Zh ? "确定" : "OK";
    public static string PrimaryAccount => Zh ? "1 个账号（主）" : "1 account (primary)";
    public static string PrimaryPlusAdded(int n) => Zh
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
        ? "已有登录流程在进行中。"
        : "Another sign-in is already running.";
    public static string RemoveConfirm(string label) => Zh
        ? $"移除「{label}」？其凭据将从保险库删除。"
        : $"Remove “{label}”? Its credential is deleted from the vault.";
    public static string AccountRemovedDuringSignIn => Zh
        ? "该账号已移除，未写入任何内容。"
        : "That account was removed; nothing was written.";
    public static string RenameTitle => Zh ? "重命名账号" : "Rename account";
    public static string RenameLabel => Zh ? "名称" : "Label";

    // --- Antigravity add dialog ---
    public static string AddAntigravityTitle => Zh ? "添加 Antigravity 账号" : "Add Antigravity account";
    public static string AddAntigravityHint => Zh
        ? "Antigravity 配额仅在对应登录的 language server 运行时可用。请粘贴另一实例的端口与 CSRF token（命令行 --csrf_token）。"
        : "Antigravity quota is only available while that login's language server is running. Paste the port(s) and CSRF token from the other Antigravity instance (command line flag --csrf_token).";
    public static string AntigravityPorts => Zh ? "端口（逗号分隔）" : "Port(s), comma-separated";
    public static string AntigravityToken => Zh ? "CSRF token" : "CSRF token";

    // --- token spend ---
    public static string SpendTitle => Zh ? "Token 消耗" : "Token spend";
    public static string SpendOneRowPerSource => Zh ? "每个数据源一行" : "One row per source";
    public static string SpendRescan => Zh ? "重新扫描" : "Rescan";
    public static string SpendEnable => Zh ? "读取本地用量记录" : "Read local usage records";
}
