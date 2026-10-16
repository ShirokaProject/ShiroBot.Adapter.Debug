# ShiroBot DebugAdapter HTTP 调试手册

这是当前运行实例的调用文档。基础地址：{{BASE_URL}}。网页入口 `/`；本文 `/llm`，返回 Markdown，无需浏览器或 Python。需要真实插件响应时必须连接加载了该插件的实例；独立 Runner 的 --echo 只演示 pong，不代表 MyParser 已加载。

## 推荐调试流程

1. GET /api/status 确认平台与实例，再 GET /api/catalog 获取实际类型与示例。
2. GET /api/records 记下最后一条 id（空列表视为 0）。需要实时输出时先连接 /api/stream。
3. POST /api/events 注入事件；GET /api/records?after=最后id 轮询插件输出。
4. 后台插件可能在注入返回后继续处理，HTTP 200 不代表解析或下载完成；用输出记录或场景断言判断。
5. 需要自动化时 POST /api/scenarios/run，并检查 JSON 的 passed，不能只检查 HTTP 200。
6. 内存调试读取 /api/diagnostics，记录空闲基线、工作峰值和结束后多次采样；它不触发 GC。

## 通用请求、认证与错误

API JSON 使用 camelCase；枚举使用目录给出的字符串。POST 设置 Content-Type: application/json。配置了 api_token 时，所有 /api 路由需要 Authorization: Bearer TOKEN；本文不显示 Token。`/` 和 `/llm` 不要求 Token。不要在 URL 中放 Token。跨来源 Origin 被拒绝；curl 无需 Origin。

```sh
BASE='{{BASE_URL}}'
curl -sS "$BASE/api/status"
# 若需要认证，给下面所有 /api 请求加：-H 'Authorization: Bearer YOUR_TOKEN'
```

200：调用成功；400：JSON、类型、字段或规则参数错误，通常返回 {"error":"..."}；401：Token 缺失或不匹配；403：Origin 不匹配；404：路径或待删除规则不存在；413：请求超过 1 MiB。插件内部异常也可能返回 500。

## 所有 HTTP 调用

| 方法与路径 | 输入 | 输出与语义 |
|---|---|---|
| GET / | 无 | HTML 调试工作台 |
| GET /llm | 无 | 本 Markdown、当前实例信息、实际路由、完整类型目录 |
| GET /api/methods | 无 | SDK 与 Model 接口方法、重载签名、参数、示例、调用模式 |
| POST /api/call | methodId/arguments/可选 targetId | 执行模拟服务，返回 methodId/simulated/result |
| GET /api/mock-results | 无 | 自定义平台模拟返回值列表 |
| POST /api/mock-results | methodId/result | 配置平台 Mock 返回值，空 200 |
| DELETE /api/mock-results?methodId=... | URL 编码的方法 ID | 删除返回值；200 或 404 |
| GET /api/status | 无 | platform/selfId/boundUrl/enableScripts/scriptRunner/scriptExample |
| GET /api/catalog | 无 | events/payloads/segments/kinds/operations/rawFormat；类型字段与示例 |
| GET /api/diagnostics | 无 | 整个进程内存与 GC、Skia 缓存、记录数量；不强制 GC |
| POST /api/events | EventInput，见下文 | 注入后的 inbound DebugRecord，宿主处理后记录 outbound |
| GET /api/records?after=0 | 可选 after：long，默认 0 | id > after 的现存记录数组，按 id 升序 |
| DELETE /api/records | 无 | 清空服务端记录，返回 cleared；不重置递增 ID，不触发 GC |
| GET /api/stream | 无 | text/event-stream，只推送订阅后的记录 |
| GET /api/rules | 无 | TriggerRule 数组 |
| POST /api/rules | 单个 TriggerRule | 空 200；相同 id 替换并重置触发计数 |
| DELETE /api/rules/{id} | 路径 id，须 URL 编码 | 已删除 200，不存在 404 |
| POST /api/scenarios/run | Scenario | passed/correlationId/checks/records |

```sh
curl -sS "$BASE/api/catalog"
curl -sS "$BASE/api/diagnostics"
curl -sS "$BASE/api/records?after=0"
curl -sS "$BASE/api/rules"
curl -sS -X DELETE "$BASE/api/rules/reply-check"
```

## 注入消息与平台事件

EventInput：type（必填，events 中完整类型名）、data（必填对象）、correlationId（可选关联字符串，默认自动生成）。平台强制使用实例配置；selfId 缺失时补上；instanceId 由宿主提供；MessageEvent 时间缺失时补当前时间。必填字段不能为空。不同消息段的 `$type` 使用 segments 中完整类型名。

```sh
curl -sS "$BASE/api/events" -H 'Content-Type: application/json' --data-binary @- <<'JSON'
{"type":"ShiroBot.SDK.Models.MessageEvent","data":{"messageId":"llm-test-1","channel":{"id":"group-1","type":"Group"},"sender":{"id":"user-1","name":"测试用户"},"segments":[{"$type":"ShiroBot.SDK.Models.TextSegment","text":"#ping"}]}}
JSON
```

测试 MyParser 时，将 text 换成待解析链接，messageId 每次使用不同值，以免命中插件去重。该请求模拟用户消息，不是直接调用平台的发送接口。

平台事件使用 PlatformEvent，kind 从目录 kinds 选择，raw 支持 `{"type":"平台Model完整名","data":{...}}`，可反序列化为真实 Model；其他原始 JSON 保留为 JsonElement。选择与事件语义对应的 Model，不按名称猜映射。

```sh
curl -sS "$BASE/api/events" -H 'Content-Type: application/json' --data-binary @- <<'JSON'
{"type":"ShiroBot.SDK.Models.PlatformEvent","data":{"kind":"group_nudge","channel":{"id":"group-1","type":"Group"},"raw":{"type":"ShiroBot.Model.QQ.QGroupNudge","data":{"groupId":"group-1","senderId":"user-1","receiverId":"debug-bot"}}}}
JSON
```

## 接收记录与 SSE

DebugRecord：id（递增整数）、time（UTC 时间）、direction（inbound/outbound/trigger）、type（入站完整事件类型名或输出操作名）、data（对象）、correlationId（可为 null）、depth（关联触发深度）。大记录超出 maxRecordBytes 时 data 改为 truncated 摘要。

```sh
curl -N "$BASE/api/stream"
# 另一终端注入事件；SSE 帧：id: 123，data: {完整记录JSON}，随后空行。
curl -sS "$BASE/api/records?after=123"
```

SSE 支持 Authorization 请求头。无历史重放、无 Last-Event-ID 自动恢复，重连需用 records 补齐；记录已被上限淘汰时无法补回。最多 32 客户端，每客户端缓冲 128 条，慢客户端断开。记录与规则仅在内存，重启清空；网页清空显示不会清除服务端记录。

## 脚本、Webhook 与事件触发

TriggerRule 必填：id、direction（inbound/outbound）、type（完整事件名、输出操作名或 *）、action（script/webhook/event）。可选 enabled=true、maxExecutions=100（1–10000）。action 对应填写 script、url 或 event。

```sh
curl -sS "$BASE/api/rules" -H 'Content-Type: application/json' --data-binary @- <<'JSON'
{"id":"reply-check","direction":"outbound","type":"sendMessage","action":"script","script":"check_reply.sh","enabled":true,"maxExecutions":100}
JSON
```

script 需先在后台启用 enable_scripts，文件位于 script_directory 内，填写相对路径；拒绝目录穿越和符号链接。默认 macOS/Linux /bin/sh，Windows powershell.exe，不要求 Python；需要其他解释器时自行配置 script_runner。标准输入为完整 DebugRecord JSON，输出不自动变成事件；退出码/stdout/stderr 记录在 trigger/scriptResult，异常记录 trigger/actionError。stdout/stderr 各截取最多 8192 字符，超时终止进程树。

```json
{"id":"notify","direction":"outbound","type":"sendMessage","action":"webhook","url":"http://127.0.0.1:9000/events","enabled":true}
```

Webhook 接收 HTTP POST，正文为完整记录 JSON，受 scriptTimeoutSeconds 限制。添加规则不会自动启动接收服务。

```json
{"id":"inject-once","direction":"inbound","type":"*","action":"event","maxExecutions":1,"event":{"type":"ShiroBot.SDK.Models.MessageEvent","data":{"messageId":"auto-1","channel":{"id":"group-1","type":"Group"},"sender":{"id":"user-1"},"segments":[{"$type":"ShiroBot.SDK.Models.TextSegment","text":"#ping"}]}}}
```

上述 JSON 作为 POST /api/rules 正文。最多 100 规则，128 项串行触发队列，满队列跳过并记日志，关联深度最多 4。累计达到 maxExecutions 后下一次匹配停用；重新 POST 同 id 可重新启用。测试后 DELETE 对应规则。

## 场景自动断言

Scenario：events（1–100 个 EventInput）、expect（可选 Expectation 数组）、timeoutMilliseconds（1–30000，默认 3000）。Expectation：type（必填）、contains（可选，记录 data 的 JSON 子串，区分大小写）、direction（默认 outbound）、count（至少出现次数，默认 1，必须 >=1）。expect 为空只检查注入，不检查插件响应。

```sh
curl -sS "$BASE/api/scenarios/run" -H 'Content-Type: application/json' --data-binary @- <<'JSON'
{"events":[{"type":"ShiroBot.SDK.Models.MessageEvent","data":{"messageId":"scenario-1","channel":{"id":"group-1","type":"Group"},"sender":{"id":"user-1"},"segments":[{"$type":"ShiroBot.SDK.Models.TextSegment","text":"#ping"}]}}],"expect":[{"type":"sendMessage","direction":"outbound","contains":"pong","count":1}],"timeoutMilliseconds":3000}
JSON
```

此例适用于 --echo Runner；真实插件需改 text 与 contains。每个 checks 项包含 expected/actual/passed。超时断言失败仍返回 HTTP 200，务必检查 passed。返回 records 包含本次窗口记录。后台输出可能无关联 ID，场景也匹配窗口内无关联输出；使用专用实例串行运行场景，避免其他流量误判。

## 内存诊断字段与能力边界

/api/diagnostics：processId、sampledAt、workingSetBytes（整个进程驻留内存）、managedBytes、heapSizeBytes、fragmentedBytes、gcCommittedBytes、totalAllocatedBytes（累计分配，不是当前占用）、serverGC、assemblyCount、threadCount、gcIndex、gcPauseTimePercentage、gen0Collections/gen1Collections/gen2Collections、generations（按索引对应 gen0/gen1/gen2/LOH/POH，包含最近 GC 前后空间与碎片）、skiaFontCacheBytes/skiaResourceCacheBytes（未加载时 null）、retainedRecords/maxRecords。单位为字节。最近一次 GC 的空间不等于当前存活对象；采样本身不强制 GC。

DebugAdapter 记录 SDK 通用发送、撤回、交互与群管理操作，查询使用有界模拟消息快照，不连接真实平台。SDK 方法通过统一 POST /api/call 调用，也可由事件处理插件调用。平台专用扩展接口通过动态代理提供 Mock；模拟通过不能证明真实平台支持。目录随启动时已加载的 Model 程序集生成，新增程序集后重启刷新。

## SDK 方法反射与统一调用

GET /api/methods 反射 SDK、当前已加载平台 Model 的公开接口方法（含继承方法）。methodId 包含接口全名、方法名、完整参数类型，区分重载；不要只传方法名。使用目录中的 example 即可 POST /api/call。arguments 是以参数名为键的对象，可选参数可省略，CancellationToken 由 HTTP 请求取消信号注入。未知方法、参数或缺少必填参数返回 400。

```sh
curl -sS "$BASE/api/methods"
# 保存目录里任意 callable=true 方法的 example 为 call.json，然后：
curl -sS "$BASE/api/call" -H 'Content-Type: application/json' --data-binary @call.json
```

mode=simulation 表示 SDK 通用服务：发送、撤回、查询、群管理、文件上传、交互确认、表情回应。查询读取有界消息快照；上传仅生成模拟文件 ID，不读取本地路径或上传远端。默认接口方法（例如 AssessMessage）也可调用。

mode=mock 表示平台扩展动态代理；插件通过 GetAdapterExtension 获得相同代理。调用记录为 outbound/sdkCall，data 含 methodId/arguments/simulated。默认 string 返回 debug-* ID，集合为空，bool 为 false（能力探测需自行配置为 true），对象按反射示例生成。这些是测试占位数据，不是平台响应。

```json
{"methodId":"从 /api/methods 复制的完整 methodId","result":"custom-message-id"}
```

上述正文 POST /api/mock-results，可配置平台方法返回值，包括 Task<T> 的 T；也支持同步返回值。接口、void、Stream 返回不能设为 JSON；通用 simulation 的数据来自消息快照，不接受平台 Mock 覆盖。结果最大 maxRecordBytes，方法键数量受当前反射目录约束，重启清空。删除使用 DELETE /api/mock-results?methodId=URL编码后的完整ID。

Stream 输入用 {"base64":"..."}，HTTP 请求仍受 1 MiB 限制；请求拥有的输入流调用后关闭，Mock 记录只保留流元数据。返回接口对象（如 BeginStream）时 result 是 {targetId,service}，调用该接口方法时在 POST /api/call 的正文中带 targetId；最多保留 64 个返回对象，超出后旧 ID 失效，重启清空。例：BeginStream → 携带 targetId 调 AppendAsync → CompleteAsync → DisposeAsync。

SDK 的宿主、渲染、配置与插件生命周期接口同样在目录中，以 hostContract 标示；它们不是 adapter 的服务对象，没有相应调用目标。泛型、委托、ref/out 方法不适合 JSON 调用，callable=false 并提供原因。事件订阅通过 /api/stream，事件上报通过 /api/events。反射调用仅允许目录中的 adapter/Model 接口，不接受任意 .NET 类型或方法。
