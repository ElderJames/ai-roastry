# MCP Server 追踪测试场景

## 🎯 测试目标

验证完整的 MCP Server 调用追踪链路:

```
External Request
  → OpenAI Controller (LlmPool)
    → App 1: weather-assistant (主应用)
      → Model: gpt-4
        → MCP Tool: GetWeather (Client Activity)
          → MCP Server Processing (Server Activity)
            → LlmPoolClient Callback (traceparent 传播)
              → OpenAI Controller (Recursive)
                → App 2: city-info-app (MCP Server 回调)
                  → Model: gpt-4o-mini
```

## 📋 前置条件

### 1. LlmPool 配置

需要在 LlmPool 中创建以下配置:

#### 模型配置 (Model Config)
- **gpt-4**: 主应用使用
- **gpt-4o-mini**: MCP Server 回调的 App 使用

#### MCP Server 配置
目前 TestMcpServer 是简化示例,实际场景需要:
1. 完整的 MCP Server 实现 (使用 ModelContextProtocol.Server API)
2. 在 LlmPool 的 `/mcp-config` 页面添加服务器配置
3. 注册工具并在 App 中引用

### 2. 应用配置 (Apps)

#### App 1: weather-assistant (主应用)
```json
{
  "name": "weather-assistant",
  "model": "gpt-4",
  "systemPrompt": "You are a helpful weather assistant. When asked about weather, use the GetWeather tool.",
  "tools": [
    {
      "type": "mcp",
      "serverId": "weather-mcp-server",
      "toolName": "GetWeather"
    }
  ]
}
```

#### App 2: city-info-app (MCP Server 回调)
```json
{
  "name": "city-info-app",
  "model": "gpt-4o-mini",
  "systemPrompt": "You provide detailed information about cities.",
  "temperature": 0.7
}
```

## 🚀 测试步骤

### 步骤 1: 启动 LlmPool

```bash
cd e:\lianyuan\llm-pool
dotnet run --project src/LY.LlmPool.Web/LY.LlmPool.Web.csproj
```

等待启动完成: `http://localhost:5054`

### 步骤 2: 配置 LlmPool

1. 访问 `http://localhost:5054/model-config`
   - 添加 `gpt-4` 和 `gpt-4o-mini` 的配置

2. 访问 `http://localhost:5054/app-config`
   - 创建 `helper-app` (用于 TestMcpServer 示例)
   - 配置使用 `gpt-4o-mini` 模型

### 步骤 3: 运行 TestMcpServer

```bash
cd e:\lianyuan\llm-pool\tests\TestMcpServer
dotnet run
```

TestMcpServer 会:
1. 等待 5 秒
2. 运行两个示例:
   - Example 1: 直接调用 Model (`gpt-4o-mini`)
   - Example 2: 调用 App (`helper-app`)

### 步骤 4: 查看追踪数据

访问: `http://localhost:5054/app-call-trace`

## ✅ 验证要点

### 1. TraceId 一致性

所有 Activity 应该共享同一个 TraceId:
```
TraceId: 00-abc123def456... (所有节点相同)
```

### 2. SpanId 关联

每个 Activity 的 ParentSpanId 应该指向其父 Activity 的 SpanId:
```
Activity 1: SpanId = aaa, ParentSpanId = (null)
Activity 2: SpanId = bbb, ParentSpanId = aaa
Activity 3: SpanId = ccc, ParentSpanId = bbb
```

### 3. Activity 层级结构

在 AppCallTrace UI 中应该看到树形结构:
```
🌐 http.request POST /v1/chat/completions (TestMcpServer)
  └─ 📱 ServerRequestActivity: /v1/chat/completions
      └─ 🤖 chat gpt-4o-mini
```

### 4. 日志验证

#### TestMcpServer 控制台:
```
🔧 Example 1: Call Model | TraceId: xxxxx | SpanId: xxxxx
📞 Calling LlmPool model 'gpt-4o-mini' directly...
   ActivityPropagationHandler 会自动注入 traceparent header
✅ Model Response: 4
```

#### LlmPool 日志:
```
从 traceparent header 提取父 Activity: TraceId=xxxxx, SpanId=xxxxx
🌐 LlmPool Server Activity 已启动: TraceId=xxxxx, SpanId=yyyyy, ParentSpanId=xxxxx
```

## 🔍 完整场景测试 (需要真实 MCP Server)

要测试完整的 App → MCP Tool → MCP Server → App 链路,需要:

### 1. 实现完整的 MCP Server

参考 MCP SDK 文档创建 MCP Server:
```csharp
// 使用 ModelContextProtocol.Server API
var server = await McpServer.CreateSseServerAsync(
    port: 3100,
    serverInfo: new ServerInfo { Name = "WeatherServer", Version = "1.0" }
);

// 注册工具
server.RegisterTool(async (string city) =>
{
    // 🎯 在这里使用 LlmPoolClient 回调 App
    var client = new LlmPoolClient("http://localhost:5054/v1", "api-key");
    var response = await client.ChatAsync("city-info-app", new[] {
        new ClientMessage { Role = "user", Content = $"Tell me about {city}" }
    });
    return $"Weather in {city}: {response}";
}, "GetWeather", "Get weather information for a city");
```

### 2. 在 LlmPool 中配置 MCP Server

访问 `/mcp-config`:
```json
{
  "id": "weather-server",
  "name": "Weather MCP Server",
  "transport": "sse",
  "url": "http://localhost:3100/sse"
}
```

### 3. 创建使用 MCP Tool 的 App

在 `/app-config` 中创建:
```json
{
  "name": "weather-assistant",
  "model": "gpt-4",
  "tools": [
    {
      "type": "mcp",
      "serverId": "weather-server",
      "toolName": "GetWeather"
    }
  ]
}
```

### 4. 发起测试请求

```bash
curl -X POST http://localhost:5054/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer test-key" \
  -d '{
    "model": "weather-assistant",
    "messages": [
      {"role": "user", "content": "What is the weather in Beijing?"}
    ]
  }'
```

### 5. 验证完整调用链

访问 `/app-call-trace`,应该看到:
```
🌐 External Request
  └─ 📱 weather-assistant
      └─ 🤖 gpt-4
          └─ 🔧 GetWeather (MCP Client)
              └─ 🌐 MCP Server: weather-server
                  └─ 📞 LlmPool Callback
                      └─ 📱 city-info-app
                          └─ 🤖 gpt-4o-mini
```

## 📊 性能指标

### 预期延迟

- 简单调用 (无 MCP): ~500ms
- 带 MCP 递归调用: ~1500ms (包括 2 次模型推理)
- Activity 创建开销: <5ms

### 内存使用

- 每个 Activity: ~2-5 KB
- 100 个已完成追踪: ~500 KB
- ActivityTraceService 总开销: <10 MB

## 🐛 故障排查

### 问题 1: 看不到 MCP Activity

**检查**:
- ActivityTraceService 是否监听 `Experimental.ModelContextProtocol`
- OpenTelemetry 配置是否包含 `.AddSource("Experimental.ModelContextProtocol")`
- MCP SDK 版本 >= 0.4.0-preview.1

**验证**:
```
LlmPool 日志中应该有:
ActivitySource 检测: Experimental.ModelContextProtocol - 监听: True
```

### 问题 2: TraceId 不一致

**原因**: traceparent 未正确传播

**检查**:
1. LlmPoolClient 是否注册了 ActivityPropagationHandler
2. OpenAI Controller 是否正确解析 traceparent header
3. MCP SDK 是否正确注入/提取 trace context

**验证**:
```bash
# 查看 HTTP 请求头
# 应该包含: traceparent: 00-{traceId}-{spanId}-01
```

### 问题 3: 递归调用未嵌套

**原因**: ParentSpanId 未正确设置

**检查**:
- OpenAI Controller 是否使用 `parentContext` 参数创建 Activity
- Activity.Current 是否在调用链中正确传递

## 📚 参考资料

- [MCP Server Tracing Architecture](../docs/MCP-Server-Tracing-Architecture.md)
- [MCP Tracing Implementation Summary](../docs/MCP-Tracing-Implementation-Summary.md)
- [OpenTelemetry .NET](https://opentelemetry.io/docs/instrumentation/net/)
- [W3C Trace Context](https://www.w3.org/TR/trace-context/)

---

**测试日期**: 2025-10-19  
**状态**: ✅ 基础追踪已实现,完整 MCP 场景等待真实 MCP Server 测试
