using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using LY.LlmPool.Client.Extensions;
using TestMcpServer.Tools;
using ModelContextProtocol.Server;

namespace TestMcpServer;

/// <summary>
/// 测试 MCP Server - 完整实现
/// 
///  实现的功能:
/// 1. 注册 MCP Tools (llmpool_call_model, llmpool_call_app, get_current_time)
/// 2. 启动 Stdio MCP Server (通过命令行调用)
/// 3. 配置 OpenTelemetry 追踪并导出到 LlmPool
/// 4. Tools 内部使用 LlmPoolClient 回调 LlmPool
/// 
///  调用链:
/// External  LlmPool App 1  Model  MCP Tool (llmpool_call_app)  
/// TestMcpServer  LlmPoolClient  LlmPool App 2  Model
/// 
///  使用方式:
/// 1. 启动 LlmPool: dotnet run --project src/LY.LlmPool.Web/LY.LlmPool.Web.csproj
/// 2. 在 LlmPool 的 /mcp-config 中添加此服务器:
///    Command: dotnet
///    Args: run --project tests/TestMcpServer/TestMcpServer.csproj
///    Type: Stdio
/// 3. 创建 App 并选择 Tools: llmpool_call_app, get_current_time
/// 4. 测试调用,查看 /app-call-trace 中的完整追踪链路
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // ✅ 一行代码添加 LlmPoolClient 和 OpenTelemetry 追踪
        builder.Services.AddLlmPoolClient(
            baseUrl: "http://localhost:5071/v1",
            apiKey: "test-key",
            serviceName: "TestMcpServer",
            serviceVersion: "1.0.0"
        );

        // 注册 MCP Server 和 Tools
        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<LlmPoolTool>();

        // 配置日志 - 输出到控制台 (标准错误流,避免与 Stdio 冲突)
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            // 输出到 stderr,避免与 MCP Stdio 协议 (使用 stdout) 冲突
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        var host = builder.Build();

        Console.WriteLine(" TestMcpServer 启动完成!");
        Console.WriteLine(" OpenTelemetry 追踪已启用 - 导出到 LlmPool /v1/traces/json");
        Console.WriteLine(" MCP Server 已注册 (Stdio Transport)");
        Console.WriteLine(" 已注册 Tools: llmpool_call_model, llmpool_call_app, get_current_time");
        Console.WriteLine();
        Console.WriteLine(" 使用方式:");
        Console.WriteLine("   1. 在 LlmPool 的 /mcp-config 中添加此服务器");
        Console.WriteLine("      Command: dotnet");
        Console.WriteLine("      Args: run --project tests/TestMcpServer/TestMcpServer.csproj");
        Console.WriteLine("      Type: Stdio");
        Console.WriteLine("   2. 创建 App 并选择 Tools (llmpool_call_app 推荐)");
        Console.WriteLine("   3. 测试调用,查看 /app-call-trace 中的完整追踪链路");
        Console.WriteLine();
        Console.WriteLine(" Waiting for MCP client connection...");

        await host.RunAsync();
    }
}
