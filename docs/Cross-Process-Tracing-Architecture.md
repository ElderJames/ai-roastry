# 跨进程追踪架构说明

## 🔍 问题分析

### ActivityListener 的限制

`ActivityListener` 只能监听**当前进程**中创建的 Activity,无法跨进程监听。

```
进程 A: TestMcpServer          进程 B: LlmPool
├─ ActivitySource              ├─ ActivitySource
├─ Activity (MCP Server)       ├─ Activity (OpenAI Controller)
└─ ActivityListener ❌         └─ ActivityListener ✅
   (无法监听进程 B)               (只能监听进程 B)
```

### 当前实现的局限

LlmPool 的 `ActivityTraceService` 配置:
```csharp
ShouldListenTo = source =>
{
    return source.Name.StartsWith("Experimental.ModelContextProtocol") || // ❌ 只能监听本进程
           source.Name.StartsWith("Microsoft.Extensions.AI") ||
           // ...
};
```

**问题**: 这只能捕获 LlmPool **自己进程内**的 MCP Client Activity,无法捕获 MCP Server 进程的 Activity。

## 🎯 完整的追踪链路

### 实际的 Activity 分布

```
进程: TestMcpServer (MCP Server)
  └─ Activity: MCP Server Processing (Server Activity)
      ├─ TraceId: abc123
      ├─ SpanId: server-span-001
      └─ ParentSpanId: client-span-001 (来自 LlmPool)

进程: LlmPool
  ├─ Activity: OpenAI Controller (Server Activity)
  │   ├─ TraceId: abc123
  │   ├─ SpanId: controller-span-001
  │   └─ ParentSpanId: external-span (来自外部请求)
  │
  ├─ Activity: App Tool Execution
  │   ├─ TraceId: abc123
  │   ├─ SpanId: app-span-001
  │   └─ ParentSpanId: controller-span-001
  │
  ├─ Activity: Model Inference
  │   ├─ TraceId: abc123
  │   ├─ SpanId: model-span-001
  │   └─ ParentSpanId: app-span-001
  │
  └─ Activity: MCP Tool Call (Client Activity) ✅ LlmPool 能看到
      ├─ TraceId: abc123
      ├─ SpanId: client-span-001
      ├─ ParentSpanId: model-span-001
      └─ 通过 _meta 传播 traceparent 到 MCP Server

(进程边界 - HTTP 或 SSE)

进程: TestMcpServer
  └─ Activity: MCP Server Processing ❌ LlmPool 看不到
      └─ 通过 LlmPoolClient 回调 LlmPool
          └─ 通过 HTTP header 传播 traceparent

(进程边界 - HTTP)

进程: LlmPool (递归调用)
  └─ Activity: OpenAI Controller (Recursive) ✅ LlmPool 能看到
      └─ App 2 Tool Execution
          └─ ...
```

### LlmPool 能看到的 Activity

LlmPool 的 ActivityTraceService 只能捕获:
- ✅ OpenAI Controller Activities (自己的)
- ✅ App Tool Execution Activities (自己的)
- ✅ Model Inference Activities (自己的)
- ✅ MCP Tool Call Activities (Client 端,自己的)
- ❌ MCP Server Processing Activities (MCP Server 进程的)

## 💡 解决方案

### 方案 A: OTLP Exporter (标准分布式追踪) ⭐ 推荐

#### 架构

```
TestMcpServer                    LlmPool
  ├─ ActivitySource              ├─ ActivitySource
  ├─ Activity                    ├─ Activity
  └─ OtlpExporter ───HTTP───>   └─ OtlpReceiver (待实现)
                                    └─ ActivityTraceService
```

#### 实现步骤

**1. TestMcpServer 添加 OTLP Exporter**

```csharp
// 需要安装包
// <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.10.0" />

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .AddSource("Experimental.ModelContextProtocol")
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri("http://localhost:5054/v1/traces");
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
            });
    });
```

**2. LlmPool 实现 OTLP 接收端点**

需要在 LlmPool 中添加 OTLP 接收器:
- 创建 `/v1/traces` 端点接收 OTLP 数据
- 解析 Protobuf 格式的 Activity 数据
- 转换并存储到 ActivityTraceService

**复杂度**: 需要实现 OTLP 协议解析和数据转换。

### 方案 B: 自定义 Exporter (简化版)

TestMcpServer 通过 HTTP API 上报 Activity 数据:

```csharp
// TestMcpServer
public class LlmPoolActivityExporter : BaseExporter<Activity>
{
    private readonly HttpClient _httpClient;

    public override ExportResult Export(in Batch<Activity> batch)
    {
        var activities = batch.Select(a => new
        {
            TraceId = a.TraceId.ToString(),
            SpanId = a.SpanId.ToString(),
            ParentSpanId = a.ParentSpanId.ToString(),
            OperationName = a.OperationName,
            StartTime = a.StartTimeUtc,
            Duration = a.Duration,
            Tags = a.Tags.ToDictionary(t => t.Key, t => t.Value)
        });

        // POST to LlmPool
        _httpClient.PostAsJsonAsync("http://localhost:5054/api/traces", activities);
        
        return ExportResult.Success;
    }
}
```

然后 LlmPool 添加接收端点:
```csharp
[HttpPost("/api/traces")]
public IActionResult ReceiveTraces([FromBody] List<ActivityDto> activities)
{
    foreach (var activity in activities)
    {
        // 转换并添加到 ActivityTraceService
        _activityTraceService.AddExternalActivity(activity);
    }
    return Ok();
}
```

### 方案 C: 仅依赖 traceparent 传播 (当前实现) ✅ 最简单

**原理**: 不上报 MCP Server 的 Activity,只依赖 traceparent 传播。

**优点**:
- ✅ 实现简单,无需额外代码
- ✅ traceparent 自动传播 (MCP SDK + ActivityPropagationHandler)
- ✅ LlmPool 能看到完整的 TraceId 链路

**缺点**:
- ❌ 看不到 MCP Server 内部的处理细节
- ❌ 无法知道 MCP Server 的响应时间
- ❌ AppCallTrace UI 中会缺少 MCP Server 节点

**适用场景**: 
- 开发阶段,快速验证 traceparent 传播
- MCP Server 是第三方服务,无法修改

### 方案 D: 混合模式 (推荐生产环境)

TestMcpServer 和 LlmPool 都导出到统一的 OTLP Collector (Jaeger/Tempo):

```
TestMcpServer ──OTLP──┐
                       ├──> Jaeger/Tempo
LlmPool ──────OTLP────┘
```

**优点**:
- ✅ 标准化的分布式追踪架构
- ✅ 可以使用 Jaeger UI 查看完整链路
- ✅ LlmPool 的 AppCallTrace UI 可以同时查询 Jaeger 数据

**实现**:
```bash
# 启动 Jaeger
docker run -d -p 4317:4317 -p 16686:16686 jaegertracing/all-in-one

# TestMcpServer 和 LlmPool 都配置导出到 Jaeger
.AddOtlpExporter(options =>
{
    options.Endpoint = new Uri("http://localhost:4317");
});
```

## 🎯 推荐方案

### 开发阶段: 方案 C (当前实现)
- 快速验证 traceparent 传播
- 无需额外实现
- LlmPool 能看到自己的 Activity 链

### 生产阶段: 方案 D (Jaeger/Tempo)
- 完整的分布式追踪
- 标准化架构
- 可视化完整链路

### 特殊需求: 方案 A 或 B
如果必须在 LlmPool UI 中显示 MCP Server 的 Activity,
则需要实现 OTLP 接收或自定义上报协议。

## 📝 当前 TestMcpServer 的定位

当前的 TestMcpServer 是一个**简化示例**,用于:
1. ✅ 演示 LlmPoolClient 的使用
2. ✅ 验证 traceparent 传播 (跨进程)
3. ✅ 展示 Activity.Current 如何自动注入到 HTTP header

**不包括**:
- ❌ 完整的 MCP Server 实现 (JSON-RPC 协议)
- ❌ Tool 注册和调用
- ❌ Activity 数据上报到 LlmPool

## 🚀 下一步

### 如果需要完整的 MCP Server 追踪

1. **实现真正的 MCP Server**:
   ```csharp
   var server = await McpServer.CreateSseServerAsync(port: 3100, ...);
   server.RegisterTool(async (string city) => {
       // Tool 实现,回调 LlmPoolClient
   });
   ```

2. **选择追踪方案**:
   - 方案 C: 仅 traceparent 传播 (最简单)
   - 方案 D: 导出到 Jaeger (推荐)

3. **在 LlmPool 中配置 MCP Server**:
   - 访问 `/mcp-config` 添加服务器
   - 创建 App 并引用 MCP Tool

4. **测试完整链路**:
   ```
   External → OpenAI Controller → App 1 → Model → 
   MCP Tool Call → MCP Server → LlmPoolClient → 
   OpenAI Controller → App 2 → Model
   ```

---

**结论**: 
- 当前实现 (方案 C) 是正确的,无需主动上报
- LlmPool 能看到自己进程内的完整 Activity 链
- 如需看到 MCP Server 内部细节,考虑导出到 Jaeger
