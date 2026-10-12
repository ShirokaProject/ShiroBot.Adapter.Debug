# ShiroBot Debug Adapter

调试适配器，不连接真实平台。模拟输入进入宿主插件处理流程，消息、撤回、群管理等输出记录到网页和 WebAPI。默认模拟平台 `qq-official`，可改为其他平台标识。

## 启动与安装

需要 .NET 10 / ASP.NET Core 10；将本仓库与 [ShiroBot](https://github.com/ShirokaProject/ShiroBot) 放在同一父目录，在本仓库根目录执行：

```sh
dotnet build ShiroBot.Adapter.Debug.csproj -c Release
```

宿主源码位于其他目录时，给 build/run/test 增加 `-p:ShiroBotSourceRoot=/绝对路径/ShiroBot`。当前仓库引用宿主源码中的 SDK 和 Model，不需要把宿主一起提交。

将 `bin/Release/net10.0/ShiroBot.Adapter.Debug.dll` 安装到宿主的独立 adapter 目录，或下载 CI 生成的安装包。需要宿主提供共享的 SDK、QQ、Discord、Telegram Model 程序集。网页已经嵌入 DLL，无需安装 Node、Python 或构建前端。

首次加载后，在后台配置中设置监听地址、模拟平台、机器人 ID、记录上限和脚本选项。修改配置后重启 adapter。网页默认 `http://127.0.0.1:5088`，多个实例需要不同端口。

也可以独立预览，不加载真实插件：

```sh
dotnet run --project tools/DebugAdapter.Runner -- --echo
```

`--echo` 添加演示处理器，回复 `pong: 原文`。真实插件测试需要将 adapter 安装到宿主。Runner 支持 `DEBUG_LISTEN_URL`、`DEBUG_API_TOKEN`、`DEBUG_PLATFORM`、`DEBUG_ENABLE_SCRIPTS=1` 和 `DEBUG_SCRIPT_DIRECTORY` 环境变量。

## 配置

```toml
listen_url = "http://127.0.0.1:5088"
platform = "qq-official"
self_id = "debug-bot"
api_token = ""
max_records = 1000
max_record_bytes = 65536
enable_scripts = false
script_directory = "debug-scripts"
script_runner = "/bin/sh"
script_timeout_seconds = 10
```

默认执行器在 macOS/Linux 是 `/bin/sh`，Windows 是系统 PowerShell `powershell.exe`；不依赖 Python。需要 Python 或 Node 脚本时自行设置 `script_runner`，对应解释器需要已安装。PowerShell 使用 `-NoProfile -NonInteractive -File` 启动，不修改系统执行策略。

非本机监听要求设置 Token，API 请求使用 `Authorization: Bearer TOKEN`。网页 Token 只保留在当前页面内存中。跨来源网页请求被拒绝。服务用于受控的开发环境。

## 网页

- 选择事件，修改自动生成的 JSON，点击发送；支持复制 curl。
- 字段目录可搜索所有发现的 Model、字段和枚举值；选择平台 Model 后可插入 `raw`。
- 实时记录显示入站事件、插件输出和触发结果，支持方向过滤。
- 规则支持本地脚本、Webhook、自动注入事件；支持导入和导出 JSON。
- 场景支持多事件注入和输出断言，支持导入和导出 JSON。

反射目录在 adapter 启动时扫描 SDK、内置三个平台的 Model 及其他已加载的 `ShiroBot.Model.*` 程序集。新增或更新程序集后重启刷新目录。QQ 的 Kind 常量也在目录中；平台负载与通用事件/Kind 的对应关系由调用方选择，不根据类型名猜测。

## WebAPI

当前实例提供 `GET /llm`：返回可直接供开发者或 LLM 读取的 Markdown，包含全部 HTTP 调用、参数、curl 示例、事件收发、脚本/Webhook 规则、场景断言和内存诊断方式。末尾实时生成实例信息（不含 Token）、已注册路由及完整反射目录。访问示例：`http://127.0.0.1:5089/llm`，实际端口以监听配置为准。网页「使用方式」中也有文档链接。

| 方法 / 路径 | 用途 |
| --- | --- |
| `GET /llm` | HTTP / LLM 完整调用文档与当前实例类型目录 |
| `GET /api/diagnostics` | 进程内存、GC、Skia 缓存与记录数量，不触发 GC |
| `GET /api/status` | 模拟平台、脚本执行器等运行信息 |
| `GET /api/catalog` | 事件、Model、消息段、字段、示例与 QQ Kind |
| `POST /api/events` | 注入事件 |
| `GET /api/records?after=0` | 查询记录，按递增 ID 过滤 |
| `GET /api/stream` | SSE 实时记录（支持 Authorization 请求头） |
| `GET /api/rules` | 查询规则 |
| `POST /api/rules` | 添加或更新规则，相同 ID 替换 |
| `DELETE /api/rules/{id}` | 删除规则 |
| `POST /api/scenarios/run` | 注入事件并运行断言 |

规则和记录保存在内存，重启清空；规则和场景可由网页导出保存，再导入复用。SSE 断开后重新连接并使用 records 查询补齐；不自动重放 Last-Event-ID。记录到达上限后删除旧记录。

### 消息事件

```sh
curl http://127.0.0.1:5088/api/events \
  -H 'Content-Type: application/json' \
  --data '{"type":"ShiroBot.SDK.Models.MessageEvent","data":{"messageId":"test-1","channel":{"id":"group-1","type":"Group"},"sender":{"id":"user-1","name":"测试用户"},"segments":[{"$type":"ShiroBot.SDK.Models.TextSegment","text":"#ping"}]}}'
```

服务强制设置 `platform` 与 adapter 配置一致，默认补上 `selfId`、消息时间；`instanceId` 由宿主填入。`$type` 使用目录给出的完整类型名，支持抽象消息段及按钮动作等多态字段。

### 平台 Model 负载

```json
{
  "type": "ShiroBot.SDK.Models.PlatformEvent",
  "data": {
    "kind": "group_nudge",
    "channel": {"id": "group-1", "type": "Group"},
    "raw": {
      "type": "ShiroBot.Model.QQ.QGroupNudge",
      "data": {"groupId": "group-1", "senderId": "user-1", "receiverId": "debug-bot"}
    }
  }
}
```

Typed raw 会反序列化为真实 Model 对象，插件可以直接转型；普通原始 JSON 则保留为 `JsonElement`。未知事件类型、缺少必填字段、未知多态类型返回 HTTP 400。

### 触发规则

将 `examples/debug-scripts/check_reply.sh` 复制到配置的脚本目录（Windows 使用 `.ps1`），启用脚本后提交：

```json
{
  "id": "reply-check",
  "direction": "outbound",
  "type": "sendMessage",
  "action": "script",
  "script": "check_reply.sh",
  "enabled": true,
  "maxExecutions": 100
}
```

脚本标准输入是完整记录 JSON：`id/time/direction/type/data/correlationId/depth`。标准输出、错误及退出码记录为 `scriptResult`；超时或执行异常记录为 `actionError`。脚本自身可调用 API，但不会自动解释脚本输出为事件。

`direction` 为 `inbound` 或 `outbound`；入站 `type` 使用完整事件类型名，出站使用操作名（如 `sendMessage`），`*` 匹配全部类型。

- Webhook：`action: "webhook"`，填写 `url`；收到 HTTP POST，正文为完整记录 JSON。
- 自动事件：`action: "event"`，填写 `event: {"type": "…", "data": {…}}`。
- 最多 100 条规则，工作队列最多 128 项、串行执行，满队列跳过并记录日志。
- 关联链最多递归 4 层。每条规则默认累计触发 100 次后在下一次匹配时自动停用；`maxExecutions` 可设 1–10000，更新规则可重新启用并清零计数。这也限制宿主异步分发丢失上下文时产生的循环。
- 只执行脚本目录内的相对路径文件，拒绝目录穿越和符号链接。脚本超时会终止进程树；stdout/stderr 各最多记录 8192 字符。

### 场景断言

参考 `examples/ping-scenario.json`：

```sh
curl http://127.0.0.1:5088/api/scenarios/run \
  -H 'Content-Type: application/json' \
  --data-binary @examples/ping-scenario.json
```

`expect` 检查指定方向、操作/事件类型、JSON 内容子串及至少出现次数。返回 `passed/checks/records`；超时是断言失败，HTTP 请求仍成功。每次最多 100 个事件，等待最多 30 秒。

同步处理及携带 ReplyTo/Quote 的消息输出尽量关联到原入站事件。宿主后台队列或插件主动输出可能没有关联 ID；场景会同时检查本次时间窗口内的无关联输出。因此场景应在专用实例串行运行，避免其他流量导致误判；不承诺并发测试隔离。

## 模拟能力与边界

实现 SDK 通用消息发送、按钮/交互回复记录、撤回、历史消息读取、用户/群成员查询、群管理操作记录。查询数据来源于有界消息快照，不是真实平台数据。模拟成功不等于平台接口一定支持或授权。

平台特有扩展接口（例如 `IQOfficialMessageApi`、平台上传/审批接口）当前未实现，插件能力探测会返回 null。平台 Model 的事件与消息段可以注入/记录，但不模拟全部平台 API。

最多 32 个 SSE 客户端，每个队列 128 项；慢客户端会断开。请求上限 1 MiB，记录及消息快照数量有界，大记录只保留摘要。停止服务会取消触发任务、关闭订阅并释放网络资源。

## 验证

```sh
dotnet test tests/DebugAdapter.Tests/DebugAdapter.Tests.csproj
```

测试覆盖 HTTP 注入/回复/场景、全部自动事件示例、typed Raw、Token/Origin、SSE、记录大小/慢订阅者、Webhook、脚本及超时、递归和数量上限。

内存诊断：页面中的「读取内存快照」及 `GET /api/diagnostics` 返回整个进程的 Working Set、托管内存、最近 GC 堆大小、碎片、累计分配、各代 GC 次数和调试记录数量。此接口不触发 GC。生成场景时默认匹配任意 sendMessage 回复；可修改 contains 添加内容断言。

内存快照同时提供 `gcCommittedBytes`、`fragmentedBytes` 与已加载 Skia 的字体/图片缓存字节数；未加载 Skia 时缓存字段为 null。此接口不执行 GC 或清空缓存。
