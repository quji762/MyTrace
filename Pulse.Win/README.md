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
| Token Spend 首批数据源 | UsageLedger/LedgerDay/TokenTally 四类 token 计数 + 每刻钟 slot + 日聚合（缺口补齐）+ 定价（无价模型计数不计价）；Claude Code（消息 id 去重、<synthetic> 剔除、customTitle 优先）与 Codex（运行总量差分、cached 从 input 拆分）JSONL 解析器，经 %USERPROFILE% 路径 locator 扫描；OpenCode/Kilo SQLite store（reasoning 计入 output、store 自带 cost 忽略） |
| Claude status-line hook（Windows 等价物） | settings.json 安装器（记住并恢复原 status line、首次备份、**拒改不可解析文件**）；`--statusline` 捕获模式（原子写入、used_percentage>101 防泄漏、垃圾输入静默）；app 启动分流；Claude Provider 在 endpoint 不可达/凭据失效时回落到捕获读数，超 10 分钟标 stale |
| CherryStudio spend 源 | 同一 API 调用流式追加 3–4 份拷贝按 requestId（回落 message.id/uuid）折叠、逐字段最大值合并；V2 树优先于 V1 抢占同名会话路径；无身份记录独立保留 |
| Cline CLI spend 源 | `<session>.messages.json` + manifest 对；`inputTokens` 为含缓存口径，扣减两种缓存后钳制零；env 根目录按 CLI 顺序（CLINE_SESSION_DATA_DIR→CLINE_DATA_DIR→CLINE_DIR→~/.cline）；无 ts 的消息不回填文件日期 |
| Amp spend 源 | assistant 消息与 usageLedger.events 对账（toMessageId 优先、模型+token 数兜底），匹配的消息不重复发出——两侧都发会把每次调用翻倍；无事件戳的消息标 isAggregate 放到线程时间；完全无时间不发出并标 incomplete |
| Goose spend 源 | sessions.db（accumulated 列优先、累计语义→每会话一条 isAggregate 记录）；total−input−output 的差值**保持 unclassified，不推定 reasoning**（schema 未声明）；created_at 读不出跳过（不用 1970 分桶）；GOOSE_PATH_ROOT 优先 |
| Copilot OTEL spend 源 | ~/.copilot/otel 的 OpenTelemetry JSONL：四车道优先级（chat span > inference log > agent-turn > agent-summary），跨车道按 trace/response id 抑制；token 拆分为不相交四类（cache read 从 input 扣一次、reasoning 仅在 output 缺席时补位）；裸 total 记 unclassified；W3C 全零哨兵 id 视为缺席；无时间戳的记录不落桶 |
| Copilot Desktop spend 源 | data.db 行是 LIFETIME 权威、sidecar events.jsonl 是运行中总量：shutdown 快照按模型差分、按行预算封顶，解释不了的余量在 created_at 一次性发出；无 session.start 时首快照是未知基线；cache_write 只在 sidecar；reasoning 不并入 output（归属未声明）→标 isPartial；本车道全部 isAggregate |
| Copilot VS Code spend 源 | chatSessions JSONL 是 append/patch 日志（kind 0/1/2），先重建请求数组再读取；仅 Copilot 自家请求计数（resolvedModel 或 copilot/ 前缀）；thinking tokens 折入 output；无时间戳跳过（不落 epoch）；同时刻两请求带 #n 后缀都计数 |
| Kiro spend 源 | ~/.kiro/sessions/cli 的 header+sidecar 对：只读真实计数器（input/output_token_count），全零的轮次不发出——零不是测量值；Prompt sidecar 时间优先于 end_timestamp；上下文窗口/字符数估算路线刻意不读 |
| Qwen spend 源 | Gemini 形 usageMetadata：totalTokenCount 用作**校验**——证明 cache 在 prompt 内（相减）或并列（disjoint），两种恒等式都不成立时整个 total 记 unclassified 而非猜测拆分；thoughts 计入 output；片段内容摘要 + 位置做身份 |
| Gemini CLI spend 源 | 三种盘上形状（session-*.json / tmp/<id>/chats/*.json / headless JSONL）各自解析；缓存关系**按形状区分**：session 形靠 total 恒等式证明、headless 形按输入键名（prompt 风格=含缓存、裸 input=净值）+tokens 包装判断；tool tokens 是真实计数（session 形折入 input）；reasoning 叠加在 output 上；headless 同 id 重导出**原位替换**不重复计数 |
| Crush spend 源（识别但无 token） | projects.json 注册表与各项目 crush.db 被定位和监视，但**不产生任何记录**：Crush 只报会话 cost（美元），把 cost 反推成 token 数就是编造没人测量过的数字——上游同一裁定，面板如实显示"无 token 计数"而非静默缺席 |
| ZCode spend 源 | JSONL（usage 优先、token_usage 回落、裸 total 记 unclassified）+ v2 SQLite store（schema 文档化 input 含缓存、output 含 reasoning：缓存从 input 扣除、reasoning **不二次折入** output；computed_total 超出 input+output 的部分记 unclassified）；缺列老 schema 用 NULL 占位适配 |
| DSH spend 源 | session.jsonl[.zstd]：**后缀是物理、魔数是真**——仅当字节带 zstd frame magic 才解压（.NET 无内置 zstd，压缩帧如实报不可读而非静默清零）；seq < seedLength 的 fork 前缀跳过；reasoningTokens 是 outputTokens 子集保持原样；compaction/summary 也是真实调用；身份不含 session 前缀，fork 复制的调用与原版折叠 |
| Junie spend 源 | events.jsonl 的 LlmResponseMetadataEvent.modelUsage[] 每项一次调用：**时间锚定在响应开始**（timestampMs − usage.time 延迟），timestampMs=0 视为未设回落到 session-YYMMDD-HHMMSS 自带日期；reasoning 与 output 并列且无 containment 声明→保持 output、标记 isPartial；cost 是产品自己的美元不读作 token |
| Codebuff spend 源 | manicode* 树的 chat-messages.json：同一数字复制在 metadata.usage / metadata.codebuff.usage / runState history providerOptions 多处，**逐字段取第一个非零来源合并而非求和**（高优先级副本里的零不能掩盖真实计数）；credits 是产品的钱不是 token 类型；chat id 的时-分-秒破折号还原为时间戳；Freebuff 共享目录、互不双计 |
| Mux spend 源 | per-workspace session-usage.json 的 `"provider:model"` 快照：provider 前缀剥离（路由不是模型名）、每模型一条 isAggregate 记录；**reasoning 留在计数之外**（output 与 reasoning 并列且无 total 可查包含关系→宁可少计不猜）；无 lastRequest 时间戳的会话跳过（不用文件 mtime 编造） |
| Freebuff spend 源（识别但零记录） | agentType base2-free 识别为 Freebuff；**整个 reader 就是空记录**——只有字符数估算（约 4 字符/token），推断值不冒充报告值、编造的零比诚实缺席更糟；带权威用量的聊天归 Codebuff 所有，两 reader 永不双计同一批 |
| GJC spend 源 | session 头 + message 行的 JSONL：**身份只有 entry id**——有 id 的行按 id 折叠（重放算一次），**无 id 的行永远独立计数**（相同秒数/模型/token 数不是两次调用是一次的证据，值哈希折叠会静默丢掉真实请求）；字节级镜像文件（同 session id 同文件名同字节）读一次，不同行数的同名文件是更完整记录正常解析；消息毫秒时间优先于 envelope RFC3339；cost.total 不读作 token |
| Unsloth spend 源 | studio.db 双路径：chat_messages 的 contextUsage 归一化（cache read 受 prompt 上限、cache write 受余量上限、total 提升至 prompt+completion、余量归 input，reasoning 折入 output 一次）+ api_usage_events（无缓存/无 reasoning 桶，input=total−completion）；providerType 路由名不作价键；零时间戳/无身份行跳过 |
| Jcode spend 源 | session + journal append 重放；缓存形状**只由显式标记判定**（Anthropic 式 cache_creation 键=input 独占；OpenAI 式 details 对象=cache 是 input 子集；两者皆无的正 cache 读=input 记 unclassified、标记 partial——量级从不用来推断约定）；reasoning_output_tokens 与 output 并列无声明→保持 output、标 partial；journal meta.model 重放更新后续消息的模型 |
| Fx spend 源 | per-session usage-v2.json + session.json sidecar + index.json 标题：每模型一条累计记录（models 空而聚合有用量时在 fx-unknown 下发出一次，总数不丢）；reasoning 与 output 并列无声明→保持 output、标 partial；updated_at_ms=0 回落 created_at_ms，两者皆无跳过（不用 mtime 编造）；total_cost 是产品美元不读作 token |
| OpenClaw spend 源 | SQLite（transcript_events）+ legacy JSONL 双 store，事件形状相同且 doctor --fix 会导入原文件——身份是**跨 store 的**（event id+时间戳+计数），同一调用进两个 store 也不会双计；reasoningTokens 文档化为 output 子集（仅在 output 缺席时补位）；openclaw-transcript/delivery-mirror/gateway-injected 等记账行跳过；model_change/model-snapshot 的 bookkeeping 模型向前携带；bare totalTokens 记 unclassified；.zst 与 codex-home 镜像不读 |
| RooCode/KiloCode/Cline spend 源 | VS Code task log（ui_messages.json）：只计 `api_req_started` 项，四种 token 在 text 字段里嵌套 JSON；modelInfo 优先、conversation history 的最后 `<model>` 标签兜底、`<slug>`/`<name>` 作 agent 名；ts=0 与不可解析时间戳跳过；三编辑器布局（Code/Insiders/VSCodium）+ .config + .vscode-server 均被探测 |
| Droid spend 源 | settings.json 的**会话累计总量**（单条 isAggregate 记录，不拆到回复）：totalTokenCount 是唯一权威——对四种候选恒等式（reasoning 并入/独立 × cache 含入/并列）逐一求和验证，恰匹配一种才落地；都不匹配→整 total 记 unclassified（总数完整、只是种类未知，isPartial=false）；无 total 且有正 cache→output 保留、input 记 unknown、标 partial；thinking 无声明同样标 partial；模型 id 保留原样（只剥 custom: 前缀与方括号），无模型时给 claude/gpt/gemini/grok-unknown 占位——绝不借用具体模型的费率 |
| Pi family spend 源（Pi/omp/Senpi/Kimchi） | 一个解析器四种身份：四桶独立 usage + totalTokens 只作 unclassified 余量载体；reasoning 文档化为 output 子集从不二次相加；fork 副本用**会话无关身份**（responseId 优先）折叠到原版，Kimchi 用 session-scoped 命名空间保持两会话独立；Senpi 发现 OmO 项目子树并把 session_info.name 当标题；无 header/时间/模型的行标 partial |
| PrimeAgent spend 源 | Pi RLM 格式 + 父子对账：parent 的 aggregateUsage 已含子调用、child_usage_attributed 声明明细，子 transcript 也被独立扫描——**parent 自身 tally 恰等于 aggregate 的那条被减去准确的 child 用量**（钳制到零）；找不到子会话时 parent 保留聚合（保守可查）；attribution id 按 fork-lineage 根配对，parentSession 环取最小路径（确定性而非遍历序） |
| Token Spend 历史面板 | 每日 tokens 柱状图（零高度缺口日 + 无价份额帽）；会话列表（标题/项目/tokens/最近活动）；摘要行（全期与 7 天 tokens/cost、最忙日、top model 份额、无价模型）；托盘菜单直入 |

### 🚧 与原版仍有差距

| 能力 | 差距 | 备注 |
|---|---|---|
| Token Spend 后续数据源 | 首批 2 类（Claude Code / Codex 本地 transcript）已实现；其余 52 类为各工具各自的数据存储解析 | 按上游 1.x 节奏逐步补充 |
| Status line/Desktop 会话路由 | Claude Code 的 status-line hook 与 Desktop cookie fallback 为 macOS 集成 | Windows 需等价物或永久缺省（endpoint 路由已可用） |
| Token Spend 其余 27 类数据源 | 首批 27 类已实现（Claude/Codex transcript + OpenCode/Kilo store + CherryStudio 流式去重 + Cline CLI store + Amp thread 对账）+ 历史面板；Warp 快照只报请求数与金额、无 token（上游同一裁定：不发明数字）；其余为各工具独立存储的解析 | 按上游 1.x 节奏逐步补充 |
| Per-Monitor DPI 完整矩阵 | WM_DPICHANGED 钩子已挂；混合 DPI 实机矩阵未验证 | 需多屏硬件 |
| winget manifest | 草稿已入库（packaging/winget），sha256 由发布流水线盖章后提交 | 打包链已就绪 |
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
