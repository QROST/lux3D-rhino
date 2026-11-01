# 实施结构与来源

参考 Tripo-Rhino 本地干净 `main` commit `838fbd82d6d4d6105845a5883342a99e9430e8cd`，许可证 Apache-2.0。未修改或覆盖 Tripo 源码与安装。受控复制为 Core GLB validator 及其测试、Sidecar 公网下载连接策略；Rhino native Import/headless/block 组织参考源工程，Lux3D 单独实现身份、放置与预览。文件保留来源通知，包附 LICENSE/NOTICE。

| 模块 | Runtime | 责任 |
| --- | --- | --- |
| Lux3D.Rhino | net7.0，Rhino 8 宿主 | Eto 面板、文档会话、GLB 原生导入、Conduit、Block、Undo |
| Lux3D.Core | net7.0 | 版本化协议、私有子进程客户端、任务 journal、资产/图片校验、工作流 |
| Lux3D.Provider | net8.0 | 区域 REST、OUS 上传、OAuth discovery/DCR/PKCE、macOS Keychain / Windows DPAPI |
| Lux3D.Sidecar | net8.0 | 用户私有 stdin/stdout 管道、连接生命周期、后台执行、下载隔离 |

Rhino 插件 GUID `B8B147EC-5732-4FB9-BA40-C5BB8D36C2AB`；面板 GUID `AC141E4E-8B0E-4A82-A34A-F2B0783201FB`；命令 `Lux3DPanel` / `Lux3DSelfTest`；Yak `lux3d-rhino`。没有复用 Tripo GUID、发现文件、可变存储、凭证服务或进程名称。

## 本地通信与秘密

本版简化为宿主创建的私有子进程 stdin/stdout 管道；双方协议 Version=1 和唯一请求 ID。没有开放本地控制 TCP 端口、全局命名管道或可被其他进程发现的控制令牌。唯一短时 loopback TCP 是 OAuth 回调，先绑定随机 IPv4 loopback 端口再注册并发起授权；用 state + PKCE 保护。所有 IPC 有限长，历史每页最多 30 条。

OAuth/API 密钥留在 sidecar 内存和平台凭证存储：macOS 使用独立 Keychain 服务 `ai.aholo.lux3d.rhino`，Windows 使用当前用户 DPAPI 加密文件及受限目录。唯一例外是用户主动在 Rhino 密码框输入 API Key，随私有管道传入 sidecar 后清空 UI。令牌不传回 Rhino；任务只包含连接指纹。API Key 与 OAuth Bearer 不混用。CDN 下载使用独立无鉴权 HttpClient，禁止代理，DNS 解析后直连公网 IP，逐跳复验 HTTPS，限制 64 MiB。

双方管道显式使用无 BOM UTF-8，避免 Windows 控制台代码页改变中文消息。后台由完整路径启动；framework-dependent 包通过 `SidecarRuntime` 在环境配置、系统安装目录和 PATH 中定位包含 .NET 8 的 host。候选路径不包含当前工作目录的隐式搜索。Rhino 侧为跨平台 net7.0 程序集，要求 Rhino 8 的 NETCore 模式；不支持 Windows NETFramework 模式。

## 故障语义

提交前使用原子文件写入、flush-to-disk 和单写者文件锁。`Submitting` 在恢复时显示 `SubmissionUnknown`。任何新提交前重新检查完整磁盘历史，未知提交必须由用户明确确认可能重复费用；不是仅依赖旧面板内存。创建 POST 不自动重试。查询以区域、连接指纹、原 task ID 绑定，签名 URL 不落盘。远程成功与下载 Ready 分开。

该 journal 防止客户端盲目重发，不承诺服务端 exactly-once。已知失败和未知结果可能保守呈现为未知，用户可以核对官方历史。任务读取共享删除权限，允许 Windows 上另一进程原子替换 journal，而读者仍读取同一打开句柄的快照。图片保存内容哈希快照，GLB 验证后原子缓存。候选切换不会调用 Provider。

## 坐标与宿主

方向/单位转换由 Rhino 原生 GLB 导入承担；插件不重复 Y-up 到 Z-up 转换。放置在已转换的中立几何上按底部中心、目标高度、平面完整 X/Y 基、旋转计算。源单位米与毫米/米文档的实际行为由固定非对称夹具断言，未运行宿主测试前不声称通过。

预览使用独立 headless 文档和 DisplayConduit。正式接受重新校验快照哈希、目标文档会话和单位，以单 Undo 记录创建独立 Block。后台任务不自动插入当前活动文档。

## 与 PRD 的关系

本轮用户新增要求优先于 PRD v0.1 的“首版不依赖浏览器登录”：现在实现官方 OAuth 登录。原 PRD 保留为验收基线。P0 真实生成、PBR 视觉/迁移、重启重开、完整共存验收未因代码实现而自动通过。Windows 在 0.1.1 补齐凭证、启动和安装代码，尚待 Windows 实机验收；Grasshopper、MCP 生成适配器仍未交付，当前 OAuth MCP 探测只读取工具清单。
