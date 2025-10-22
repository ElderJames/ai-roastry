#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using LY.LlmPool.Web.Services;
using LY.LlmPool.Web.Services.Agents;
using LY.LlmPool.Web.Services.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace LY.LlmPool.Web.Tests;

public class AgentOrchestratorTests
{
    private static ChatResponse Ok(string text) => new ChatResponse { Message = text, Status = "success" };

    [Fact]
    public async Task ExecuteAsync_SequentialStrategy_Returns_LastAgentOutput()
    {
        // 模拟 ChatClientService
        var chatCalls = new List<(string configId, List<Microsoft.Extensions.AI.ChatMessage> messages)>();
        var chatClient = new MockChatClientService((cfg, msgs) =>
        {
            chatCalls.Add((cfg.Id!, msgs));
            return Task.FromResult(Ok(cfg.Id == "cfg-1" ? "first" : "second"));
        });

        var toolProvider = new Mock<ToolProviderService>(null!, new NullLogger<ToolProviderService>(), null!).Object;
        var orchestrator = new LY.LlmPool.Web.Services.Agents.AgentOrchestratorService(chatClient, toolProvider, new NullLogger<LY.LlmPool.Web.Services.Agents.AgentOrchestratorService>());

        var cfg1 = new LlmConfig { Id = "cfg-1", Name = "c1", Model = "m", BaseUrl = "http://localhost" };
        var cfg2 = new LlmConfig { Id = "cfg-2", Name = "c2", Model = "m", BaseUrl = "http://localhost" };
        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "app-seq",
            AppType = "AgentGroup",
            OrchestrationMode = OrchestrationMode.Sequential,
            AgentMembers = new List<AgentMember>
            {
                new AgentMember { Id = "m1", Name = "A1", Role = "r1", Order = 1, LlmConfigId = cfg1.Id!, LlmConfig = cfg1, LlmPrompt = new LlmPrompt { Content = "p1" } },
                new AgentMember { Id = "m2", Name = "A2", Role = "r2", Order = 2, LlmConfigId = cfg2.Id!, LlmConfig = cfg2, LlmPrompt = new LlmPrompt { Content = "p2" } }
            }
        };

        var userMsgs = new List<Microsoft.Extensions.AI.ChatMessage> { new AIChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hi") };
        var answer = await orchestrator.ExecuteAsync(app, userMsgs);

        Assert.Equal("second", answer);
        Assert.Equal(2, chatCalls.Count);
        Assert.Equal("cfg-1", chatCalls[0].configId);
        Assert.Equal("cfg-2", chatCalls[1].configId);
    }

    [Fact]
    public async Task ExecuteAsync_GroupChatStrategy_Returns_SharedHistory()
    {
        var chatCalls = new List<(string configId, List<Microsoft.Extensions.AI.ChatMessage> messages)>();
        var chatClient = new MockChatClientService((cfg, msgs) =>
        {
            chatCalls.Add((cfg.Id!, msgs));
            var response = cfg.Id == "cfg-1" ? "plan step" : "FINAL: done";
            return Task.FromResult(Ok(response));
        });

        var toolProvider = new Mock<ToolProviderService>(null!, new NullLogger<ToolProviderService>(), null!).Object;
        var orchestrator = new LY.LlmPool.Web.Services.Agents.AgentOrchestratorService(chatClient, toolProvider, new NullLogger<LY.LlmPool.Web.Services.Agents.AgentOrchestratorService>());

        var cfg1 = new LlmConfig { Id = "cfg-1", Name = "c1", Model = "m", BaseUrl = "http://localhost" };
        var cfg2 = new LlmConfig { Id = "cfg-2", Name = "c2", Model = "m", BaseUrl = "http://localhost" };
        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "app-group",
            AppType = "AgentGroup",
            OrchestrationMode = OrchestrationMode.GroupChat,
            AgentMembers = new List<AgentMember>
            {
                new AgentMember { Id = "m1", Name = "Planner", Role = "planner", Order = 1, LlmConfigId = cfg1.Id!, LlmConfig = cfg1 },
                new AgentMember { Id = "m2", Name = "Researcher", Role = "researcher", Order = 2, LlmConfigId = cfg2.Id!, LlmConfig = cfg2 }
            }
        };

        var userMsgs = new List<Microsoft.Extensions.AI.ChatMessage> { new AIChatMessage(Microsoft.Extensions.AI.ChatRole.User, "task") };
        var answer = await orchestrator.ExecuteAsync(app, userMsgs);

        Assert.Equal("done", answer); // FINAL: 被提取
        Assert.Equal(2, chatCalls.Count); // 第一轮两个agent发言,第二个触发FINAL
    }

    [Fact]
    public async Task ExecuteAsync_InvalidAppType_ThrowsException()
    {
        var chatClient = new MockChatClientService((cfg, msgs) => Task.FromResult(Ok("test")));
        var toolProvider = new Mock<ToolProviderService>(null!, new NullLogger<ToolProviderService>(), null!).Object;
        var orchestrator = new LY.LlmPool.Web.Services.Agents.AgentOrchestratorService(chatClient, toolProvider, new NullLogger<LY.LlmPool.Web.Services.Agents.AgentOrchestratorService>());

        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "prompt-app",
            AppType = "Prompt", // 非AgentGroup
            OrchestrationMode = OrchestrationMode.Sequential
        };

        var userMsgs = new List<Microsoft.Extensions.AI.ChatMessage> { new AIChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hi") };
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.ExecuteAsync(app, userMsgs));
    }

    // 辅助类:模拟 IChatClientService
    private class MockChatClientService : IChatClientService
    {
        private readonly Func<LlmConfig, List<Microsoft.Extensions.AI.ChatMessage>, Task<ChatResponse>> _sendFunc;

        public MockChatClientService(Func<LlmConfig, List<Microsoft.Extensions.AI.ChatMessage>, Task<ChatResponse>> sendFunc)
        {
            _sendFunc = sendFunc;
        }

        public Task<ChatResponse> SendMessageAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null, CancellationToken cancellationToken = default)
        {
            return _sendFunc(config, messages);
        }

        public Task<ChatResponse> SendMessageAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages, Dictionary<string, object>? parameters = null, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null, CancellationToken cancellationToken = default)
        {
            return _sendFunc(config, messages);
        }

        public async IAsyncEnumerable<string> SendStreamingMessageAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
        {
            var response = await _sendFunc(config, messages);
            yield return response.Message ?? string.Empty;
        }

        public async IAsyncEnumerable<string> SendStreamingMessageAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages, Dictionary<string, object>? parameters = null, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
        {
            var response = await _sendFunc(config, messages);
            yield return response.Message ?? string.Empty;
        }

        public async IAsyncEnumerable<string> SendStreamingMessageAsync(LlmEndpoint endpoint, List<Microsoft.Extensions.AI.ChatMessage> messages, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
        {
            await Task.CompletedTask;
            yield return string.Empty;
        }

        public async IAsyncEnumerable<ChatStreamingUpdate> SendStreamingMessageWithDetailsAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages, Dictionary<string, object>? parameters = null, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
        {
            var response = await _sendFunc(config, messages);
            yield return new ChatStreamingUpdate 
            { 
                Text = response.Message ?? string.Empty
            };
        }

        public async IAsyncEnumerable<ChatStreamingUpdate> SendStreamingMessageWithDetailsAsync(LlmEndpoint endpoint, List<Microsoft.Extensions.AI.ChatMessage> messages, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
        {
            await Task.CompletedTask;
            yield return new ChatStreamingUpdate 
            { 
                Text = string.Empty
            };
        }

        public async IAsyncEnumerable<ChatStreamingUpdate> SendStreamingMessageWithDetailsAsync(LlmEndpoint endpoint, List<Microsoft.Extensions.AI.ChatMessage> messages, Dictionary<string, object>? parameters = null, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
        {
            await Task.CompletedTask;
            yield return new ChatStreamingUpdate 
            { 
                Text = string.Empty
            };
        }

        public async IAsyncEnumerable<ChatStreamingUpdate> SendStreamingMessageViaControllerAsync(string appName, List<Microsoft.Extensions.AI.ChatMessage> messages, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
        {
            await Task.CompletedTask;
            yield return new ChatStreamingUpdate 
            { 
                Text = string.Empty
            };
        }
    }
}
