# LlmPool ```
External Client
  ↓ [traceparent HTTP header]
OpenAI Controller (Server Activity)
  ↓ [Activity.Current]
App 1 Tool Execution (Tool Activity - 主应用)
  ↓ [Activity.Current]
Model Inference (Chat Activity - Microsoft.Extensions.AI)
  ↓ [Activity.Current]
MCP Tool Call (Client Activity - Experimental.ModelContextProtocol)
  ↓ [params._meta with traceparent]
MCP Server Processing (Server Activity - Experimental.ModelContextProtocol)
  ↓ [LlmPoolClient with traceparent HTTP header]
OpenAI Controller (Recursive Call - Server Activity)
  ↓ [Activity.Current]
App 2 Tool Execution (Tool Activity - MCP Server 回调)
  ↓ [Activity.Current]
Model Inference (Chat Activity - App 2 的模型)
```erver 调用追踪实现

## 📊 概述

LlmPool 已实现**简易版 OTLP Collector**功能,无需依赖外部 Jaeger/Tempo,直接使用内置的 `ActivityTraceService` 和 `AppCallTrace.razor` UI 进行追踪数据的收集和展示。

## 🎯 追踪链路

完整的调用链追踪路径:

```
External Client
  ↓ [traceparent HTTP header]
OpenAI Controller (Server Activity)
  ↓ [Activity.Current]
App Tool Execution (Tool Activity - ActivityScopeManager)
  ↓ [Activity.Current]
Model Inference (Chat Activity - Microsoft.Extensions.AI)
  ↓ [Activity.Current]
MCP Tool Call (Client Activity - Experimental.ModelContextProtocol)
  ↓ [params._meta with traceparent]
MCP Server Processing (Server Activity - Experimental.ModelContextProtocol)
  ↓ [LlmPoolClient with traceparent HTTP header]
OpenAI Controller (Recursive Call - Server Activity)
```

## 🔧 实现细节

### 1. **MCP SDK 内置追踪机制**

MCP SDK (ModelContextProtocol v0.4.0-preview.1) 已内置 OpenTelemetry 支持:

- **ActivitySource**: `Experimental.ModelContextProtocol`
- **Trace Context 传播**: 
  - Client 端: 通过 `DistributedContextPropagator.Inject()` 将 Activity 注入到 JSON-RPC 的 `params._meta` 字段
  - Server 端: 通过 `DistributedContextPropagator.Extract()` 从 `params._meta` 提取 traceparent
- **自动 Activity 创建**:
  - Client 调用: `ActivityKind.Client`
  - Server 处理: `ActivityKind.Server` (parentContext 从 _meta 提取)

**关键代码** (来自 MCP SDK):
```csharp
// McpSessionHandler.cs Line 408+
using Activity? activity = Diagnostics.ShouldInstrumentMessage(request) ?
    Diagnostics.ActivitySource.StartActivity(
        CreateActivityName(method), 
        ActivityKind.Client) :
    null;

// 注入 trace context 到 MCP 协议的 _meta 字段
_propagator.InjectActivityContext(activity, request);

// Server 端接收时提取 trace context
Activity? activity = Diagnostics.ShouldInstrumentMessage(message) ?
    Diagnostics.ActivitySource.StartActivity(
        CreateActivityName(method),
        ActivityKind.Server,
        parentContext: _propagator.ExtractActivityContext(message),
        links: Diagnostics.ActivityLinkFromCurrent()) :
    null;
```

### 2. **LlmPool 的追踪基础设施**

#### 2.1 ActivityTraceService (内存存储)

- **位置**: `Services/Telemetry/ActivityTraceService.cs`
- **功能**: 
  - 通过 `ActivityListener` 监听所有 Activity 的启动和停止
  - 维护活跃和已完成的追踪树
  - 支持按 ConversationId 或 TraceId 查询
  - 实时事件通知 (ActivityStarted, ActivityStopped)

**监听的 ActivitySource**:
```csharp
ShouldListenTo = source =>
{
    return source.Name.StartsWith("Microsoft.Extensions.AI") ||
           source.Name.StartsWith("Experimental.Microsoft.Extensions.AI") ||
           source.Name.StartsWith("Experimental.ModelContextProtocol") || // ✅ MCP SDK
           source.Name.Contains("LlmPool") ||
           source.Name.StartsWith("OpenAI") ||
           source.Name.Contains("ChatClient");
};
```

#### 2.2 InMemoryActivityExporter (自定义导出器)

- **位置**: `Services/Telemetry/InMemoryActivityExporter.cs`
- **功能**: 实现 `BaseExporter<Activity>`,将 OpenTelemetry 的 Activity 导出到 ActivityTraceService
- **作用**: 桥接 OpenTelemetry 和内存存储

#### 2.3 OpenTelemetry 配置 (Program.cs)

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService("LlmPool")
        .AddAttributes(new Dictionary<string, object>
        {
            ["service.version"] = "1.0.0",
            ["deployment.environment"] = builder.Environment.EnvironmentName
        }))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource("Experimental.ModelContextProtocol") // ✅ MCP SDK
            .AddSource("Microsoft.Extensions.AI")
            .AddSource("Experimental.Microsoft.Extensions.AI")
            .AddSource("LlmPool.*")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddProcessor(sp => new SimpleActivityExportProcessor(
                new InMemoryActivityExporter(
                    sp.GetRequiredService<ActivityTraceService>(),
                    sp.GetRequiredService<ILogger<InMemoryActivityExporter>>()
                )
            ));
    });
```

#### 2.4 Activity 传播机制

**HTTP Layer (LlmPool → LlmPool 回调)**:
- `ActivityPropagationHandler` (DelegatingHandler)
- 自动将 `Activity.Current` 注入到 HTTP 请求的 `traceparent` header
- OpenAI Controller 接收并解析 `traceparent` header 创建子 Activity

**MCP Protocol Layer (LlmPool ↔ MCP Server)**:
- MCP SDK 自动处理
- 通过 JSON-RPC 的 `params._meta` 字段传播 traceparent

### 3. **UI 展示** (AppCallTrace.razor)

- **实时监控**: 每秒自动刷新活跃调用
- **调用树展示**: 
  - 活跃调用 Tab: 显示进行中的调用链
  - 已完成调用 Tab: 显示最近 100 条已完成的调用
- **会话管理**: 按 ConversationId 或 TraceId 分组显示
- **详细信息**: 
  - TraceId, SpanId, ParentSpanId
  - 工具调用参数和结果
  - Token 使用统计
  - Activity Events
  - 错误信息

## 🚀 使用方法

### 查看追踪数据

1. 启动 LlmPool
2. 访问 `/app-call-trace` 页面
3. 发起包含 MCP Tool 调用的请求
4. 实时查看完整的调用链路

### 外部追踪系统集成 (可选)

如果需要导出到 Jaeger/Tempo/Grafana,只需在 `Program.cs` 中取消注释:

```csharp
.WithTracing(tracing =>
{
    tracing
        // ... 现有配置 ...
        .AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri("http://localhost:4317"); // Jaeger gRPC
        });
});
```

## 📝 关键优势

✅ **无需外部依赖**: 不需要安装 Jaeger/Tempo  
✅ **自动追踪**: MCP SDK 自动处理 trace context 传播  
✅ **完整链路**: 支持递归调用 (MCP Server → LlmPool)  
✅ **实时展示**: 内置 UI 实时显示追踪数据  
✅ **可扩展**: 可同时导出到多个后端 (内存 + OTLP)  

## 🔍 验证追踪链路

### 测试步骤

1. 创建一个使用 MCP Tool 的 App
2. MCP Server 配置为回调 LlmPool 的其他 App/Model
3. 发起请求并查看 `/app-call-trace` 页面
4. 验证是否显示完整的调用树:
   ```
   🌐 http.request POST /v1/chat/completions
     └─ 📱 App Tool: MyApp
         └─ 🤖 gen_ai.client.chat: gpt-4
             └─ 🔧 Tool: my_mcp_tool (MCP Client Activity)
                 └─ 🌐 http.request POST /v1/chat/completions (MCP Server 回调)
                     └─ 📱 App Tool: HelperApp
   ```

## 🛠️ 故障排查

### 1. MCP Activity 未显示

**问题**: 看不到 MCP Tool 调用的 Activity

**检查**:
- MCP SDK 版本 >= 0.4.0-preview.1
- `ActivityTraceService` 中 `ShouldListenTo` 包含 `Experimental.ModelContextProtocol`
- OpenTelemetry 配置中包含 `.AddSource("Experimental.ModelContextProtocol")`

**日志验证**:
```
ActivitySource 检测: Experimental.ModelContextProtocol - 监听: True
```

### 2. 递归调用未关联

**问题**: MCP Server 回调 LlmPool 时创建了新的 TraceId

**检查**:
- LlmPoolClient 是否正确传递 `traceparent` header
- `ActivityPropagationHandler` 是否注册到 HttpClient pipeline
- OpenAI Controller 是否正确解析 `traceparent` header

### 3. 性能影响

**优化建议**:
- 使用 `BatchActivityExportProcessor` 代替 `SimpleActivityExportProcessor` (批量导出)
- 限制 `ActivityTraceService` 的内存存储大小 (已实现: 最多 100 条已完成追踪)
- 生产环境考虑采样策略 (修改 `Sample` 回调)

## 📦 依赖包

```xml
<PackageReference Include="OpenTelemetry" Version="1.10.0" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.10.0" />
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.10.1" />
<PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.11.0" />
<PackageReference Include="ModelContextProtocol" Version="0.4.0-preview.1" />
```

## 🎓 参考资料

- [OpenTelemetry .NET Documentation](https://opentelemetry.io/docs/instrumentation/net/)
- [MCP C# SDK - Diagnostics.cs](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/src/ModelContextProtocol.Core/Diagnostics.cs)
- [W3C Trace Context Specification](https://www.w3.org/TR/trace-context/)

---

**实现时间**: 2025-10-19  
**状态**: ✅ 已完成 - 等待测试验证
