# Pulse.Win — Windows 复刻版

基于 [qunqin24/Pulse](https://github.com/qunqin24/Pulse)（macOS/Swift）的 Windows 原生重写，
技术方案与阶段规划见仓库根目录的 `deep-research-report.md`。

**定位**：Windows 原生重写，不是 Swift 移植。复用上游的 Provider 协议知识、DTO、
归一化算法、fixtures 与刷新语义；平台层、安全层、UI 层全部按 Windows 惯例重写。

## 功能对齐状态（vs macOS 原版）

### ✅ 已对齐

| 能力 | 状态 |
|---|---|
| 19 个额度 Provider 的读取/解析层 | 全部移植（Claude、Codex、Antigravity、Cursor、OpenCode Go、Kimi、Ollama Cloud、z.ai、Zhipu GLM、MiniMax ×2、Copilot、Grok、Grok Bot、Volcengine、Command Code、DeepSeek、Devin、Xiaomi MiMo），上游语义铁律逐条锁定并有契约测试 |
| 上游语义铁律 | 不发明百分比；exhausted 只来自 Provider 标志；剩余→已用只反转一次；windowSeconds 可能只是排序键；缺失金额≠0；Grok 缺失=0 与 GrokBot 缺失=unset 的相反规则 |
| last-good 缓存 | scope 严格隔离；reset 已过窗口直接丢弃；无 reset 窗口 24h 上限；缓存读数永远标 stale |
| 自适应刷新 | 单调度器 + 账户级 due time；2–30 分钟区间；余额类 Provider 5 分钟上限；进行中刷新合并；单 Provider 异常隔离 |
| 合同健康 | Healthy/SchemaChanged/Unauthorized/CredentialExpired/RateLimited/ProviderUnavailable/UnsupportedPlatform 全链路，UI 显示诚实文案（"route changed" 而非崩溃） |
| 凭据安全 | DPAPI CurrentUser 加密 vault（crypt32 interop）+ Windows Credential Manager；禁止明文 JSON/机器序列号派生；密钥配置 UI |
| CLI 凭据借用（Windows 路径） | `~/.claude/.credentials.json`（含 expiresAt 校验）、`~/.codex/auth.json`（+ChatGPT-Account-Id 头）、`~/.grok/auth.json`（freshest unexpired）、`~/.commandcode/auth.json` |
| OAuth/设备码 | Copilot GitHub Device Flow（`read:user` 仅限）；Claude loopback（任意端口，`user:profile` 仅限，exchange 带 state）；OpenAI Device Code（非 RFC 8628：403/404=等待，provider 生成 proof key，不带 state）；PKCE（challenge=SHA-256 of ENCODED verifier） |
| 浏览器会话 Provider | Ollama Cloud + Xiaomi MiMo：仅用户主动粘贴 Cookie（allow-list 过滤 + 注入防护）；**绝不自动解密 Chrome Cookie**（App-Bound Encryption 是安全边界） |
| Antigravity | 本地 language_server 进程/端口/CSRF 发现（app 优先于 IDE）；remainingFraction 反转一次；所有候选全部尝试 |
| Rail UI | 无边框置顶悬浮条；左/右贴边 + 吸附；拖拽；位置按显示器+归一化偏移持久化（非绝对像素）；自动收起（2s 后收起、hover 展开）；19 环滚动布局；plan/reset/余额说明文字 |
| 燃烧率预测 | 单读数 rate = spent share/elapsed share；仅在 reset 前 2 小时内给出；burn ≤ 1 无 wall |
| 阈值通知 | crossing 触发一次（armed/disarmed 迟滞）；reset 变更重新武装；Provider exhausted 标志无视百分比；气泡通知 + Rail 双通道 |
| Windows 集成 | 托盘图标（Explorer 重启自恢复）；命名互斥体单实例；HKCU Run 键开机启动（无需管理员）；设置窗口 |

### ✅ 已对齐（第二批）

| 能力 | 状态 |
|---|---|
| 多账号管理 | Claude/Codex/Grok/GrokBot 四个 `supportsMultipleAccounts` Provider 均有设置页"Add account"，驱动各自主 OAuth 流程（OpenAI 设备码/xAI 设备码/Claude loopback）；slot 生成后永不复用，删除重加不继承身份；与 CLI 自身登录完全隔离 |
| Grok 设备码登录 | xAI RFC 8628 标准形（等待与拒绝都是 400，由 body error 区分）；`billing:read` 不请求（端点实测拒绝）；使用 xAI 自带的 verification_uri_complete |
| Grok Bot Cursor 网页登录 | loginDeepControl + poll（404=未完成，403 才终止）；challenge=SHA-256(ENCODED verifier)；~60 天 token 无刷新端点 |
| Token 刷新 | 旧 refresh token 前向保留；绝不触碰 CLI 自身存储 |
| 主题 | Light/Dark/System 三态，System 跟随系统 AppsUseLightTheme；调色板 token 热切换 |
| Hover 详情卡片 | 非激活 Popup 列出全部窗口（环只显示第一个）+ scope/reset/plan/余额 + 燃烧率 ETA |
| MSIX 发布链 | windows-release.yml：测试 → x64 发布 → MakeAppx → 签名（secrets 可选）→ canary 扫描 → Release；独立于上游 macOS 流水线 |
| Codex app-server fallback | `codex app-server` JSON-RPC 子进程（握手/请求 ID/EOF 终止/20s 超时/rateLimits 推送钩子）；`account/rateLimits/read` 解析（分组排序、ordinaryUsageAllowed 全组标记、planType 顶层字段）；token 缺失或被拒时回落 |
| Token Spend 首批数据源 | UsageLedger/LedgerDay/TokenTally 四类 token 计数 + 每刻钟 slot + 日聚合（缺口补齐）+ 定价（无价模型计数不计价）；Claude Code（消息 id 去重、<synthetic> 剔除、customTitle 优先）与 Codex（运行总量差分、cached 从 input 拆分）JSONL 解析器，经 %USERPROFILE% 路径 locator 扫描 |
| Token Spend 历史面板 | 每日 tokens 柱状图（零高度缺口日 + 无价份额帽）；会话列表（标题/项目/tokens/最近活动）；摘要行（全期与 7 天 tokens/cost、最忙日、top model 份额、无价模型）；托盘菜单直入 |

### 🚧 与原版仍有差距

| 能力 | 差距 | 备注 |
|---|---|---|
| Token Spend 后续数据源 | 首批 2 类（Claude Code / Codex 本地 transcript）已实现；其余 52 类为各工具各自的数据存储解析 | 按上游 1.x 节奏逐步补充 |
| Status line/Desktop 会话路由 | Claude Code 的 status-line hook 与 Desktop cookie fallback 为 macOS 集成 | Windows 需等价物或永久缺省（endpoint 路由已可用） |
| Token Spend 其余 52 类数据源 | 首批 2 类（Claude/Codex transcript）+ 历史面板（每日柱状图/会话列表/摘要）已实现；其余为各工具独立存储的解析 | 按上游 1.x 节奏逐步补充 |
| Per-Monitor DPI 完整矩阵 | WM_DPICHANGED 钩子已挂；混合 DPI 实机矩阵未验证 | 需多屏硬件 |
| winget manifest | 待首个发布 tag 后提交 | 打包链已就绪 |
| 代码签名证书 | 流水线支持，证书由发布者提供 | 商业发布所需 |

## 技术栈

| 项 | 选择 |
|---|---|
| Runtime | .NET 10 LTS |
| UI | WPF（Rail 悬浮条 + 托盘） |
| 模式 | MVVM + 插件式 Provider |
| 密钥存储 | DPAPI CurrentUser vault + Windows Credential Manager |
| 认证 | GitHub Device Flow / OpenAI Device Code / Claude loopback OAuth |
| 测试 | xUnit + Provider 契约测试（fixtures 来自上游 Apache-2.0 仓库） |
| 打包 | MSIX（windows-release.yml 流水线，windows-v* 标签触发） |

## 解决方案结构

```text
Pulse.Win/
├─ src/
│  ├─ Pulse.App/            WPF 应用（Rail 窗口、托盘、设置、组合根）
│  ├─ Pulse.Core/           领域模型：UsageWindow/ProviderUsage、缓存、刷新引擎、预测、告警
│  ├─ Pulse.Providers/      19 个 Provider 适配器 + 注册表
│  ├─ Pulse.Auth/           OAuth/设备码/loopback 登录流程
│  ├─ Pulse.Diagnostics/    脱敏日志（canary 扫描、Bearer/Cookie/Authorization 拦截）
│  └─ Pulse.Storage/        DPAPI 凭据库 + Credential Manager
├─ tests/
│  ├─ Pulse.Core.Tests/         归一化、缓存、刷新、预测、告警、OAuth、日志脱敏
│  └─ Pulse.ProviderContract.Tests/  上游 fixtures 驱动的解析契约测试
```

## 核心语义（继承自上游，测试锁定）

- **不发明百分比**：`UsedFraction` 只来自 Provider 报告；`IsExhausted` 只来自
  Provider 自己的标志，绝不用 `>=100%` 推断。
- **剩余→已用只反转一次**：Kimi `detail.remaining`、Copilot `percent_remaining`、
  Antigravity `remainingFraction`、MiniMax `*_remaining_percent` 在适配器边界反转，
  下游全部是"已用"。
- **windowSeconds 可能只是排序键**（`ReportsLength=false`），禁止拿来当真实时长。
- **缺失金额 ≠ 0**：DeepSeek 金额解析失败时保持缺失（0 会画满红环）。
- **缓存按账户 scope 隔离**；reset 已过的窗口直接丢弃；无 reset 的窗口 24h 上限；
  缓存读数永远以 stale 状态出现。
- **刷新引擎**：单调度器，账户级 due time；进行中的刷新合并重复请求；
  单 Provider 异常被隔离，不影响同一轮其他 Provider。
- **日志零明文凭据**：所有日志经脱敏器（Authorization/Cookie/Bearer/token 形态 +
  运行时注册的 canary 值）。
- **不绕过浏览器安全边界**：会话凭据仅接受用户主动粘贴；无 Chrome 解密、无提权、
  无注入。

## 运行

```bash
cd Pulse.Win
dotnet build Pulse.Win.slnx
dotnet test Pulse.Win.slnx
dotnet run --project src/Pulse.App
```

需要 .NET 10 SDK（`winget install Microsoft.DotNet.SDK.10`）。
应用启动后显示 Rail 悬浮条和托盘图标；在托盘 → Settings 中粘贴各 Provider 密钥。

## 许可

本项目遵循上游 Apache-2.0（见根目录 LICENSE）；基于 Pulse by qunqin24 修改，
相关权利与归属要求按 Apache-2.0 执行。
