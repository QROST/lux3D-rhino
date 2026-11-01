# Lux3D for Rhino

[English](readme.md) | 简体中文

Rhino 8 macOS / Windows 开发候选：Manycore OAuth 登录、Lux3D 文字/单图任务、持久资产缓存、GLB 本地候选预览、参考高度/平面/旋转放置与独立 Block 导入。当前源码版本为 **0.1.3**，取消插件自定几何数量门槛，并针对生成网格的退化面/无效法线加入导入修复与诊断。跨平台实现已具备，Windows 实机验收待执行。

**当前版本生成模型需要所选区域的 API Key。Manycore OAuth 登录成功后仍需配置 API Key；也可以跳过插件内 OAuth 登录，直接配置 API Key。**

当前验证状态详见 [docs/verification.md](docs/verification.md)。用户已反馈在 macOS Rhino 中完成 OAuth 登录、配置 API Key 并生成真实模型；修复后的导入显示、完整材质和 Undo 仍待宿主验收。构建通过或安装文件一致不等于这些项目已经通过。

## OAuth 登录与生成授权

官方资料复核：2026-11-01（UTC）。

| 连接状态 | 当前插件能否提交生成 |
| --- | --- |
| 仅完成 Manycore OAuth 登录 | 不能；当前生成适配器不使用 OAuth token |
| 仅配置有效的区域 API Key | 可以；不要求先在插件内完成 OAuth，仍受服务权限及额度约束 |
| OAuth 和 API Key 都已配置 | 可以；生成仍通过 API Key 鉴权 |

原因有两个层面：

- **官方 REST 契约：** [鉴权文档](https://labs.aholo.ai/api-docs/en/authentication)要求将 API Key 直接放入 `Authorization` 请求头，不加 `Bearer` 前缀。[Lux3D OpenAPI](https://labs.aholo.ai/api-docs/specifications/services/lux3d/openapi.en.json)是生成接口契约。OAuth access token 不能直接当作此 API Key 使用。
- **当前插件的集成范围：** 生成、远程任务查询和图片上传使用 REST/API Key。OAuth 目前用于官方浏览器授权、会话保存及只读 MCP 工具清单探测；插件未实现通过 OAuth/MCP 提交生成，也未实现 OAuth token 换取 API Key。官方公开的[本地 SDK MCP 文档](https://labs.aholo.ai/api-docs/en/mcp)同样要求 API Key；它与插件探测的远程 OAuth MCP 不是同一接入方式。

官方仓库发布的[远程 MCP 连接契约](https://github.com/manycore-research/Aholo-Lux3D/blob/ac93a14bc84b501d167f907d8d4fcc22c041a88a/plugins/common/aholo-lux3d/references/mcp/connection.json)也声明使用原始区域 API Key。因此，目前不能把改走 MCP 当作已验证的免 Key 生成方案。

因此，“登录成功后仍需 API Key”符合当前 REST 契约和插件实现边界，**不能据此断言 Lux3D 的所有接口都不支持 OAuth 生成**。目前没有核实到可直接替代本插件 REST 鉴权的公开 OAuth 生成契约；登录成功或读到 MCP 工具清单，也不能证明生成权限或额度。

开始生成：

1. 在面板“连接”页选择国际区或中国区。
2. 从对应官方区域平台取得 API Key；国际区见 [Aholo Labs API Keys](https://labs.aholo.ai/api-keys)及[官方入门说明](https://labs.aholo.ai/api-docs/en/quickstart)。两区 Key 不互通。
3. 在密码框输入 Key，点击“设置 API Key”，选择仅本次会话使用或保存至系统凭证存储。此验证只查询任务列表，不创建生成任务，也不检查剩余额度。
4. 回到“生成”页提交任务。OAuth 登录为可选操作；本地 GLB 候选预览和放置不需要 OAuth 或 API Key。

## 本机安装

需要 Rhino **8.32 或更新版本**、.NET 8 SDK（构建）、.NET 8 Runtime（后台进程）和 Python 3.9+（打包与夹具检查）。Rhino 使用 NETCore 模式；Windows 的旧 NETFramework 模式不支持本插件。[Rhino 官方运行时说明](https://www.rhino3d.com/docs/guides/netcore/)介绍了 `SetDotNetRuntime` 和重启步骤。

| 平台 | 实现范围 | 当前验收状态 |
| --- | --- | --- |
| macOS，Apple Silicon / Intel | Keychain、系统浏览器 OAuth、Mac Yak 构建安装 | Apple Silicon 用户已开始真实生成测试；修复后的导入由用户继续验收，Intel 待验证 |
| Windows x64 | 用户 DPAPI 凭证加密、系统浏览器 OAuth、PowerShell Yak 构建安装 | 代码适配完成；Windows 构建、安装与宿主测试待执行 |

以下命令构建和安装当前源码；自动安装后，由用户保存文档并重启 Rhino 加载新版本。

macOS（在仓库目录内运行）：

```bash
bash scripts/build-package.sh
bash scripts/install-local.sh
```

Windows（在仓库目录内运行 Windows PowerShell 5.1 或更新版本）：

```powershell
.\scripts\build-package.ps1
.\scripts\install-local.ps1
```

构建脚本默认执行离线测试；仅准备未测试候选可分别使用 `--skip-tests` / `-SkipTests`。两平台生成各自的 `mac` / `win` Yak 包，安装脚本检查平台、版本及包哈希，再逐文件比对。包安装到独立的 `lux3d-rhino/<版本>` 目录，不发布至包服务器。用户自行打开或重启 Rhino，运行 `Lux3DPanel`。

后台启动解析 `DOTNET_ROOT_<架构>`、`DOTNET_ROOT`、系统安装目录与 PATH，检查是否有 .NET 8；不依赖 Rhino GUI 继承终端环境。缺少 Runtime 会给出安装提示。路径支持空格与非 ASCII 字符。

## Manycore OAuth 登录（可选）

在“连接”页选择国际区或中国区，点击“Manycore OAuth 登录”，在系统浏览器完成官方授权。插件使用官方资源/授权服务器发现、动态公开客户端注册、PKCE S256、随机 state 与本机 loopback 回调。账号密码只在官方页面输入。

macOS 令牌存入产品独立的 Keychain 条目；Windows 使用当前用户的 DPAPI 加密后写入受限访问目录，支持较长的 OAuth 会话，不受 Credential Manager 单条凭证大小限制。凭证不进入 `.3dm`、任务历史、明文配置或日志。断开清除本机凭证，官方远程授权应在账户网站管理。登录失败、取消或超时不创建生成任务。

开发时可用纯命令行开始登录，终端只打印授权链接和脱敏结果，由用户手动打开链接：

```bash
dotnet src/Lux3D.Sidecar/bin/Release/net8.0/Lux3D.Sidecar.dll --oauth-login global
```

官方 OAuth 元数据目前只声明 `get_user_info` scope；该声明本身不能证明远程 MCP 可用的生成能力。登录后的自动探测只初始化 MCP 并读取工具清单，不调用生成工具。详细发现端点、鉴权区别和验证边界见 [provider-contract.md](docs/provider-contract.md)。

## 生成与候选

选择文字或单张 PNG/JPEG，当前面板模型为 `G1-Turbo`、面数 10000–300000。`G1` 的 ZIP 输出路径尚未支持，因此未在面板开放。只有点击“生成”才提交收费任务。先写任务记录再提交；请求结果未知不会自动重发。查询、下载失败可继续查询原任务，原区域与连接保持绑定。历史每页 30 条，原记录完整保留。

演示模式默认勾选“生成完成后放入当前文档”：提交时固定目标文档、放置点、高度与旋转，结果校验完成后创建独立 Block 并缩放到模型。目标文档必须仍打开、活动且可写；关闭面板会取消该自动意图。已生成的任务可在任务页点击“放入当前文档”，必要时只恢复原任务的下载，不再次生成。同一任务反复点击会定位已有 Block，不重复插入；Undo 后不会自动重插。

也可运行 `Lux3DPlaceTask <操作 GUID>` 直接放入一条已有任务，默认采用当前 CPlane、约一米参考高度。命令先结束，再异步导入，以保持独立 Undo 记录。用户可取消自动放置，在任务页加入至多 3 个候选，或加载本地 GLB，调整高度、放置点和旋转后“接受当前候选”。候选切换只改变临时显示。

插件不设置顶点、三角面、网格数量、节点深度、GLB 文件大小或贴图像素总量的业务上限，也不按生成目标面数推断导入容量。保留容器、字节范围、索引、引用、循环和哈希正确性检查；实际导入结果由 Rhino 返回。当前支持嵌入资源的静态网格，尚未支持的 required extensions、动画与蒙皮会明确报告。系统内存和运行时错误与格式无效分别处理，不承诺无限容量。结构校验失败的结果保存具体原因码及诊断副本，不作为可导入模型。完整 PBR 视觉及纹理迁移验收单列，预览目前显示几何和基础颜色。

若已生成的网格含退化面或无效法线，0.1.3 会在 Rhino 原生导入后修复本次导入网格的副本，再检查有效性；原 GLB 和有效几何保留，不做减面。成功提示会显示修复数量。放置结果与错误同时写入 Rhino 命令历史；各阶段的本地诊断保存在任务目录下 `host-diagnostics/`，不记录提示词、凭证或下载 URL。没有取得当次原生导入结果时，不把离线修复验证当成宿主显示成功。

## 用户手动执行宿主自测

请在 Rhino 命令栏运行（只创建独立测试文档，不导入当前用户文档）。将下列绝对路径替换为本机仓库及输出目录；Windows 示例可使用 `C:/path/to/lux3D-rhino/...`：

```text
Lux3DSelfTest "/path/to/lux3D-rhino/fixtures/asymmetric-pbr.glb" "/path/to/lux3D-rhino/artifacts/rhino-selftest"
```

自测报告和 `.3dm` 写入新的 run 目录，包含加载环境、尺度/平面、候选、Block、重放、Undo/Redo、保存重开结果。真实 PBR 视觉、实际重启后重开、账户登录与收费生成需真人参与验收；已报告的 macOS OAuth/API Key 生成成功不代表全部平台和链路都已通过。

## 数据与开发

后台状态位于 .NET `LocalApplicationData/Lux3D-Rhino`；macOS 本机实测为 `~/Library/Application Support/Lux3D-Rhino`，Windows 对应 `%LOCALAPPDATA%\Lux3D-Rhino`。`tasks/` 不含凭证，`inputs/` 保存提交图片快照，`assets/` 保存 SHA-256 命名的不可变 GLB。没有自动上传遥测或清理缓存。

```bash
dotnet build Lux3D.Rhino.sln -c Release
dotnet test Lux3D.Rhino.sln -c Release --no-build
python3 scripts/create-fixtures.py --check
```

原始产品验收基线保存在 [PRD.md](PRD.md)，未改写为“已完成”。来源及架构见 [NOTICE](NOTICE)、[architecture.md](docs/architecture.md) 与 [provider-contract.md](docs/provider-contract.md)。两份 README 保持同等范围；当前插件界面和详细开发文档主要为中文。
