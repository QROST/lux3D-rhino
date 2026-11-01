# Rhino 集成、离线自测与人工验收

当前交付为候选实现。编译通过不等于 Rhino、真实 Manycore OAuth、真实 Lux3D 生成、完整 PBR 或跨机器纹理迁移验收通过。用户要求手工操作 Rhino；安装脚本不应自动开关应用或操作其界面。

## 身份与宿主

- Rhino 8.32 或更新版本，macOS / Windows x64，`net7.0`；编译引用 `RhinoCommon 8.32.26160.13001`，宿主提供 RhinoCommon/Eto，不随插件分发它们。Windows 要求 NETCore 模式，旧 NETFramework 模式无法加载本程序集；参见 [官方运行时说明](https://www.rhino3d.com/docs/guides/netcore/)。
- 插件/assembly GUID：`B8B147EC-5732-4FB9-BA40-C5BB8D36C2AB`。
- 面板 GUID：`AC141E4E-8B0E-4A82-A34A-F2B0783201FB`。
- 主命令：`Lux3DPanel`；直接放置命令：`Lux3DPlaceTask <操作 GUID>`；离线自测命令：`Lux3DSelfTest`。
- Sidecar 位于 `.rhp` 同目录的 `sidecar/`；界面调用 `Lux3D.Core.Lux3DClient` 私有子进程协议，独立于 Tripo 的配置、身份和通信。
- Native GLB 预检及 Block 包装流程参考 `QROST/tripo-rhino`，源许可证为 Apache-2.0，见仓库 `NOTICE`。没有复用 Tripo 的 GUID、凭证或任务库。

构建：

```sh
dotnet build src/Lux3D.Rhino/Lux3D.Rhino.csproj
```

## 界面行为

打开 `Lux3DPanel`，面板固定绑定创建时的文档运行期编号及独立 session ID。连接页通过系统浏览器进行 Manycore OAuth，或使用单独的 API Key 模式。OAuth 登录状态与 Lux3D 生成权限分开；只有 Core 返回 `CanGenerate=true` 才启用生成。区域切换后重新检查连接；旧任务保留原区域。

生成页支持文字、单张 PNG/JPEG、模型、10,000–300,000 目标面数和材质意图。图片在本机执行有界头检查、Eto Bitmap 解码和像素访问；文字/图片选择及参数编辑不会提交生成。唯一创建入口为“生成（将消耗服务额度）”。UI 请求锁防双击；每次提交前向后台检查完整持久历史（含 Submitting/SubmissionUnknown），存在未知记录时明确提示新请求可能重复收费。确认随请求发送，后台在写入前再次核对。

任务历史每页 30 条，支持上一页/下一页；新生成回到第一页，查询保持当前页。任务页查询选中任务；面板可见时最多每 5 秒查询一次在途任务。停止等待、隐藏或关闭面板停止轮询，不代表取消提供方任务。

0.1.4-beta.1 将高度、位置和旋转设置移至生成页，任务/候选页提供摘要与跳转；API Key 是当前生成前提，OAuth 明确标为可选。瞬时阶段/耗时与会话内任务反馈分开，后台查询不覆盖导入错误；查询恢复只清除对应查询错误。共享 RPC 串行门闩仍保留。暂停按钮准确说明只暂停自动查询和等待中的自动放置，不取消远端任务或已开始请求。

0.1.2 新增默认勾选的“生成完成后放入当前文档”：在生成点击时冻结目标文档会话、单位、放置参数；即使切换历史选择，也轮询有自动放置意图的任务。结果 Ready 后在 UI 线程重新检查原文档仍活动、可写且无正在执行的命令，然后导入并缩放到 Block。隐藏面板暂停，关闭面板取消自动意图；目标状态改变则保留任务并提示手动放入。

“放入当前文档”按钮和 `Lux3DPlaceTask <操作 GUID>` 使用同一服务：读取本地任务，必要时查询并下载原 task ID，绝不创建新生成。命令返回后在 Rhino Idle 启动异步工作，再回 UI 线程提交；不复用命令自己的 Undo record。同一任务在该文档使用稳定 import ID，重复调用只定位已有对象；已有 Undo/删除回执则不重新插入。保存重开后可按对象标记找回仍存在的实例。

Ready 任务或手动选取的本地 GLB 可加入最多三个候选。它们共用点、完整 CPlane 基、参考高度和旋转角。拾取点要求先激活绑定的目标文档；点的默认值为世界原点，高度采用文档单位。候选通过 DisplayConduit 显示，切换不会增加对象或 Undo 记录，也不会调用生成接口。预览只用于形体和基础颜色比较，不代表完整 Rendered/PBR 视觉。

0.1.4-beta.1 对当前候选保留独立网格/材质缓存，只在首次载入或更换资产时验证快照并建立缓存；高度/旋转/位置变化仅更新绘制矩阵与包围盒，连续输入合并为 150ms 刷新。绘制结束恢复变换栈，Clear/Dispose 释放缓存；正式 Commit 的快照哈希验证不变。可以移除单个候选，原资产与任务保留。

“接受当前候选”创建一个独立 Block 并清空比较。新接受动作分配新 import ID；同一 ID 的内部重放核对文档对象，返回 Imported/Undone/Missing，不重新插入。不同参数不得复用同一 ID。

## 坐标、材料与回滚

GLB 在独立 headless RhinoDoc 中由 Rhino 原生 importer 解码。Core 与 Host 均不设顶点、三角面、网格数、GLB 体积、节点深度或贴图像素总量的业务门槛；格式解析按实际字节范围、索引和引用关系检查，实例展开使用显式栈和循环检测。准备阶段保存固定哈希快照；接受前再验证内容与目标文档会话/单位，实际 Rhino 导入失败和资源不足分别报告。生成端面数参数约束属于 Lux3D API，不作为 Rhino 的导入上限。当前实现仍受 .NET 字节数组及 Rhino 原生数据类型、机器内存约束；移除业务门槛不等于无限容量。参见 [Rhino 8 网格改进说明](https://docs.mcneel.com/rhino/8mac/help/en-us/commandlist/newinrhino8.htm)。

直接放置服务在后台完成纯文件读取、Core 校验、哈希和私有快照；进入 Rhino 原生预检前、预检后及正式提交前重新检查文档会话、单位、活动状态与放置意图。所有 Rhino 对象访问和变更仍在 UI 线程。候选手动载入沿用同步入口；未将 Rhino 原生导入放入工作线程。`host-diagnostics` 为各事件记录单调累计时间和距上一事件的毫秒数；该计时用于定位阶段开销，不能当作已完成的性能对比。

方向和单位转换交给原生 importer 一次完成。放置层使用已导入的坐标与单位，取包围盒底部中心作锚点，使用 `P + [U V N] × Rz(angle) × H/h × (p-anchor)`。自测会断言原生 importer 是否符合夹具预期；在实际 Rhino 未通过前，不能宣称方向与尺度已验收。

正式导入前将已校验的 GLB 保存在本机 LocalApplicationData 下 `Lux3D-Rhino/host-imports/<sha256>/asset.glb`；该持久源不会随候选预览关闭而删除。native importer 若使用自己的临时纹理目录，仍须通过下面的跨进程验收确认保存机制。

接受动作在 UI 线程同步执行，在自建 Undo record 内建立 Block marker、native import、复制几何及属性到 Block、移除本次顶层导入对象并插入一个实例。失败只撤销本次 record；先比较文档对象、定义、材料、RDK 资源、嵌入文件、图层与单位快照，以免无变更时撤销用户的上一操作。失败后清除本次失败的 Redo。若不能确认回滚，明确报告需要核对文档，不自动重试同一 import ID。

材质通过 Rhino native GLB importer 与原属性保留。当前不将“native import 成功”作为完整 PBR 的证据：base color、metallic、roughness、normal 的通道/采样/Rendered 视觉以及持久纹理迁移仍必须逐项验收；透明度、双面、emissive、AO、压缩扩展等不据此宣称支持。自测记录材质、纹理哈希和 RDK 子树，并检查保存重开一致性，但相同 Rhino 进程可能仍访问原缓存，因此不能替代重启后迁移检查。

## 用户在 Rhino 中运行离线自测

以下为后续验收步骤，本轮按用户要求未执行。加载候选插件后，输入以下命令（请将 `/path/to/lux3D-rhino` 替换为实际仓库绝对路径；第二个参数是允许创建证据的目录）：

```text
Lux3DSelfTest "/path/to/lux3D-rhino/fixtures/asymmetric-pbr.glb" "/tmp/lux3d-rhino-evidence"
```

再对无材质夹具运行一次：

```text
Lux3DSelfTest "/path/to/lux3D-rhino/fixtures/asymmetric-geometry.glb" "/tmp/lux3d-rhino-evidence"
```

Windows 使用对应的绝对路径，例如（请替换为实际仓库位置）：

```text
Lux3DSelfTest "C:\work\lux3D-rhino\fixtures\asymmetric-pbr.glb" "C:\work\lux3D-rhino\artifacts\rhino-selftest"
```

每次新建 `lux3d-<timestamp>-<random>/`，不覆盖已有文件，不修改用户当前文档。里面有 `report.json`、米/毫米测试 `.3dm` 及复制重开样本。命令只接受固定 SHA-256 的仓库夹具；不使用凭证、不联网、不创建付费任务。

自动检查覆盖：

1. 原生节点变换、Y-up→Z-up、米/毫米转换与 8 三角形保持。
2. 世界平面、同法向不同 X 轴、倾斜平面，各自 0°/37°；位置阈值固定为 `max(documentTolerance, 1e-6 × H)`。
3. 20 次 conduit 参数更新复用缓存，源网格 CRC、对象和 Undo 序列不变；校验变换/包围盒、首次快照检查、Clear/Dispose 生命周期。
4. 接受后单 Block、相同请求重放不重复插入。
5. 单次 Undo、Undo 后重放、Redo、手工删除后的回执核对。
6. 包含 BitmapTable/RenderMeshes 的 `.3dm` 保存、复制到子目录、headless 重开，检查几何、材质/纹理证据一致。

`automaticChecksPassed` 是上述自动检查结果；`fullP0Acceptance` 固定为 `false`。某项失败会留下名称、错误和其它可继续执行的结果。错误 JSON 并不等于验收通过；自测报告应交给开发者核对。

仍需人工：在 Rendered 模式分别确认四个必需 PBR 通道；关闭 Rhino 后在无原临时纹理缓存条件下重开复制样本；文档 A/B 切换、同名文档、关闭目标文档；与 Tripo 同时加载；真实 OAuth 授权及权限；真实文字/照片任务与生成记录；演示录屏。未执行项保持未验证。
