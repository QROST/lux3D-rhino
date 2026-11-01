# Lux3D for Rhino

Rhino 8 macOS 开发候选：Manycore OAuth 登录、Lux3D 文字/单图任务、持久资产缓存、GLB 本地候选预览、参考高度/平面/旋转放置与独立 Block 导入。

当前验证状态详见 [docs/verification.md](docs/verification.md)。**构建通过或安装文件一致不等于真实账户及 Rhino 宿主验收通过。** OAuth 登录令牌与 REST API Key 是两种连接；当前生成适配器使用所选区域的 API Key，不把 OAuth token 当作 API Key。

## 本机安装

需要 Rhino 8、.NET 8 SDK（构建）与 .NET 8 Runtime（后台进程）。当前本机已具备。此包仅标记 macOS；未验证 Windows。

```bash
cd /Users/addincui/Github/lux3D-rhino
bash scripts/build-package.sh
bash scripts/install-local.sh
```

安装脚本校验包哈希，通过 Rhino 自带 Yak 安装到独立的 `lux3d-rhino/0.1.0`，再逐文件比对。它不发布包，不更改 Tripo 安装，不打开/关闭或操作 Rhino。请自行打开或重启 Rhino，运行 `Lux3DPanel`。

## Manycore 登录

在“连接”页选择国际区或中国区，点击“Manycore OAuth 登录”，在系统浏览器完成官方授权。插件使用官方资源/授权服务器发现、动态公开客户端注册、PKCE S256、随机 state 与本机 loopback 回调。账号密码只在官方页面输入。

令牌存入产品独立的 macOS 钥匙串条目；不进入 `.3dm`、任务历史、明文配置或日志。断开清除本机凭证，官方远程授权应在账户网站管理。登录失败、取消或超时不创建生成任务。

开发时可用纯命令行开始登录，终端只打印授权链接和脱敏结果，由用户手动打开链接：

```bash
dotnet src/Lux3D.Sidecar/bin/Release/net8.0/Lux3D.Sidecar.dll --oauth-login global
```

官方 OAuth 元数据当前只声明 `get_user_info` scope，MCP 接口返回 Bearer challenge。当前不声称已验证 OAuth 可以替代 REST API Key 发起生成。请在相应官方区域平台取得 API Key，在面板密码框输入并“设置 API Key”；验证只查询任务历史，不消费生成额度。可选择仅会话或钥匙串保存。

## 生成与候选

选择文字或单张 PNG/JPEG，当前面板模型为 `G1-Turbo`、面数 10000–300000。`G1` 的 ZIP 输出路径尚未支持，因此未在面板开放。只有点击“生成”才提交收费任务。先写任务记录再提交；请求结果未知不会自动重发。查询、下载失败可继续查询原任务，原区域与连接保持绑定。历史每页 30 条，原记录完整保留。

生成成功后下载并验证 GLB，变为“可预览”。在任务页加入至多 3 个候选，或加载本地 GLB。候选切换只改变临时显示。设定文档单位下的参考高度、放置点与当前 CPlane、平面内旋转，然后“接受当前候选”。后台完成不自动修改活动文档。

GLB 上限 64 MiB、250000 顶点、500000 三角形。仅接受嵌入资源的静态网格；未支持的 required extensions、动画与蒙皮明确拒绝。完整 PBR 视觉及纹理迁移验收单列，预览目前显示几何和基础颜色。

## 用户手动执行宿主自测

请在 Rhino 命令栏运行（只创建独立测试文档，不导入当前用户文档）：

```text
Lux3DSelfTest "/Users/addincui/Github/lux3D-rhino/fixtures/asymmetric-pbr.glb" "/Users/addincui/Github/lux3D-rhino/artifacts/rhino-selftest"
```

自测报告和 `.3dm` 写入新的 run 目录，包含加载环境、尺度/平面、候选、Block、重放、Undo/Redo、保存重开结果。真实 PBR 视觉、实际重启后重开、账户登录与收费生成仍需真人参与验收。

## 数据与开发

后台状态位于 .NET `LocalApplicationData/Lux3D-Rhino`；macOS 通常为 `~/.local/share/Lux3D-Rhino`。`tasks/` 不含凭证，`inputs/` 保存提交图片快照，`assets/` 保存 SHA-256 命名的不可变 GLB。没有自动上传遥测或清理缓存。

```bash
dotnet build Lux3D.Rhino.sln -c Release
dotnet test Lux3D.Rhino.sln -c Release --no-build
python3 scripts/create-fixtures.py --check
```

原始产品验收基线保存在 `PRD.md`，未改写为“已完成”。来源及架构见 `NOTICE`、`docs/architecture.md` 与 `docs/provider-contract.md`。
