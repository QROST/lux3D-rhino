# Lux3D for Rhino

Rhino 8 macOS / Windows 开发候选：Manycore OAuth 登录、Lux3D 文字/单图任务、持久资产缓存、GLB 本地候选预览、参考高度/平面/旋转放置与独立 Block 导入。当前源码版本为 **0.1.2**，修复了 30 万面生成结果因顶点预算过低被拒绝的问题，并加入直接放入文档的演示流程。跨平台实现已具备，Windows 实机验收待执行。

当前验证状态详见 [docs/verification.md](docs/verification.md)。**构建通过或安装文件一致不等于真实账户及 Rhino 宿主验收通过。** OAuth 登录令牌与 REST API Key 是两种连接；当前生成适配器使用所选区域的 API Key，不把 OAuth token 当作 API Key。

## 本机安装

需要 Rhino **8.32 或更新版本**、.NET 8 SDK（构建）与 .NET 8 Runtime（后台进程）。Rhino 使用 NETCore 模式；Windows 的旧 NETFramework 模式不支持本插件。[Rhino 官方运行时说明](https://www.rhino3d.com/docs/guides/netcore/)介绍了 `SetDotNetRuntime` 和重启步骤。

| 平台 | 实现范围 | 当前验收状态 |
| --- | --- | --- |
| macOS，Apple Silicon / Intel | Keychain、系统浏览器 OAuth、Mac Yak 构建安装 | Apple Silicon 用户已开始真实生成测试；修复后的导入由用户继续验收，Intel 待验证 |
| Windows x64 | 用户 DPAPI 凭证加密、系统浏览器 OAuth、PowerShell Yak 构建安装 | 代码适配完成；Windows 构建、安装与宿主测试待执行 |

以下命令构建和安装当前源码；自动安装后，由用户保存文档并重启 Rhino 加载新版本。

macOS：

```bash
cd /Users/addincui/Github/lux3D-rhino
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

## Manycore 登录

在“连接”页选择国际区或中国区，点击“Manycore OAuth 登录”，在系统浏览器完成官方授权。插件使用官方资源/授权服务器发现、动态公开客户端注册、PKCE S256、随机 state 与本机 loopback 回调。账号密码只在官方页面输入。

macOS 令牌存入产品独立的 Keychain 条目；Windows 使用当前用户的 DPAPI 加密后写入受限访问目录，支持较长的 OAuth 会话，不受 Credential Manager 单条凭证大小限制。凭证不进入 `.3dm`、任务历史、明文配置或日志。断开清除本机凭证，官方远程授权应在账户网站管理。登录失败、取消或超时不创建生成任务。

开发时可用纯命令行开始登录，终端只打印授权链接和脱敏结果，由用户手动打开链接：

```bash
dotnet src/Lux3D.Sidecar/bin/Release/net8.0/Lux3D.Sidecar.dll --oauth-login global
```

官方 OAuth 元数据当前只声明 `get_user_info` scope，MCP 接口返回 Bearer challenge。当前不声称已验证 OAuth 可以替代 REST API Key 发起生成。请在相应官方区域平台取得 API Key，在面板密码框输入并“设置 API Key”；验证只查询任务历史，不消费生成额度。可选择仅会话或系统凭证存储。

## 生成与候选

选择文字或单张 PNG/JPEG，当前面板模型为 `G1-Turbo`、面数 10000–300000。`G1` 的 ZIP 输出路径尚未支持，因此未在面板开放。只有点击“生成”才提交收费任务。先写任务记录再提交；请求结果未知不会自动重发。查询、下载失败可继续查询原任务，原区域与连接保持绑定。历史每页 30 条，原记录完整保留。

演示模式默认勾选“生成完成后放入当前文档”：提交时固定目标文档、放置点、高度与旋转，结果校验完成后创建独立 Block 并缩放到模型。目标文档必须仍打开、活动且可写；关闭面板会取消该自动意图。已生成的任务可在任务页点击“放入当前文档”，必要时只恢复原任务的下载，不再次生成。同一任务反复点击会定位已有 Block，不重复插入；Undo 后不会自动重插。

也可运行 `Lux3DPlaceTask <操作 GUID>` 直接放入一条已有任务，默认采用当前 CPlane、约一米参考高度。命令先结束，再异步导入，以保持独立 Undo 记录。用户可取消自动放置，在任务页加入至多 3 个候选，或加载本地 GLB，调整高度、放置点和旋转后“接受当前候选”。候选切换只改变临时显示。

插件不设置顶点、三角面、网格数量、节点深度、GLB 文件大小或贴图像素总量的业务上限，也不按生成目标面数推断导入容量。保留容器、字节范围、索引、引用、循环和哈希正确性检查；实际导入结果由 Rhino 返回。当前支持嵌入资源的静态网格，尚未支持的 required extensions、动画与蒙皮会明确报告。系统内存和运行时错误与格式无效分别处理，不承诺无限容量。结构校验失败的结果保存具体原因码及诊断副本，不作为可导入模型。完整 PBR 视觉及纹理迁移验收单列，预览目前显示几何和基础颜色。

## 用户手动执行宿主自测

请在 Rhino 命令栏运行（只创建独立测试文档，不导入当前用户文档）：

```text
Lux3DSelfTest "/Users/addincui/Github/lux3D-rhino/fixtures/asymmetric-pbr.glb" "/Users/addincui/Github/lux3D-rhino/artifacts/rhino-selftest"
```

自测报告和 `.3dm` 写入新的 run 目录，包含加载环境、尺度/平面、候选、Block、重放、Undo/Redo、保存重开结果。真实 PBR 视觉、实际重启后重开、账户登录与收费生成仍需真人参与验收。

## 数据与开发

后台状态位于 .NET `LocalApplicationData/Lux3D-Rhino`；macOS 本机实测为 `~/Library/Application Support/Lux3D-Rhino`，Windows 对应 `%LOCALAPPDATA%\Lux3D-Rhino`。`tasks/` 不含凭证，`inputs/` 保存提交图片快照，`assets/` 保存 SHA-256 命名的不可变 GLB。没有自动上传遥测或清理缓存。

```bash
dotnet build Lux3D.Rhino.sln -c Release
dotnet test Lux3D.Rhino.sln -c Release --no-build
python3 scripts/create-fixtures.py --check
```

原始产品验收基线保存在 `PRD.md`，未改写为“已完成”。来源及架构见 `NOTICE`、`docs/architecture.md` 与 `docs/provider-contract.md`。
