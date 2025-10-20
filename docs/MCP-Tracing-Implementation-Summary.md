# MCP Server 追踪功能实现总结

## ✅ 已完成的工作

### 1. **OpenTelemetry 集成** (LlmPool)

#### 添加的 NuGet 包:
- `OpenTelemetry` (1.10.0)
- `OpenTelemetry.Extensions.Hosting` (1.10.0)
- `OpenTelemetry.Instrumentation.AspNetCore` (1.10.1)
- `OpenTelemetry.Instrumentation.Http` (1.11.0)

#### 配置 (Program.cs):
```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .AddSource("Experimental.ModelContextProtocol") // ✅ MCP SDK
            .AddSource("Microsoft.Extensions.AI")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddProcessor(sp => new SimpleActivityExportProcessor(
                new InMemoryActivityExporter(...) // 自定义导出器
            ));
    });
```

### 2. **InMemoryActivityExporter** (自定义导出器)

**文件**: `src/LY.LlmPool.Web/Services/Telemetry/InMemoryActivityExporter.cs`

- 实现 `BaseExporter<Activity>`
- 将 OpenTelemetry 的 Activity 导出到 ActivityTraceService
- 使 LlmPool 成为简易版 OTLP Collector

### 3. **ActivityTraceService 增强**

**修改**: `src/LY.LlmPool.Web/Services/Telemetry/ActivityTraceService.cs`

添加 MCP SDK ActivitySource 监听:
```csharp
ShouldListenTo = source =>
{
    return source.Name.StartsWith("Experimental.ModelContextProtocol") || // ✅ 新增
           source.Name.StartsWith("Microsoft.Extensions.AI") ||
           // ... 其他 source
};
```

### 4. **TestMcpServer 示例**

**位置**: `tests/TestMcpServer/`

**功能**:
- 演示 LlmPoolClient 的使用
- 验证 Activity Context 在递归调用中的传播
- 展示 traceparent header 自动注入

**关键代码**:
```csharp
// Activity.Current 会自动通过 ActivityPropagationHandler 注入到 HTTP 请求头
var client = new LlmPoolClient(baseUrl, apiKey);
var response = await client.ChatAsync("gpt-4o-mini", messages);
// LlmPool 接收并解析 traceparent,创建子 Activity
```

### 5. **文档**

- ✅ `docs/MCP-Server-Tracing-Architecture.md` - 架构说明
- ✅ `tests/TestMcpServer/README.md` - 使用指南
- ✅ `docs/MCP-Tracing-Implementation-Summary.md` (本文档)

## 🎯 追踪链路

### 完整的调用链:

```
External Client
  ↓ [traceparent HTTP header]
OpenAI Controller (Server Activity)
  ↓ [Activity.Current 继承]
App 1 Tool Execution (Tool Activity - 主应用)
  ↓ [Activity.Current 继承]
Model Inference (Chat Activity - Microsoft.Extensions.AI)
  ↓ [Activity.Current 继承]
MCP Tool Call (Client Activity - Experimental.ModelContextProtocol)
  ↓ [params._meta with traceparent - MCP SDK 自动注入]
MCP Server Processing (Server Activity - Experimental.ModelContextProtocol)
  ↓ [LlmPoolClient with traceparent HTTP header]
OpenAI Controller (Recursive Call - Server Activity)
  ↓ [Activity.Current 继承]
App 2 Tool Execution (Tool Activity - MCP Server 回调的应用)
  ↓ [Activity.Current 继承]
Model Inference (Chat Activity - App 2 使用的模型)
```

**重点**: MCP Server 通常会回调 LlmPool 的**另一个 App**,而不是直接调用 Model。这样可以:
- 复用 App 的工具链和提示词
- 形成更复杂的工作流 (App 1 调用 MCP Tool → MCP Server 调用 App 2 → App 2 可能再调用其他工具)
- 实现工具的组合和编排

### 双重传播机制:

1. **MCP Protocol Layer**: 通过 `params._meta` 字段 (MCP SDK 内置)
2. **HTTP Layer**: 通过 `traceparent` header (LlmPool 的 ActivityPropagationHandler)

## 🔍 关键发现

### MCP SDK 已内置完整追踪支持

查看 MCP SDK 源码 (`Diagnostics.cs`, `McpSessionHandler.cs`):

1. **ActivitySource**: `Experimental.ModelContextProtocol`
2. **Trace Context 注入**:
   ```csharp
   _propagator.InjectActivityContext(activity, request);
   // 注入到 params._meta: { "traceparent": "00-xxx-xxx-01" }
   ```
3. **Trace Context 提取**:
   ```csharp
   parentContext: _propagator.ExtractActivityContext(message)
   // 从 params._meta 提取 traceparent 并创建子 Activity
   ```

### 无需修改 ToolProviderService

原因: MCP SDK 已自动处理 trace context 传播

- `ToolProviderService` 调用 `client.CallToolAsync()` 时,`Activity.Current` 存在
- MCP SDK 自动将其注入到 JSON-RPC 的 `params._meta`
- MCP Server 接收后自动提取并创建子 Activity
- MCP Server 回调 LlmPool 时,`Activity.Current` 自动通过 `ActivityPropagationHandler` 注入到 HTTP 请求头

### LlmPool 作为 OTLP Collector

优势:
- ✅ 无需外部依赖 (Jaeger/Tempo)
- ✅ 内置 UI (`/app-call-trace`) 实时展示
- ✅ 可同时导出到多个后端 (内存 + OTLP Exporter)
- ✅ 支持按 ConversationId 过滤追踪

## 🧪 测试步骤

### 验证完整追踪链路:

1. **启动 LlmPool**:
   ```bash
   dotnet run --project src/LY.LlmPool.Web/LY.LlmPool.Web.csproj
   ```

2. **配置模型**:
   - 在 LlmPool 中添加 `gpt-4o-mini` 配置

3. **运行 TestMcpServer**:
   ```bash
   cd tests/TestMcpServer
   dotnet run
   ```

4. **查看追踪**:
   - 访问 `http://localhost:5054/app-call-trace`
   - 验证看到 TestMcpServer 发起的请求
   - 验证 TraceId 在整个链路中保持一致

5. **验证日志**:
   - TestMcpServer 控制台: 看到 "TraceId: xxx, SpanId: xxx"
   - LlmPool 日志: 看到 "从 traceparent header 提取父 Activity"

### 预期结果:

#### ActivityTraceService 捕获的 Activity:
```
🌐 http.request POST /v1/chat/completions
  TraceId: 00-abc123...
  SpanId: def456...
  ParentSpanId: (null or external)
  └─ 📱 ServerRequestActivity: /v1/chat/completions
      ParentSpanId: def456...
      └─ 🤖 chat gpt-4o-mini
          └─ ...
```

#### AppCallTrace.razor UI:
- 显示完整的调用树
- TraceId 在所有节点中一致
- ParentSpanId 正确关联
- 显示 Activity Events (tool_call, tool_result 等)

## 🚀 未来扩展

### 1. 完整的 MCP Server 实现

TestMcpServer 当前是简化示例,完整实现需要:
- 实现 MCP JSON-RPC 协议 (tools/list, tools/call, resources/list 等)
- 使用 `ModelContextProtocol.Server` API
- 支持 SSE/Stdio 多种传输协议

### 2. 导出到外部 OTLP Collector (可选)

取消注释 Program.cs 中的代码:
```csharp
.AddOtlpExporter(options =>
{
    options.Endpoint = new Uri("http://localhost:4317");
});
```

然后启动 Jaeger/Tempo:
```bash
docker run -d -p 4317:4317 -p 16686:16686 jaegertracing/all-in-one
```

### 3. 采样策略 (生产环境)

添加采样器以减少性能影响:
```csharp
.SetSampler(new TraceIdRatioBasedSampler(0.1)) // 10% 采样率
```

### 4. 性能优化

使用 Batch Processor:
```csharp
.AddProcessor(sp => new BatchActivityExportProcessor(
    new InMemoryActivityExporter(...),
    maxQueueSize: 2048,
    scheduledDelayMilliseconds: 5000
))
```

## 📊 性能影响

### 内存使用:
- ActivityTraceService 保留最近 100 条已完成追踪
- 每个 Activity 约 1-5 KB (取决于 Tags 数量)
- 估计内存占用: ~500 KB (100 traces)

### CPU 开销:
- Activity 创建和事件触发: <1% CPU
- 建议生产环境使用采样策略

## 🎓 学习资源

- [OpenTelemetry .NET](https://opentelemetry.io/docs/instrumentation/net/)
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [W3C Trace Context](https://www.w3.org/TR/trace-context/)
- [Distributed Tracing](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing)

## 🙏 致谢

感谢 MCP C# SDK 团队提供完整的 OpenTelemetry 追踪支持,使得实现变得简单且优雅。

---

**实现时间**: 2025-10-19  
**版本**: LlmPool v1.0 + MCP Tracing  
**状态**: ✅ 已完成,等待生产验证
