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

## 0.1.0 当时的验收状态（后续进展见下）

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

## 0.1.2 真实任务修复与直接放置

用户已手动在本机 Rhino 登录 OAuth、配置 API Key 并生成，任务操作编号 `e0056392-4a2c-4f03-b044-41bf201a4143`，原服务任务 `4545638`。此次修复仅查询、下载该原任务，没有再次创建生成任务。

原因已用实际下载文件复现：原模型为 **272,630 顶点 / 283,986 三角面**，文件 **20,939,868 字节**，不含 required extensions；原校验器因插件自定的 250,000 顶点预算返回 `The GLB mesh exceeds the pre-native vertex limit.`。这不是 OAuth 或 API Key 验证失败，也不是 Rhino 返回的几何容量限制。模型 SHA-256：`7aff9189207038101ef61a5230639f96a9a8056511288f39d85e0b7c4c3d268e`。

按用户要求，Core/下载器/Host 均撤销插件自定的几何数量、文件体积、节点深度、JSON/数组数量及贴图尺寸/像素预算；生成参数继续遵守 Lux3D API。保留格式、实际字节范围、索引、资源引用与循环验证；以实际 Rhino 导入和系统资源决定能否放入。内存不足独立报告，格式错误保留具体原因与诊断副本。

直接演示入口为 `_Lux3DPlaceTask e0056392-4a2c-4f03-b044-41bf201a4143`：读取已缓存的原任务，按 1 米参考高度换算至当前文档单位，放到当前 CPlane，创建独立 Block 并定位视图。面板也提供“放入当前文档”，新生成任务默认启用完成后自动放置。自动意图绑定提交时的文档/会话/单位/位置；关闭面板后由用户手动继续。稳定导入编号防重复，一次 Undo；撤销/删除后当前会话内不自动重放，保存重开时可识别仍存在的原 Block。

本轮不使用 Computer Use、不自动操作 Rhino。真实宿主的 GLB 解码、视觉/PBR、Undo/Redo 与 Windows 实机仍须手动验收；离线策略或包安装验证不能替代这些结果。

本轮实际完成的检查：

- 二进制源码提交 `6868f72528e150afdc6bc7682176c52130268ca5`（包含校验修复 `809c148`）。Release 构建 0 warning / 0 error，Core **91/91**、Provider **39/39**，共 **130/130** 通过；两个自制 fixture 的可再生校验通过。
- 真实原 GLB 已按最终无插件数量门槛策略通过 Core 校验。回归覆盖原模型规模、超过旧门槛的几何/数组/贴图、20,000 层节点链、环与非法索引；不等于执行原生解码或视觉检查。
- Rhino 官方 Yak 安装 **0.1.2** 成功，**25/25** 安装文件 SHA-256 与包一致。安装目录 `~/Library/Application Support/McNeel/Rhinoceros/packages/8.0/lux3d-rhino/0.1.2`。
- 使用该安装目录内的 Sidecar 运行 `--recover-task e0056392-4a2c-4f03-b044-41bf201a4143`，原服务任务 `4545638` 从本地 `Rejected` 恢复为 **Ready**，`error` / `validationCode` 为空，缓存哈希与上述原文件一致。已保留恢复前的本地记录，没有调用创建接口。
- 已安装 Sidecar 的 `getTask` / `hasUnknown` 两次私有协议请求通过，版本和请求 ID 匹配、stderr 为空。默认沙箱启动未成功，随后在正常本机权限下完成检查；该检查不调用 Provider。
- 演示文件 `artifacts/demo/longmen-buddha.glb` 与任务缓存哈希一致；证据为 `artifacts/glb-diagnostic/validation-fixed.json`、`artifacts/glb-diagnostic/recovery-installed.json` 和 `artifacts/install-verification-mac.json`。这些本地文件不包含 API Key、token 或签名下载 URL，也不提交生成资产到 Git。

最终已安装包为 `artifacts/package-mac.22HYbo/lux3d-rhino-0.1.2-rh8_32-mac.yak`，SHA-256 `7412fb904a2f71c862669c67f053e05da3f459eea79cdd0410270d266256ea40`。包内文档保留构建时状态，此段为安装后的补录。用户需保存当前文档并重启 Rhino 后执行上述演示命令；尚未声称模型已显示在 Rhino 中。

## 0.1.3 原生网格兼容性与导入诊断

用户反馈 0.1.2 点击“放入当前文档”后加载但未显示。当次插件未保存阶段日志，因此目前不能认定唯一失败阶段；已向用户索取面板/命令历史提示。本地原任务仍为 Ready，缓存 SHA-256 未变。

实际 GLB 逐项检查：272,630 顶点、283,986 三角面，无非有限坐标或越界索引；有 **5 个重合坐标退化面**与 **39 个零长度法线**。使用官方 `rhino3dm 8.35.0` 在隔离环境重建同一网格，未启动 Rhino：几何校验报 `m_F[35599] has degenerate double precision vertex locations`，附上原法线后报 `m_N[1072] is not a unit vector (length = 0)`。对副本执行 `CullDegenerateFaces` 删除 5 面，再 `ComputeNormals` 后有效性通过；保留 272,630 顶点、283,981 面。证据 `artifacts/glb-diagnostic/rhino3dm-validity.json`。这是确定的网格兼容性缺陷；尚不能证明 Rhino 原生 GLB importer 在用户此次操作中是否先自行修复或直接拒绝。

另已核实两个独立实现错误：`RhinoDoc.IsLocked` 表示本实例持有文件写锁，不能作为不可写判据；默认 `ObjectAttributes.LayerIndex` 为 0，不能代替当前可见图层。0.1.3 修正写锁检查并明确模型空间、当前图层；活动布局纸面需切换模型视口再放置。原生预检、网格修复、正式导入、Block 和视图阶段记录到本地 JSON，错误同时写入命令历史，避免只显示短暂底部状态。

本轮继续只自动构建与安装，不控制 Rhino 界面、不创建生成任务。原生 Import、最终显示、完整材质及 Windows 实机仍保留为待验证项。

0.1.3 已执行记录：

- 二进制源码提交 `b6e9bedd26dcb36e99381b46846360a1581f219f`。Release 构建 **0 warning / 0 error**，Core **97/97**、Provider **39/39**，合计 **136/136** 通过；两个固定 GLB fixture 再生检查通过。
- 主线程独立重建并复核实际生成网格，确认原网格无效、清理 5 面并重算法线后有效，顶点数不变；证据 `artifacts/glb-diagnostic/mesh-repair-root-verification.json`。新增 Rhino 宿主自测覆盖源网格、顶点、UV、颜色不变及有效网格不修复；该宿主自测只完成编译，未执行。
- Rhino 官方 Yak 安装 **0.1.3** 成功，**25/25** 已安装文件与包完全一致。包为 `artifacts/package-mac.7LKuwX/lux3d-rhino-0.1.3-rh8_32-mac.yak`，SHA-256 `12fb5bc09cc19c81a7731d60777aacbc4d03a464e5bf6c595af5b26c7e03b373`。
- 已安装 Sidecar 的只读 `getTask` 协议验证通过；原任务仍 Ready、原资产 SHA-256 未变，stderr 为空。本次未访问账户或发起生成，证据 `artifacts/glb-diagnostic/installed-013-smoke.json`。

用户需保存文档并重启 Rhino，确认命令历史显示 `Lux3D 0.1.3.0 已加载`，在模型视图重新放入原任务。模型实际显示、PBR 与单次 Undo 尚待用户手动验收；若失败，面板和命令历史会显示本地 `host-diagnostics` 报告路径。此段为安装后补录，包内文档保留构建时状态。

## 2026-11-01 用户复测与 0.1.4-beta.1 发布候选

用户明确回复：重启加载 0.1.3 后，原佛像模型“已正常显示”。据此将该版本、该模型在本机 macOS Rhino 的直接放置及可见性记为用户实测通过。此反馈不等于新版本交互、完整 PBR、Undo/Redo、纹理迁移、其他区域或 Windows 验收通过。

0.1.4-beta.1 的改动：

- 生成页直接设置高度/位置/角度，API Key 必需与 OAuth 可选的区别同步进入界面；支持单独移除候选。
- 实际导入阶段、耗时与会话内结果/错误分开显示；后台查询不覆盖导入错误，查询恢复只清除查询类错误。
- 当前候选复用网格/材质缓存，连续输入合并为 150ms 刷新；不增加几何数量限制，不改原始几何精度。首次载入和最终提交仍分别验证快照。
- 直接放置的文件读取、结构校验、哈希与私有快照准备移到后台；原生 Rhino 预检和文档提交留在 UI 线程，进入原生阶段与提交前保留文档/会话/单位/意图检查。诊断新增单调累计和阶段间耗时。

发布前检查：Release 全量构建 **0 warning / 0 error**；Core **102/102**、Provider **39/39**，合计 **141/141** 离线测试通过，两个夹具的确定性再生检查通过。新增测试覆盖后台快照与源文件隔离、哈希/格式拒绝、清理与 Unix 权限、阶段计时和失败观察者隔离。默认沙箱阻止测试运行器的 loopback socket，随后在正常本机权限下完成测试；未调用真实生成服务。

新增宿主自测覆盖 20 次预览参数变换的缓存身份、源网格 CRC、包围盒/矩阵、首次校验及 Clear/Dispose；本轮仅编译，未运行 Rhino。没有使用 Computer Use，也没有新增付费任务。原生导入仍可能短暂阻塞界面，尚未测得可对外宣称的性能倍数。

作者元数据参考 Tripo 项目的维护者身份（当前公开昵称为 `qrost`）；公司品牌依据 [qrost 公开简介](https://github.com/QROST)采用 **CuriousArc**，不添加未经核实的法律实体后缀，保留 NOTICE 中原始版权归属。公开发行按 **macOS / Rhino 8.32+ 预览版**准备，包名 `lux3d-rhino`、版本 `0.1.4-beta.1`；Windows 代码和构建入口保留待实机验收，不发布 `any` 包。最终包与上传结果在完成后另行记录；这段不代表已上传。

## 2026-11-01 Yak 发布与本机安装

`lux3d-rhino 0.1.4-beta.1` 已发布到官方 Yak 服务器，发行标签为 `rh8_32-mac`，`prerelease: true`。发布团队为 **CuriousArc**，维护者为 **qrost**；本记录不再展示历史包中的个人姓名。可在 macOS Rhino 的 `PackageManager` 中启用预览版后搜索安装；本机已完成安装，保存文档并重启 Rhino 后加载新版本。

- 二进制来源：功能提交 `91d4dd7`，发布元数据提交 `454e05d4c8808da3f9dca8299861a0745ab486bd`；打包时工作树干净。最终打包流程再次通过 Release 构建（0 warning / 0 error）、**141/141** 离线测试和夹具再生检查。
- 包名：`lux3d-rhino-0.1.4-beta.1-rh8_32-mac.yak`，大小 **332,695 字节**，SHA-256 **`e89d6e7cd704c997519526dcf1b1a4d5256ac198c4211c1b101f18e57e1c6d4f`**。
- 官方 Yak 安装成功，**26/26** 文件与包内内容逐一匹配。安装目录为 `~/Library/Application Support/McNeel/Rhinoceros/packages/8.0/lux3d-rhino/0.1.4-beta.1`；证据为 `artifacts/install-verification-mac.json`。
- 上传命令在会话中断前已发出，其退出状态未保留。恢复后独立查询[公开版本接口](https://yak.rhino3d.com/versions/lux3d-rhino)，确认版本、作者、预览标记和 macOS 分发文件；官方客户端 `yak search --all --prerelease lux3d-rhino` 返回 `lux3d-rhino (0.1.4-beta.1)`，未重复上传。
- 从服务器提供的[公开下载地址](https://files.mcneel.com/yak/packages/lux3d-rhino-0.1.4-beta.1-rh8_32-mac.yak)重新下载，SHA-256 与上述本地包完全一致。版本响应与下载证据保存在 `artifacts/yak-publication/`。

本段为发布后补录，不改变已上传归档；包内文档保留构建时的发布候选状态。安装及公开下载校验不替代 Rhino 新版 UI、完整 PBR、Undo/Redo 或 Windows 的宿主验收；本轮未自动控制 Rhino，也未新增付费生成任务。

## 2026-11-01 原生插件属性修复（0.1.4-beta.2）

用户截图确认 beta.1 的 Rhino Plug-in Properties 中 Publisher、Description、Web site 为空。源码复核发现 Yak `authors` 与 .NET `AssemblyCompany` 已设置，但漏掉了 Rhino 专用的 `PlugInDescription` 属性，且没有 `AssemblyDescription`。此前的发布包作者核验没有覆盖这个原生属性页，这是已确认的元数据遗漏。

beta.2 补齐 `DescriptionType.Organization = CuriousArc`、`DescriptionType.WebSite = https://github.com/QROST/lux3D-rhino`；通过 SDK 的 `Description` 生成程序集说明，包含公司、维护者署名、功能及 API Key 要求。使用 RhinoCommon 的[开发者信息属性](https://developer.rhino3d.com/api/rhinocommon/type/rhino.plugins.plugindescriptionattribute/)；未核实的地址、国家、电话和邮箱不填充。插件 GUID 和生成/导入代码不变。

编译已通过，0 warning / 0 error；直接读取 RHP 的 ECMA-335 程序集元数据验证发布者、网站、说明、完整预发布版本及原 GUID，避免只检查源码或 Yak 清单。宿主属性页的数字版本仍为 `0.1.4.0`，完整 `0.1.4-beta.2` 版本见 Yak 和插件加载消息；[AssemblyVersion 不支持预发布后缀](https://developer.rhino3d.com/en/guides/scripting/projects-create/)。加载新版必须重启 Rhino，不清理用户配置或操作桌面。实际属性页视觉结果仍待用户确认；最终安装和发布结果另行补录。

beta.2 发布后补录：

- 二进制来源提交 `506bf9e81f28d54a522d607fcf34136528b2f8a3`；正式打包构建 0 warning / 0 error，**141/141** 测试及两个夹具再生检查通过。
- 本机官方 Yak 安装成功，**26/26** 文件匹配；直接检查已安装 `0.1.4-beta.2/Lux3D.Rhino.rhp`，发布者为 `CuriousArc`、网站为项目地址、说明包含当时的维护者署名（本记录不再展示个人姓名），完整版本及 GUID 正确。
- Yak push 返回成功；独立公开版本查询确认 `0.1.4-beta.2` / `rh8_32-mac` / prerelease。重新下载[公开分发包](https://files.mcneel.com/yak/packages/lux3d-rhino-0.1.4-beta.2-rh8_32-mac.yak)，**335,169 字节**，SHA-256 `909cf934d90e721043fdf65d95c8558f768d425ec1ffb14a340a00b3cc18ff10`，与本地安装来源包一致。
- 元数据、安装、版本响应及下载证据存于 `artifacts/yak-publication-beta2/`；此段为发布后文档补录，不更改已发布归档。尚未代替用户重启后的属性页视觉确认。


## 2026-11-01 公开署名统一（0.1.4-beta.3）

按用户要求，当前 README、其他文档、PRD、NOTICE、构建作者、Yak 清单及 Rhino 插件说明统一使用团队名 **CuriousArc** 与昵称 **qrost**。移除公开文档中的个人姓名和本机用户名路径示例；保留实际 GitHub 仓库地址、Tripo 来源、Apache-2.0 许可证及参考提交。历史验证记录只省略个人姓名，不声称此前发布的归档内容已经改变。

本次仅调整文档与署名元数据，生成、凭据和几何导入逻辑不变。旧 Git 提交和已发布的 beta.1/beta.2 包仍是历史版本，本次不重写历史或撤下既有版本。beta.3 的安装和发布结果在完成后补录。

beta.3 本轮交付记录：

- 源码提交 `5e9d30b9e0940bfce35c8c9f705d263086dc2662`。受版本控制的文本及最终归档的 9 份文档/署名文本扫描均通过，无旧个人姓名或本机用户名路径；原有截图亦完成身份信息检查。发布归档仅收录已提交的文档素材，未纳入工作区并行新增的未提交素材。
- Release 构建 0 warning / 0 error，Core **102/102**、Provider **39/39**，共 **141/141** 测试通过；两个夹具再生检查通过。
- 最终包 `lux3d-rhino-0.1.4-beta.3-rh8_32-mac.yak`，大小 **2,498,294 字节**，SHA-256 `2fc0e1644b6ec68d05dbd9ff7ce5ad13c88fa776e0d61a237bac46c1655a4746`。本机安装成功，**27/27** 文件匹配；已安装 RHP 的发布者为 `CuriousArc`，说明署名为 `CuriousArc and qrost`，GUID 未变。
- **Yak 发布尚未完成**：两次上传均返回服务器 HTTP 500；每次失败后独立查询公开版本接口，均只有 beta.1/beta.2，没有 beta.3。因此不能把本机安装成功视为公开发行成功。证据位于 `artifacts/yak-publication-beta3/`。本轮未推送 GitHub 源码，公开文档需后续同步。


## 2026-11-06 English/Chinese UI release candidate (0.1.4-beta.4)

This macOS / Rhino 8.32+ preview includes the language-selection implementation from `fdc3598`: English is the default; a valid Rhino UI language takes precedence, with system UI language as the fallback. Chinese locales use Simplified Chinese. The host, private background process and OAuth callback page use the selected language. Numeric formatting keeps the user's regional settings. Restart Rhino after changing its UI language. Native language/layout acceptance remains pending; existing macOS placement feedback does not establish acceptance of this change.

The preceding implementation checks passed: Release build with **0 warnings / 0 errors**, Core **123/123** and Provider **39/39** offline tests. English, Simplified Chinese and Traditional Chinese sidecar locale cases produced the expected translated offline error responses. These checks did not use real credentials, call generation services or control Rhino. The final package build will rerun the automated checks; installation and Yak publication results will be recorded separately after completion.

This release retains **CuriousArc / qrost** attribution. A regional API Key is still required for generation; optional Manycore OAuth sign-in does not replace it. Full PBR, Undo/Redo, save/reopen and Windows host acceptance remain pending. No Windows distribution is included in this release. The official Yak search on 2026-11-06 returned beta.1 and beta.2; beta.4 was unused, and beta.3 remains a historical local-only package.


## 2026-11-13 公开状态复核与 beta.5 产品加固

只读复核官方 Yak 版本接口，`0.1.4-beta.4` 已于 `2026-11-06T22:34:42Z` 发布，分发为 `rh8_32-mac`、prerelease。公开包 **2,741,242 字节**，SHA-256 **`94a6e523824e191291a55e1b8f53384c393d35c3d6ff7a168558609e1a521cd5`**，与本地 beta.4 记录相符。此前本文件只写到发布候选，现补上公开状态。GitHub main 为 `29a73abbdd50587036930ba6c45436dcd6da2ea9`，Releases 和 tags 公开列表均为空；源码同步不等于存在 GitHub Release。

本轮在原干净工作树上准备 **0.1.4-beta.5 本地候选**：

- 保留明确提交拒绝的失败状态，持久化缺失回执状态；预先取消不产生虚假提交，查询结果必须与原任务 ID 一致。
- 损坏或身份错配的历史记录不再阻断正常历史，使用不可读占位并保留原文件；仍保守阻止未经明确确认的后续生成，不能刷新或保存占位覆盖原始记录。
- 畸形服务回执不能被误当作确定拒绝；已开始生成但回执丢失仍防重复提交。上传未进入生成阶段就失败或取消，则不会错误声称可能已创建收费任务。
- 候选接受检查活动/可写文档、纸空间和运行命令；关闭面板时延迟清理仍在原生导入中使用的资源，预检返回后不恢复已经关闭的候选。
- macOS/Windows 打包共用明确文件清单，排除未列入的私人文档和旧构建残留；离线回归覆盖污染、路径穿越、符号链接和缺失文件。
- 加入双平台 GitHub Actions 构建/离线测试工作流；本轮尚未推送或执行在线 CI。

原生自测扩充了资源使用中关闭与嵌套 Undo 拒绝检查；仅编译，不标为宿主执行通过。当前没有自动控制 Rhino、真实账户调用或新增收费任务。详细验收与真实录屏要求见 [hackathon-delivery.md](hackathon-delivery.md)。beta.5 仍未公开发布；最终自动测试和包记录在完成后补录。

最终候选自动验证：Core **136/136**、Provider **68/68**，共 **204/204** 离线测试通过；**5/5** 打包隔离回归及两个 GLB 夹具再生检查通过。Release 编译 **0 warning / 0 error**。这些是本地自动检查，不等于原生宿主、Windows 或线上 CI 已验收。最终包路径与哈希保存在本地 `artifacts/latest-package-mac.json`。

beta.5 本机交付补录：最终包 `artifacts/package-mac.WkUnT2/lux3d-rhino-0.1.4-beta.5-rh8_32-mac.yak`，SHA-256 `b8ded3690d7825d394fc939ae7101459af093ce40075ccd6a02b4b42af8f60c6`。官方 Yak 客户端本机安装成功，**29/29 文件**逐一匹配；证据为 `artifacts/install-verification-mac.json`。未启动或操作 Rhino；用户保存并重启后才会加载新版。此段为包生成后的补录，不改变上述归档。源码修改尚未提交/推送，beta.5 尚未公开发布。


## 2026-11-13 P0 查询恢复补齐（beta.6 候选）

逐项核对 PRD 后补齐两项实现缺口：查询错误以持久化截止时间控制指数退避并增加抖动，429/其他非成功响应的合法 Retry-After（秒数或 HTTP 日期）传递到工作流，重启不提前查询；成功查询保留 RawStatus 并清除失败计数。未知服务状态不作为成功下载。退避仅作用于查询原 task ID，不自动创建或重试生成。面板显示原始状态及下一次查询时间，后台跳过尚未到期任务。

原生保存重开自测也已加强：不能以 missing==missing 判通过，PBR 夹具至少应存在可解析纹理证据。此检查仍不证明四通道映射、Rendered 视觉或跨进程资源迁移；fullP0Acceptance 保持 false，实际宿主尚未执行。

本轮使用独立 beta.6 版本，保留已安装 beta.5 和历史归档。公开发布仍待授权；新增测试及安装证据随后补录。

最终 beta.6 本地验证与安装：Release 编译 **0 warning / 0 error**，Core **146/146**、Provider **73/73**，合计 **219/219** 测试通过；Python 打包回归 **5/5**、两个固定 GLB 夹具再生检查通过。包为 `artifacts/package-mac.0CntHs/lux3d-rhino-0.1.4-beta.6-rh8_32-mac.yak`，SHA-256 `6898d0d740960669c403428d5faec8a76e9a2f1b22f61d3a9f2bb08efe01493e`，本机安装后 **29/29** 文件匹配。未运行 Rhino，未调用真实服务，未公开上传；本段为归档生成后补录。源码仍为未提交修改。

## 2026-11-13 用户授权后的公开发布

源码提交 `56f4c15de5ce9f00b715afe233198c565a342fbe` 已推送 GitHub main，并从该干净提交重建 beta.6。Release 编译零警告/错误，219 项 .NET 测试、5 项打包测试、两个固定夹具检查通过。正式发布包 **2,756,195 字节**，SHA-256 **`b9becb3d34c71604c2322737d0405249605abe997dc76d885767f946efcdee35`**；这个哈希与先前未提交源码构建的本机候选不同，请以此标识公开包。

Yak push 返回成功，官方公开版本接口确认 `0.1.4-beta.6` / `rh8_32-mac` / prerelease，公开下载重新计算哈希与发布包一致。GitHub 已创建 [v0.1.4-beta.6 Release](https://github.com/QROST/lux3D-rhino/releases/tag/v0.1.4-beta.6)，附同一 Yak 包和 SHA-256 文件，tag 指向上述源码提交。发布证据保存在 `artifacts/yak-publication-beta6/`。

首次 Windows 线上 CI 的构建与 .NET 测试通过，但 fixture manifest 的逐字节检查失败；两个 GLB 哈希均正确。原因是 Git checkout 的 CRLF 转换改变了 JSON 清单字节，补充 `.gitattributes` 固定该文件 LF，同时明确 GLB 为二进制。模拟 `core.autocrlf=true` checkout 的字节一致性检查通过。这个仓库检出规则修复不改变已发布 macOS 二进制或资产，不重写已发布 tag。

公开预览版发布完成不等于新版 Rhino 宿主、PBR 或 Windows 原生验收完成；这些门槛仍保留。
