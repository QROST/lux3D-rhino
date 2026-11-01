# 验证记录

开发日期：2026-10-31，America/Los_Angeles。当前为开发候选，尚不是完成全部 P0 验收的发布版。

## 已执行

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
| Windows / Grasshopper | 未验证 / 未实现 |

按用户后续要求，不再使用 Computer Use 控制桌面。构建和安装自动执行；用户手动打开 Rhino 并运行 README 中的 `Lux3DSelfTest`。报告中的 `fullP0Acceptance` 保持 false，因为自动自测不能代替真实生成和视觉验收。

后续证据目录：`artifacts/install-verification.json`、`artifacts/rhino-selftest/`。真实 token、API Key、授权码不得复制到此文档或提交 Git。

已安装候选：`artifacts/package.uXyKbF/lux3d-rhino-0.1.0-rh8_32-mac.yak`；SHA-256 `f8e3e05cd81573c365275f837b7cbfc514ec5c07fb164d484ead8c4225bce4b4`。此文档是安装后的验证记录；包内文档保留构建时的状态，实际二进制来源为上述 commit。
