#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using LY.LlmPool.Web.Services;
using LY.LlmPool.Web.Services.Agents;
using Xunit;

namespace LY.LlmPool.Web.Tests;

public class ReActEngineTests
{
    private sealed class StubChatClient : IChatClientService
    {
        private readonly Queue<ChatResponse> _responses = new();
        public void Enqueue(ChatResponse r) => _responses.Enqueue(r);
        public Task<ChatResponse> SendMessageAsync(LlmConfig config, List<ChatMessage> messages, IEnumerable<object>? toolObjects = null)
        {
            if (_responses.Count == 0) throw new InvalidOperationException("No stub responses queued");
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<string> SendStreamingMessageAsync(LlmConfig config, List<ChatMessage> messages, IEnumerable<object>? toolObjects = null)
        {
            var response = await SendMessageAsync(config, messages, toolObjects);
            yield return response.Message ?? string.Empty;
        }

        public async IAsyncEnumerable<string> SendStreamingMessageAsync(LlmEndpoint endpoint, List<ChatMessage> messages, IEnumerable<object>? toolObjects = null)
        {
            await Task.CompletedTask;
            yield return string.Empty;
        }
    }

    private sealed class PassThroughExecutor : IToolExecutor
    {
        public List<(string tool, string? args)> Calls { get; } = new();
        public Task<(bool ok, string output, string? error)> ExecuteAsync(IAgentTool tool, JsonElement? args, CancellationToken ct = default)
        {
            Calls.Add((tool.Name, args?.ToString()));
            return Task.FromResult<(bool, string, string?)>((true, $"{tool.Name}-result", null));
        }
    }

    [Fact]
    public async Task NoTools_Returns_Model_Text()
    {
        var chat = new StubChatClient();
        chat.Enqueue(new ChatResponse { Message = "hello", Status = "success", ToolCalls = null });
        var engine = new ReActEngine(chat, new PassThroughExecutor());
        var config = new LlmConfig { ApiKey = "k", Model = "m" };
        var messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "hi" } };
        var result = await engine.RunAsync(config, messages);
        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task ToolCall_Executes_Then_Final_Text()
    {
        var chat = new StubChatClient();
        // 第一次返回包含一个 tool call
        chat.Enqueue(new ChatResponse
        {
            Message = "thinking",
            Status = "success",
            ToolCalls = new List<ToolCall>
            {
                new ToolCall
                {
                    Id = "tc1",
                    Type = "function",
                    Function = new ToolFunction { Name = "Echo", Arguments = "{\"q\":\"hi\"}" }
                }
            }
        });
        // 第二次返回最终文本
        chat.Enqueue(new ChatResponse { Message = "final answer", Status = "success", ToolCalls = null });

        using var schemaDoc = JsonDocument.Parse("{\"type\":\"object\"}");
        var tools = new List<IAgentTool> { new InternalPluginTool("t1", "Echo", schema: schemaDoc.RootElement) };
        var exec = new PassThroughExecutor();
        var engine = new ReActEngine(chat, exec);

        var config = new LlmConfig { ApiKey = "k", Model = "m" };
        var messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "hi" } };
        var result = await engine.RunAsync(config, messages, tools);

        Assert.Equal("final answer", result);
    Assert.Single(exec.Calls);
    Assert.Equal("Echo", exec.Calls[0].tool);
    }

    [Fact]
    public async Task MissingTool_Falls_Back_To_Model_Text()
    {
        var chat = new StubChatClient();
        chat.Enqueue(new ChatResponse
        {
            Message = "model text",
            Status = "success",
            ToolCalls = new List<ToolCall> { new ToolCall { Function = new ToolFunction { Name = "NotExist", Arguments = "{}" } } }
        });

        var engine = new ReActEngine(chat, new PassThroughExecutor());
        var config = new LlmConfig { ApiKey = "k", Model = "m" };
        var messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "hi" } };
        var result = await engine.RunAsync(config, messages, tools: new List<IAgentTool>());
        Assert.Equal("model text", result);
    }
}
