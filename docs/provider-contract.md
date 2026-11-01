# Manycore / Aholo provider contract

核实日期：2026-10-31（America/Los_Angeles）。本文件区分公开契约、离线测试和需要用户完成的真实登录/生成验收。

## 已核实的 OAuth

官方公共 JSON 发现端点已通过真实、无认证 HTTPS GET 核实；普通网页搜索工具无法读取这些端点不代表服务不存在。

| 区域 | Protected resource | Issuer |
| --- | --- | --- |
| 国际 | `https://api.aholo.ai/.well-known/oauth-protected-resource` | `https://www.aholo.ai` |
| 中国 | `https://api.aholo3d.cn/.well-known/oauth-protected-resource` | `https://www.aholo3d.cn` |

国际区原始公开响应（无用户数据）：

```json
{"resource":"https://api.aholo.ai","authorization_servers":["https://www.aholo.ai"]}
```

两区的 `{issuer}/.well-known/oauth-authorization-server` 均返回真实 OAuth metadata。国际区响应：

```json
{
  "issuer":"https://www.aholo.ai",
  "authorization_endpoint":"https://www.aholo.ai/oauth/authorize",
  "token_endpoint":"https://www.aholo.ai/oauth/token",
  "registration_endpoint":"https://www.aholo.ai/oauth/register",
  "scopes_supported":["get_user_info"],
  "response_types_supported":["code"],
  "grant_types_supported":["authorization_code","refresh_token"],
  "code_challenge_methods_supported":["S256"],
  "token_endpoint_auth_methods_supported":["none"]
}
```

中国区使用相同路径，域名为 `www.aholo3d.cn`。`openid-configuration` 没有有效 OIDC metadata；不能据此猜 userinfo 端点。

`ManycoreOAuthService` 实现上述发现、动态公开客户端注册、浏览器授权码、S256 PKCE、随机 state、IPv4 loopback 回调、五分钟取消/超时、refresh token。只请求公布的 `get_user_info`。端点必须属于已验证的区域发行方；没有 client secret。客户端注册使用本次已绑定的 OS 随机端口。回调验证完整地址、state、参数唯一性和一次性 code；token 只返回给 sidecar。`OAuthToken.ToString()` 隐去敏感值，稳定连接标识使用 region + clientId，刷新不改变它。

**发现 metadata 不等于完成用户登录，更不等于已验证付费生成能力。** 真实 DCR、用户授权、token exchange、账户访问和 Rhino 中登录结果需分别留证。API key 与 OAuth token 是不同凭证；REST adapter 只接受明确 API key，绝不把 OAuth token 冒充 raw API key。

OAuth 对应的远程 MCP：

- 国际：`https://api.aholo.ai/lux3d-mcp/mcp`
- 中国：`https://api.aholo3d.cn/lux3d-mcp/mcp`

真实国际 MCP 未认证 GET 返回 HTTP 401 和 `WWW-Authenticate: Bearer resource_metadata="https://www.aholo.ai/.well-known/oauth-protected-resource"`。接口还返回 `{"c":"-1","m":"appKey missing","d":null,"f":null}`。因此存在 OAuth Bearer challenge，但 token 可用工具与权限必须以授权后的 MCP initialize / tools/list / 无副作用账号调用核实。远程 MCP 不使用 REST 的 `/global` 前缀。

## REST 和上传

[官方鉴权](https://labs.aholo.ai/api-docs/en/authentication)、[官方接口索引](https://labs.aholo.ai/api-docs/en/llms.txt)、[官方上游契约](https://github.com/manycore-research/Aholo-Lux3D/blob/ac93a14bc84b501d167f907d8d4fcc22c041a88a/plugins/common/aholo-lux3d/core/contracts/lux3d-generation.v1.json) 和 [上传实现](https://github.com/manycore-research/Aholo-Lux3D/blob/ac93a14bc84b501d167f907d8d4fcc22c041a88a/plugins/common/aholo-lux3d/core/runtime/asset_uploader.py) 作为实现依据。公开仓库 revision：`ac93a14bc84b501d167f907d8d4fcc22c041a88a`。

| 操作 | 路径（相对于区域 REST base） |
| --- | --- |
| 只读连接验证 | `GET /lux3d/v1/generate/task/list?page=1&pagesize=1` |
| 文字生成 | `POST /lux3d/v1/generate/text-to-3d/task/create` |
| 单图生成 | `POST /lux3d/v1/generate/img-to-3d/task/create` |
| 查询任务 | `GET /lux3d/v1/generate/task/get?taskid={Int64}` |
| 上传令牌 | `GET /asset/v1/token` |

中国 REST base 为 `https://api.aholo3d.cn/`；国际为 `https://api.aholo.ai/global/`。网关 `Authorization` 为原始区域 API key，无 Bearer 前缀。两区 key 不互通。只读连接验证不创建生成任务。

明确发送 `version=G1-Turbo`、`faceCount=50000`、`outputFormat=["glb"]`、`enablePbr=true`、`aiPredictSize=false`。G1 模型契约禁止 `enablePbr` 字段，因此 G1 请求省略该字段；G1 输出通常为 ZIP 再 GLB。面数范围 10000–300000。图片首先走同区域 Asset/OUS，不能把任意外部 URL 冒充上传收据。

成功 envelope 为 `c="0"`、`d` 数据。任务编号通过 Int64/十进制字符串处理，绝不经过 double。查询验证 bizId=`LUX_3D` 且 taskId 等于请求值。状态：0 排队；1/2 运行；3 成功；4/5 失败；6 取消；其他保留未知。成功时仅选唯一 HTTPS `.glb` 输出；`NOT_REQUESTED`/`null` 不作为 URL。未知或无法识别的输出会保留生成成功事实并报告输出错误，不重复创建。

上传响应取 `ousToken`、`globalDomain`、`blockSize`。OUS 请求仅发送 `ous-token-v2`，不发送 API key；单文件以 `file`+全文件小写 MD5 multipart 上传，大文件先表单 `md5/blocks/size/name` 初始化，再按从 1 开始的 `block` 上传，状态 5 完成、6/8 失败。实现将最多 20 MiB 图片读成单一内存快照，使 MD5 与上传字节一致。分片阈值取实际响应。结果下载不由 Provider 携带任何鉴权头。所有 HTTP client 必须通过 `Lux3DApiClient.CreateHttpClient()` 创建，关闭自动重定向与 cookie。

创建请求仅一次，网络/解析失败标记 `SubmissionMayHaveSucceeded=true`；禁止盲目重试。Provider 错误消息不包含响应 body、token、输入或签名 URL。

## 凭证和验证边界

`MacKeychainCredentialStore` 直接 P/Invoke Security.framework generic-password API，service=`ai.aholo.lux3d.rhino`。API key / OAuth session 保存到 macOS 钥匙串，不通过 `security -w` 命令参数，不写普通配置文件。退出登录删除所选账户的本地凭证；没有证据支持的 revoke endpoint 不会凭空调用。

离线测试覆盖 regional routing、raw API key、精确 Int64、G1 条件字段、创建未知状态/禁止重试、task ID 校验、未知状态、结果选择、分片和凭证隔离、PKCE/state/replay/重复参数/拒绝授权、issuer pinning、token JSON roundtrip 和稳定连接标识。

尚需真实验证：DCR 与授权回调、用户 token exchange/refresh、Keychain 用户访问提示、MCP token权限及账户查询、真实区域单图/文字创建与下载、服务器分片行为。开发验证未调用付费生成。

## OAuth MCP 只读探测

`OAuthMcpProbe(HttpClient).InspectAsync(OAuthToken, CancellationToken)` 返回 `OAuthMcpInspection(Available, ToolNames, ProtocolVersion, Message)`。只发送 `initialize`、`notifications/initialized` 和带页数上限的 `tools/list`；不执行任何工具、账户资料查询、生成、报告或反馈。Bearer token 仅发往区域固定 `/lux3d-mcp/mcp`。

实现依据 [MCP Streamable HTTP 规范](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports) 与 [生命周期规范](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle)：协商协议版本后发送 `MCP-Protocol-Version`，保留服务返回的 `Mcp-Session-Id`，同时接受 JSON/SSE。单响应最多 2 MiB，最多 20 页/1000 个工具，45 秒超时；仅返回工具名称，服务器说明/指令不作为本地指令执行。`Available=true` 只证明可访问工具清单，不能证明生成权限、额度或已验收付费操作。

OAuth 回调浏览器页面只确认“已收到授权回调”，最终登录成功以 token exchange、Keychain 保存和宿主结果为准。refresh 返回的 token type、scope、有效期、refresh token 长度/字符类型被验证；回调拒绝畸形 percent 编码、控制字符、路径变化和重复参数。

当已验证的任务 `status=3` 但输出为空、只有 ZIP 或无法解析时，Provider 返回 `State="succeeded"`、`GlbUri=null` 和脱敏 `OutputError`；不再把输出解析异常当作查询失败。上层据此记录 `DownloadFailed`，保留远端成功事实与原 task ID，不重复生成。
