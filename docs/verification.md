# 验证记录

开发日期：2026-10-31，America/Los_Angeles。当前为开发候选，尚不是完成全部 P0 验收的发布版。

## 0.1.0 已执行的历史验证

- 本机 Rhino `8.34.26223.11002` / arm64；Rhino 内置 .NET 8.0.14；构建 .NET SDK 8.0.129。
- Core/Provider/Rhino/Sidecar Release 构建通过，0 warning / 0 error。
- 最终本地 commit `571ba427dabc901c589d953b1d54076179681e3b`：Core **61/61**、Provider **39/39**，总计 **100/100** 测试通过。包含 GLB、工作流恢复、下载重试、连接隔离、分页、未知提交确认、OAuth/REST/MCP 只读发现。
- 实际公开 OAuth discovery 已通过本实现解析。国际区 `/oauth/register` 已成功注册公开客户端；用户未在五分钟内返回回调，本次授权尝试超时。未取得 token，未执行 MCP 账户访问或收费操作。后续从面板重新登录。
- 自制 GLB fixture 可确定性再生，Core validator 接受，几何闭合/方向/体积及 PNG 验证通过。不是生成服务输出。
- Rhino 自带 Yak 实际安装成功；25 个已安装文件与候选 ZIP 内容 SHA-256 一致。安装路径 `~/Library/Application Support/McNeel/Rhinoceros/packages/8.0/lux3d-rhino/0.1.0`。
- 已安装 sidecar 进程实际启动成功；两次私有协议请求（list / hasUnknown）的版本、请求 ID、结果结构全部匹配，stderr 为空，未调用 Provider。证据 `artifacts/sidecar-smoke.json`。
- 本机状态目录实测为 `~/Library/Application Support/Lux3D-Rhino`。Tripo 工作树仍干净，安装位于独立包目录。

## 必须区分的验收状态

| 项目 | 状态 |
| --- | --- |
| 官方 OAuth metadata / 实际 DCR | 已通过 |
| PKCE/state/loopback/token exchange 模拟 | 已通过离线测试 |
| 用户真实 OAuth 授权、Keychain 保存、refresh | 待用户手动授权 |
| OAuth token 访问官方 MCP | 待真实授权后只读探测 |
| 本机 Yak 包安装及逐文件比对 | 已通过，25/25 文件一致 |
| Rhino 实际加载 / 命令 / 面板 | 待用户手动打开 Rhino |
| 非对称夹具毫米/米、平面/旋转/高度 | 自测命令已实现，待真实宿主运行 |
| 候选切换 / 独立 Block / 重放 / Undo/Redo | 自测命令已实现，待真实宿主运行 |
| 保存、复制资源、重开 `.3dm` | 自测命令已实现，待真实宿主运行 |
| base color / metallic / roughness / normal 视觉 | 未验证，不声称完整 PBR 已验收 |
| 真文字/单图生成与下载 | 未执行收费任务 |
| Tripo 共存 | 安装身份隔离已检查；同宿主实际使用待验收 |
| Windows / Grasshopper | 0.1.0 未适配；0.1.1 适配记录见下 / 未实现 |

按用户后续要求，不再使用 Computer Use 控制桌面。构建和安装自动执行；用户手动打开 Rhino 并运行 README 中的 `Lux3DSelfTest`。报告中的 `fullP0Acceptance` 保持 false，因为自动自测不能代替真实生成和视觉验收。

后续证据目录：`artifacts/install-verification.json`、`artifacts/rhino-selftest/`。真实 token、API Key、授权码不得复制到此文档或提交 Git。

已安装候选：`artifacts/package.uXyKbF/lux3d-rhino-0.1.0-rh8_32-mac.yak`；SHA-256 `f8e3e05cd81573c365275f837b7cbfc514ec5c07fb164d484ead8c4225bce4b4`。此文档是安装后的验证记录；包内文档保留构建时的状态，实际二进制来源为上述 commit。

## 0.1.1 双平台适配（测试后置）

用户要求“确保适配，测试以后再做”。本轮只修改实现、静态审查和编译；没有运行测试套件、实际 OAuth、Windows 原生凭证调用、Rhino 宿主命令或安装脚本，也没有生成或安装新 Yak 包。现有 0.1.0 安装和上述历史证据保留，不能作为 0.1.1 的通过记录。

本机 Release 编译已通过（0 warning / 0 error）：`dotnet build Lux3D.Rhino.sln --configuration Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`。使用串行编译避免本机沙箱中的并行 MSBuild 等待；测试项目仅编译，未运行。该结果不等于在 Windows 上完成构建或运行验收。

Shell 脚本通过 `bash -n`，Python 脚本通过 AST 语法解析，差异通过 `git diff --check`。本机没有 PowerShell；Windows 脚本仅完成 5.1 语法/API 的静态人工审阅，未标记为执行通过。

已补齐的实现：

- 平台工厂选择 macOS Keychain / Windows 当前用户 DPAPI；Windows 密文原子保存、服务/区域账户绑定和受限目录，超长令牌明确报错而不截断。
- 跨平台 .NET 8 host 定位，支持环境指定、标准目录、PATH、符号链接解析与缺少运行时提示；IPC 两端显式 UTF-8。
- Windows 任务读取允许共享删除，避免另一后台进程原子保存时被读者文件锁阻止。
- Mac shell 与 Windows PowerShell 构建/安装入口；独立平台 Yak 标签、清单版本解析、包哈希及逐文件安装核对；支持显式跳过测试并记录为 skipped。
- Rhino/Eto 界面、headless GLB 与文档逻辑静态审查，未发现新增明确的平台绑定；Windows 要求 Rhino 8 NETCore 模式。

后续验收应在各平台分别执行，优先检查：正常及含空格/中文的安装路径、无终端 PATH 时后台启动、缺少 .NET 8 的提示、OAuth 保存/重启读取/刷新/退出、长令牌、多个面板读取/更新历史、原有 GLB/Block/Undo/PBR 宿主清单。Windows 还需确认 PowerShell 5.1 脚本执行与 DPAPI/ACL 行为，macOS 需确认此次改动未影响原有安装。未执行的项目保持待验证。
