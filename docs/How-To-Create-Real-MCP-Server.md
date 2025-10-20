# 如何创建真正的 MCP Server

## 🎯 目标

创建一个完整的 MCP Server,包含:
1. ✅ 实现 MCP 协议 (JSON-RPC)
2. ✅ 注册 Tools
3. ✅ 处理 tools/call 请求
4. ✅ 在 Tool 中回调 LlmPool
5. ✅ 支持分布式追踪

## 📦 当前 TestMcpServer 的局限

当前的 `TestMcpServer` 只是一个**简化示例**:

```csharp
// ❌ 当前实现: 只是普通的控制台程序
var client = new LlmPoolClient(baseUrl, apiKey);
var response = await client.ChatAsync("helper-app", messages);
```

**缺少的功能**:
- ❌ 没有 MCP 协议实现 (JSON-RPC over SSE/stdio)
- ❌ 没有 Tool 注册机制
- ❌ 无法被 LlmPool 的 App 调用

## 🚀 创建真正的 MCP Server

### 步骤 1: 安装 MCP SDK

```xml
<PackageReference Include="ModelContextProtocol" Version="0.4.0-preview.1" />
```

### 步骤 2: 实现 MCP Server

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using LY.LlmPool.Client;
using System.Diagnostics;

namespace RealMcpServer;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // 配置 OpenTelemetry (与 TestMcpServer 相同)
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("RealMcpServer"))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource("Experimental.ModelContextProtocol")
                    .AddSource("Microsoft.Extensions.AI");
            });

        // 注册服务
        builder.Services.AddSingleton<LlmPoolClient>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var baseUrl = config["LlmPool:BaseUrl"] ?? "http://localhost:5054/v1";
            var apiKey = config["LlmPool:ApiKey"] ?? "test-key";
            return new LlmPoolClient(baseUrl, apiKey);
        });

        builder.Services.AddHostedService<McpServerHost>();

        var host = builder.Build();
        await host.RunAsync();
    }
}

public class McpServerHost : BackgroundService
{
    private readonly ILogger<McpServerHost> _logger;
    private readonly LlmPoolClient _llmPoolClient;
    private IMcpServer? _mcpServer;

    public McpServerHost(
        ILogger<McpServerHost> logger,
        LlmPoolClient llmPoolClient)
    {
        _logger = logger;
        _llmPoolClient = llmPoolClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("🚀 启动 MCP Server...");

            // 创建 SSE Server (监听 HTTP 端口)
            _mcpServer = await McpServer.CreateSseServerAsync(
                port: 3100,
                logger: _logger,
                cancellationToken: stoppingToken);

            _logger.LogInformation("✅ MCP Server 启动成功: http://localhost:3100");

            // 注册 Tool: 获取天气信息
            _mcpServer.RegisterTool(
                name: "get_weather",
                description: "Get weather information for a city",
                parameterSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        city = new
                        {
                            type = "string",
                            description = "The city name"
                        }
                    },
                    required = new[] { "city" }
                },
                handler: async (string city) => await GetWeatherAsync(city));

            // 注册 Tool: 调用 LlmPool 的另一个 App
            _mcpServer.RegisterTool(
                name: "ask_helper",
                description: "Ask the helper app a question",
                parameterSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        question = new
                        {
                            type = "string",
                            description = "The question to ask"
                        }
                    },
                    required = new[] { "question" }
                },
                handler: async (string question) => await AskHelperAsync(question));

            _logger.LogInformation("📝 已注册工具:");
            _logger.LogInformation("  - get_weather: 获取天气信息");
            _logger.LogInformation("  - ask_helper: 调用 LlmPool 的 helper-app");
            _logger.LogInformation("");
            _logger.LogInformation("💡 使用方式:");
            _logger.LogInformation("  1. 在 LlmPool 的 /mcp-config 中添加此服务器");
            _logger.LogInformation("     URL: http://localhost:3100/sse");
            _logger.LogInformation("     Type: SSE");
            _logger.LogInformation("  2. 创建 App 并在 Tools 中选择 'get_weather' 或 'ask_helper'");
            _logger.LogInformation("  3. 测试调用,查看 /app-call-trace 中的追踪数据");

            // 保持运行
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "❌ MCP Server 运行失败");
            throw;
        }
    }

    /// <summary>
    /// Tool 实现: 获取天气信息 (模拟)
    /// </summary>
    private Task<string> GetWeatherAsync(string city)
    {
        var currentActivity = Activity.Current;
        _logger.LogInformation("🌤️ get_weather | City: {City} | TraceId: {TraceId} | SpanId: {SpanId}",
            city, currentActivity?.TraceId, currentActivity?.SpanId);

        // 模拟天气数据
        var temperature = Random.Shared.Next(15, 30);
        var weather = new[] { "Sunny", "Cloudy", "Rainy" }[Random.Shared.Next(3)];
        
        var result = $"Weather in {city}: {weather}, {temperature}°C";
        _logger.LogInformation("✅ Result: {Result}", result);
        
        return Task.FromResult(result);
    }

    /// <summary>
    /// Tool 实现: 调用 LlmPool 的 helper-app
    /// 这会形成完整的调用链: App 1 → MCP Tool → MCP Server → App 2
    /// </summary>
    private async Task<string> AskHelperAsync(string question)
    {
        var currentActivity = Activity.Current;
        _logger.LogInformation("💬 ask_helper | Question: {Question} | TraceId: {TraceId} | SpanId: {SpanId}",
            question, currentActivity?.TraceId, currentActivity?.SpanId);

        try
        {
            // 🔑 关键: Activity.Current 会自动通过 ActivityPropagationHandler 注入到 HTTP header
            //    形成完整的 TraceId 链路
            _logger.LogInformation("📞 Calling LlmPool App 'helper-app'...");

            var messages = new[]
            {
                new ClientMessage
                {
                    Role = "user",
                    Content = question
                }
            };

            // 调用 LlmPool 的另一个 App
            var response = await _llmPoolClient.ChatAsync("helper-app", messages);

            _logger.LogInformation("✅ Helper App Response: {Response}", response);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to call helper app");
            return $"Error: {ex.Message}";
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🛑 Stopping MCP Server...");
        
        if (_mcpServer != null)
        {
            await _mcpServer.DisposeAsync();
        }
        
        await base.StopAsync(cancellationToken);
    }
}
```

### 步骤 3: 配置文件

**appsettings.json**:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning"
    }
  },
  "LlmPool": {
    "BaseUrl": "http://localhost:5054/v1",
    "ApiKey": "test-key"
  }
}
```

**RealMcpServer.csproj**:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="0.4.0-preview.1" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.0" />
    <PackageReference Include="OpenTelemetry" Version="1.10.0" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.10.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\LY.LlmPool.Client\LY.LlmPool.Client.csproj" />
  </ItemGroup>
</Project>
```

## 🧪 测试完整的调用链

### 1. 启动服务

```bash
# 终端 1: 启动 LlmPool
cd src/LY.LlmPool.Web
dotnet run

# 终端 2: 启动 MCP Server
cd RealMcpServer
dotnet run
```

### 2. 配置 LlmPool

访问 `http://localhost:5054/mcp-config`:

```json
{
  "name": "Real MCP Server",
  "url": "http://localhost:3100/sse",
  "type": "SSE",
  "enabled": true
}
```

### 3. 创建 App

在 LlmPool 中创建一个 App:
- Name: `weather-app`
- Model: `gpt-4o-mini`
- System Prompt: "You are a helpful assistant. Use tools when needed."
- Tools: 勾选 `get_weather` 和 `ask_helper`

### 4. 测试调用

**场景 1: 简单工具调用**
```bash
curl -X POST http://localhost:5054/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer test-key" \
  -d '{
    "model": "weather-app",
    "messages": [
      {"role": "user", "content": "What is the weather in Beijing?"}
    ]
  }'
```

**调用链**:
```
External Request
  └─ OpenAI Controller (LlmPool)
      └─ App: weather-app
          └─ Model: gpt-4o-mini
              └─ MCP Tool Call: get_weather
                  └─ MCP Server (RealMcpServer)
                      └─ GetWeatherAsync()
```

**场景 2: 递归调用 (App → MCP Server → App)**
```bash
curl -X POST http://localhost:5054/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer test-key" \
  -d '{
    "model": "weather-app",
    "messages": [
      {"role": "user", "content": "Ask the helper app: What is AI?"}
    ]
  }'
```

**调用链**:
```
External Request
  └─ OpenAI Controller (LlmPool)
      └─ App 1: weather-app
          └─ Model: gpt-4o-mini
              └─ MCP Tool Call: ask_helper
                  └─ MCP Server (RealMcpServer)
                      └─ AskHelperAsync()
                          └─ LlmPoolClient
                              └─ OpenAI Controller (LlmPool, Recursive)
                                  └─ App 2: helper-app
                                      └─ Model: gpt-4o-mini
```

### 5. 查看追踪数据

访问 `http://localhost:5054/app-call-trace`:

验证:
- ✅ TraceId 在整个链路中保持一致
- ✅ ParentSpanId 正确关联
- ✅ 递归调用嵌套显示
- ✅ MCP Tool Call Activity 显示
- ✅ 时间线正确 (子 Activity 在父 Activity 时间范围内)

## 📊 追踪数据示例

### LlmPool 能看到的 Activity

```
TraceId: abc123

├─ [controller-span-001] OpenAI Controller (External → LlmPool)
│   └─ [app-span-001] App: weather-app
│       └─ [model-span-001] Model: gpt-4o-mini
│           └─ [mcp-client-span-001] MCP Tool Call: ask_helper
│               (进程边界 - MCP Server 的 Activity 不可见)
│               └─ [controller-span-002] OpenAI Controller (MCP Server → LlmPool)
│                   └─ [app-span-002] App: helper-app
│                       └─ [model-span-002] Model: gpt-4o-mini
```

### MCP Server 能看到的 Activity (本地日志)

```
TraceId: abc123

├─ [mcp-server-span-001] MCP Server Processing
│   ├─ ParentSpanId: mcp-client-span-001 (来自 LlmPool)
│   └─ Tool: ask_helper
│       └─ [llmpool-call-span-001] LlmPoolClient.ChatAsync
│           └─ ParentSpanId: mcp-server-span-001
```

## 🎯 如需查看 MCP Server 内部 Activity

### 方案 1: 导出到 Jaeger (推荐)

```csharp
// RealMcpServer 和 LlmPool 都添加
.AddOtlpExporter(options =>
{
    options.Endpoint = new Uri("http://localhost:4317");
});
```

```bash
# 启动 Jaeger
docker run -d -p 4317:4317 -p 16686:16686 jaegertracing/all-in-one

# 访问 Jaeger UI
http://localhost:16686
```

在 Jaeger UI 中搜索 TraceId,可以看到完整的跨进程调用链。

### 方案 2: LlmPool 实现 OTLP 接收

参考 `docs/Cross-Process-Tracing-Architecture.md` 中的方案 A。

## 📝 总结

### TestMcpServer vs RealMcpServer

| 特性 | TestMcpServer | RealMcpServer |
|-----|--------------|---------------|
| **MCP 协议** | ❌ 无 | ✅ SSE/stdio |
| **Tool 注册** | ❌ 无 | ✅ RegisterTool |
| **可被 LlmPool 调用** | ❌ 否 | ✅ 是 |
| **traceparent 传播** | ✅ 是 | ✅ 是 |
| **Activity 自动创建** | ⚠️ 仅 Client 端 | ✅ Client + Server |
| **用途** | 演示 LlmPoolClient | 生产级 MCP Server |

### 关键要点

1. **Activity 传播**: 
   - ✅ LlmPoolClient 自动注入 traceparent header
   - ✅ MCP SDK 自动注入 `_meta` 字段
   - ✅ TraceId 在整个链路中保持一致

2. **跨进程可见性**:
   - ❌ LlmPool 无法直接看到 MCP Server 内部 Activity
   - ✅ 但 traceparent 正确传播,形成完整链路
   - 🎯 使用 Jaeger 查看完整跨进程追踪

3. **推荐架构**:
   ```
   LlmPool + RealMcpServer
       ↓
   都导出到 Jaeger/Tempo
       ↓
   在 Jaeger UI 查看完整链路
   ```

---

**下一步**: 按照本文档创建 RealMcpServer,测试完整的 MCP 工具调用链!
