# Pulse.Win — Windows 复刻版

基于 [qunqin24/Pulse](https://github.com/qunqin24/Pulse)（macOS/Swift）的 Windows 原生重写，
技术方案与阶段规划见仓库根目录的 `deep-research-report.md`。

**定位**：Windows 原生重写，不是 Swift 移植。复用上游的 Provider 协议知识、DTO、
归一化算法、fixtures 与刷新语义；平台层、安全层、UI 层全部按 Windows 惯例重写。

## 技术栈

| 项 | 选择 |
|---|---|
| Runtime | .NET 10 LTS |
| UI | WPF（Rail 悬浮条 + 托盘） |
| 模式 | MVVM + 插件式 Provider |
| 密钥存储 | Windows Credential Manager + DPAPI（规划中，当前为占位） |
| 测试 | xUnit + Provider 契约测试（fixtures 来自上游 Apache-2.0 仓库） |
| 打包 | MSIX + App Installer（规划中） |

## 解决方案结构

```text
Pulse.Win/
├─ src/
│  ├─ Pulse.App/            WPF 应用（Rail 窗口、托盘、组合根）
│  ├─ Pulse.Core/           领域模型：UsageWindow/ProviderUsage、缓存、自适应刷新引擎
│  ├─ Pulse.Providers/      Provider 适配器（DeepSeek、Kimi Code、OpenCode Go）
│  ├─ Pulse.Diagnostics/    脱敏日志（canary 扫描、Bearer/Cookie/Authorization 拦截）
│  └─ Pulse.Storage/        本地存储（占位）
├─ tests/
│  ├─ Pulse.Core.Tests/         归一化、缓存、刷新隔离、日志脱敏
│  └─ Pulse.ProviderContract.Tests/  上游 fixtures 驱动的解析契约测试
```

## 核心语义（继承自上游，测试锁定）

- **不发明百分比**：`UsedFraction` 只来自 Provider 报告；`IsExhausted` 只来自
  Provider 自己的标志（DeepSeek `is_available=false`、OpenCode `status!="ok"`），
  绝不用 `>=100%` 推断。
- **剩余→已用只反转一次**：Kimi `detail.remaining`、Copilot `percent_remaining`
  在适配器边界反转，下游全部是"已用"。
- **windowSeconds 可能只是排序键**（`ReportsLength=false`），禁止拿来当真实时长。
- **缺失金额 ≠ 0**：DeepSeek 金额解析失败时保持缺失（0 会画满红环）。
- **缓存按账户 scope 隔离**；reset 已过的窗口直接丢弃；无 reset 的窗口 24h 上限；
  缓存读数永远以 stale 状态出现。
- **刷新引擎**：单调度器，账户级 due time；进行中的刷新合并重复请求；
  单 Provider 异常被隔离，不影响同一轮其他 Provider。
- **日志零明文凭据**：所有日志经脱敏器（Authorization/Cookie/Bearer/token 形态 +
  运行时注册的 canary 值）。

## 运行

```bash
cd Pulse.Win
dotnet build Pulse.Win.slnx
dotnet test Pulse.Win.slnx
dotnet run --project src/Pulse.App
```

需要 .NET 10 SDK（`winget install Microsoft.DotNet.SDK.10`）。
应用启动后显示 Rail 悬浮条和托盘图标；Provider 密钥配置 UI 在后续里程碑交付。

## 路线图

| 里程碑 | 内容 |
|---|---|
| ✅ 当前 | Core 领域模型、刷新引擎、缓存、脱敏日志；3 个 key-based Provider + 契约测试；Rail/托盘雏形 |
| 下一步 | Claude/Codex/Copilot 适配器、OAuth/设备码流程、凭据入库（Credential Manager） |
| W6 | Rail 完整化：贴边停靠、自动收起、hover 详情、多显示器混合 DPI |
| W7–W8 | 通知、开机启动、代理、性能基线、MSIX/自动更新 |

## 许可

本项目遵循上游 Apache-2.0（见根目录 LICENSE）；基于 Pulse by qunqin24 修改，
相关权利与归属要求按 Apache-2.0 执行。
