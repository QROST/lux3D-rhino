# 验证记录

开发日期：2026-10-31，America/Los_Angeles。当前为开发候选，尚不是完成全部 P0 验收的发布版。

## 已执行

- 本机 Rhino `8.34.26223.11002` / arm64；Rhino 内置 .NET 8.0.14；构建 .NET SDK 8.0.129。
- Core/Provider/Rhino/Sidecar Release 构建通过，0 warning / 0 error。
- Core GLB、工作流恢复、下载重试、连接隔离、分页与提交确认测试；Provider OAuth/REST 测试。最终数量与结果随交付更新。
- 实际公开 OAuth discovery 已通过本实现解析。国际区 `/oauth/register` 已成功注册公开客户端；用户未在五分钟内返回回调，本次授权尝试超时。未取得 token，未执行 MCP 账户访问或收费操作。后续从面板重新登录。
- 自制 GLB fixture 可确定性再生，Core validator 接受，几何闭合/方向/体积及 PNG 验证通过。不是生成服务输出。

## 必须区分的验收状态

| 项目 | 状态 |
| --- | --- |
| 官方 OAuth metadata / 实际 DCR | 已通过 |
| PKCE/state/loopback/token exchange 模拟 | 已通过离线测试 |
| 用户真实 OAuth 授权、Keychain 保存、refresh | 待用户手动授权 |
| OAuth token 访问官方 MCP | 待真实授权后只读探测 |
| 本机 Yak 包安装及逐文件比对 | 待最终打包安装 |
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
