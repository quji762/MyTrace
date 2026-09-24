# Pulse.Win 项目概况

> 本文件是项目的一句话版全景图：定位、架构、进度、语义铁律与已知差距。
> 逐项功能的详细对齐矩阵见 [Pulse.Win/README.md](Pulse.Win/README.md)；
> 开发方案见根目录的 [deep-research-report.md](deep-research-report.md)。

## 一句话概括

**Pulse.Win 是 macOS 菜单栏应用 [Pulse](https://github.com/qunqin24/Pulse) 的 Windows 原生重写**：
悬浮 Rail 条实时显示 20 家 AI 编码工具的额度与用量，配合阈值通知、燃烧率预测、
多账号管理，以及一个覆盖约 50 个本地数据源的 Token Spend 历史面板。

**它是重写，不是移植**——复用上游的 Provider 协议知识、DTO、归一化算法、
fixtures 与刷新语义；平台层（WPF/DPAPI/托盘）、安全层、UI 层全部按 Windows 惯例重构。

## 基本信息

| 项 | 值 |
|---|---|
| 上游 | qunqin24/Pulse（macOS/SwiftUI，Apache-2.0） |
| Windows 版 | 本仓库 `Pulse.Win/`，分支 `windows-dev` |
| 技术栈 | .NET 10 LTS · WPF · xUnit · Microsoft.Data.Sqlite · System.Text.Json |
| 测试 | 445 个（384 Core + 49 Provider 契约 + 12 App），全部基于临时目录 fixtures，绝不读写真实用户数据 |
| 打包 | MSIX（`.github/workflows/windows-release.yml`，`windows-v*` 标签触发） |

## 解决方案结构

```text
Pulse.Win/
├─ src/
│  ├─ Pulse.App/            WPF 应用：Rail 悬浮条、托盘、设置、Token Spend 面板、组合根
│  ├─ Pulse.Core/           领域模型与全部业务逻辑：
│  │   ├─ Providers/          20 个 Provider 的额度 DTO + 归一化 + PercentNormalization
│  │   ├─ Usage/              last-good 缓存（scope 隔离、24h 上限）
│  │   ├─ Refresh/            自适应刷新引擎（单调度器、2–30 分钟、异常隔离）
│  │   ├─ Forecast/           燃烧率预测（reset 前 2 小时内给出 ETA）
│  │   ├─ Notifications/      阈值通知（crossing 触发一次 + 迟滞）
│  │   ├─ Ledger/             ~50 个 spend 数据源读取器 + UsageLedger 定价管线
│  │   ├─ ClaudeHook/         status-line hook 安装器 + --statusline 捕获模式
│  │   └─ Accounts/           多账号 slot 管理
│  ├─ Pulse.Providers/      20 个 Provider 适配器（每家一个目录）+ 注册表
│  ├─ Pulse.Auth/           OAuth/设备码/loopback 登录流程
│  ├─ Pulse.Diagnostics/    脱敏日志（canary 扫描、Bearer/Cookie 拦截）
│  └─ Pulse.Storage/        DPAPI CurrentUser vault + Windows Credential Manager
└─ tests/
   ├─ Pulse.Core.Tests/         归一化、缓存、刷新、预测、告警、OAuth、全部 reader
   ├─ Pulse.App.Tests/          面板组合根、版本对齐、工作区与 spend 装配
   └─ Pulse.ProviderContract.Tests/  上游 fixtures 驱动的解析契约测试
```

## 功能对齐进度（2026-09，截至提交 73258ca）

### 额度显示（主链路）——已对齐

- **20 个额度 Provider 全部移植**：Claude、Codex、Kiro、Antigravity、Cursor、OpenCode Go、
  Kimi、Ollama Cloud、z.ai、Zhipu GLM、MiniMax ×2、Copilot、Grok、Grok Bot、
  Volcengine、Command Code、DeepSeek、Devin、Xiaomi MiMo。
- Rail UI（贴边/吸附/拖拽/2s 收起/hover 展开/位置持久化）、燃烧率、阈值通知、
  hover 详情卡片、Light/Dark/System 主题。
- 凭据安全：DPAPI 加密 vault + Credential Manager；CLI 凭据借用走 Windows 路径
  （`~/.claude/.credentials.json` 等）；会话凭据仅接受用户粘贴，**绝不解密浏览器 Cookie**。
- 认证：GitHub Device Flow（Copilot）、OpenAI Device Code、xAI RFC 8628 设备码、
  Claude loopback OAuth、GrokBot Cursor 网页登录。
- 多账号：Claude/Codex/Grok/GrokBot 走各自 OAuth/网页登录；Antigravity 追加「第二路 language server 连接」（端口 + CSRF）。slot 生成后永不复用。
- Codex app-server fallback（JSON-RPC 子进程 + rateLimits 推送）。

### Token Spend 历史面板——50 个数据源已实现

每日 token 柱状图 + 会话列表 + 摘要行（全期/7 天 tokens/cost、最忙日、top model、
无价模型），共 **50 个 tab**，覆盖上游全部 reader 家族：

- **会话日志族**：Pi/omp/Senpi/Kimchi、Prime Agent、Gemini CLI、Qwen Code、Amp、Droid、OpenClaw
- **编辑器日志族**：Roo/Kilo/Cline（VS Code task log）、CodeBuddy/WorkBuddy、CherryStudio、CommandCode、OpenCodeReview、ZCode
- **数据库族**：Hermes、Goose、Zed、Kiro、Crush（识别但零记录）、Unsloth、Antigravity CLI、MiMo Code、Devin Desktop、Devin CLI
- **结构化日志族**：Mux、Codebuff、Freebuff（零记录裁定）、Jcode、Augment、GJC、Junie、DSH、Fx、LM Studio、Reasonix
- **捕获导出族**：Cursor、Antigravity IDE（双布局）、Trae、Warp（零记录裁定）、Hindsight、Mcode
- **Copilot 三车道**：OTEL / Desktop / VS Code，组合入口去重
- **族外三 store**：OpenCode/Kilo SQLite、Grok Build、Kimi CLI
- **原生 transcript**：Claude Code、Codex

定价来自 **models.dev 每日抓取**（`ModelPriceCatalog`），带磁盘缓存与离线回落；
第一方价目优先，计划 vendor（opencode-go/kilo/cline-pass）命名空间兜底；
别名规则解决各产品拼写差异（`grok-4.6-build`、`gpt-5-6-sol-medium`、`k2p6` 等）；
查不到的模型如实计入 unpriced，绝不借价。

### Claude Code status line hook——已对齐

设置页一键启用/禁用：`settings.json` 安装器（记住并恢复原 status line、
拒改不可解析文件）+ `--statusline` 捕获模式（原子写入、垃圾输入静默）+
Claude Provider 的回落读数链路。

## 核心语义铁律（继承自上游，测试锁定）

这些规则是整个移植的"宪法"，每一条都有契约测试锁定：

1. **不发明数字**：百分比只来自 Provider 报告；exhausted 只来自 Provider 标志；
   cost 反推 token、字符数估算 token 一律拒绝（Warp/Crush/Freebuff 为此整个 reader 为空）。
2. **剩余→已用只反转一次**：Kimi/Copilot/Antigravity/MiniMax 在适配器边界反转，下游全是"已用"。
3. **缺失 ≠ 0**：金额解析失败保持缺失（0 会画满红环）；缺失 token ≠ 零 token。
4. **不确定的包含关系宁可少计不猜**：reasoning 与 output 并列且无 total 声明 →
   output 保持原样 + 标 isPartial；cache 关系无法证明 → 记 unclassified 或整条不发出。
5. **时间只用记录自带的**：无时间戳跳过，绝不用 1970 兜底、绝不用文件 mtime 编造。
6. **身份折叠只认显式 id**：消息 id/requestId/responseId 折叠重放；无 id 的行独立计数。
7. **缓存按账户 scope 隔离**；reset 已过直接丢弃；无 reset 窗口 24h 上限；读数永远标 stale。
8. **日志零明文凭据**；**不绕过浏览器安全边界**（无 Chrome 解密、无提权、无注入）。

## 已知差距（诚实清单）

| 项 | 状态 |
|---|---|
| Claude Desktop 会话路由 | macOS 独有集成（Electron cookie + keychain），无 Windows 等价物 |
| DSH zstd 解压 | .NET 无内置 zstd；压缩帧如实标 partial 而非静默清零（平台降级） |
| 上游桌面功能 | 多语言 UI、`--json` 输出模式等未实现（更新检查/全局快捷键/deeplink/代理已落地） |

## 构建 / 运行

```bash
cd Pulse.Win
dotnet build Pulse.Win.slnx
dotnet test Pulse.Win.slnx        # 445 个测试
dotnet run --project src/Pulse.App
```

需要 .NET 10 SDK（`winget install Microsoft.DotNet.SDK.10`）。
启动后显示 Rail 悬浮条 + 托盘图标；托盘 → Settings 粘贴密钥 / 添加账号 /
启用 status line。

## 开发约定

- 每个新 spend 数据源一批交付：**读取器 + 单元测试 + 面板 tab + README 差距矩阵行**。
- 测试只用临时目录 fixtures，绝不读写真实用户数据。
- 移植语义以对应上游 `.swift` 文件的 doc comment 为准，逐条核对，不凭直觉。
- 提交信息说明语义裁定（为什么少计、为什么标 partial），不只是列文件。
