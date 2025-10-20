using ModelContextProtocol.Server;
using System.ComponentModel;
using LY.LlmPool.Client;
using System.Diagnostics;

namespace TestMcpServer.Tools;

/// <summary>
/// LlmPool Tool - 调用 LlmPool 的 App 或 Model
/// 这个 Tool 会被 LlmPool 的 App 调用,然后回调 LlmPool 的另一个 App
/// 形成完整的 App 1 → MCP Tool → MCP Server → App 2 调用链
/// </summary>
[McpServerToolType]
public sealed class LlmPoolTool
{
    [McpServerTool(Name = "llmpool_call_model")]
    [Description("Call a model in LlmPool directly. Use this when you need to invoke an LLM model.")]
    public static async Task<string> CallModelAsync(
        [Description("The model name to call (e.g., 'gpt-4o-mini', 'claude-3-5-sonnet')")] string modelName,
        [Description("The prompt/question to send to the model")] string prompt,
        LlmPoolClient client,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"🔧 llmpool_call_model | Model: {modelName} | Prompt: {prompt}");

        var messages = new[]
        {
            new ClientMessage
            {
                Role = "user",
                Content = prompt
            }
        };

        var response = await client.ChatAsync(modelName, messages, cancellationToken: cancellationToken);
        Console.WriteLine($"✅ Model '{modelName}' Response: {response}");
        return response;
    }

    [McpServerTool(Name = "llmpool_call_app")]
    [Description("Call an app in LlmPool. Apps are pre-configured workflows with prompts, tools, and models. Use this to leverage existing app capabilities.")]
    public static async Task<string> CallAppAsync(
        [Description("The app name to call (e.g., 'helper-app', 'calc')")] string appName,
        [Description("The prompt/question to send to the app")] string prompt,
        LlmPoolClient client,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"🔧 llmpool_call_app | App: {appName} | Prompt: {prompt}");

        var messages = new[]
        {
            new ClientMessage
            {
                Role = "user",
                Content = prompt
            }
        };

        var response = await client.ChatAsync(appName, messages, cancellationToken: cancellationToken);
        Console.WriteLine($"✅ App '{appName}' Response: {response}");
        return response;
    }

    [McpServerTool(Name = "get_current_time")]
    [Description("Get the current date and time")]
    public static string GetCurrentTime()
    {
        var currentActivity = Activity.Current;
        Console.WriteLine($"🔧 get_current_time | TraceId: {currentActivity?.TraceId} | SpanId: {currentActivity?.SpanId}");
        
        var now = DateTime.Now;
        var result = now.ToString("yyyy-MM-dd HH:mm:ss");
        
        Console.WriteLine($"✅ Current time: {result}");
        return result;
    }
}
