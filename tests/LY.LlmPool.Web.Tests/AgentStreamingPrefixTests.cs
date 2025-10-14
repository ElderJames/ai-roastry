#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using LY.LlmPool.Web.Services;
using LY.LlmPool.Web.Services.Agents;
using LY.LlmPool.Web.Services.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Moq;
using Xunit;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace LY.LlmPool.Web.Tests;

/// <summary>
/// 测试 Agent 流式输出时不应该在每个 token 上重复添加前缀
/// 针对问题：[Planner] 任务[Planner] 目标[Planner] ：[Planner] 解释...
/// </summary>
public class AgentStreamingPrefixTests
{
    [Fact]
    public async Task SequentialStrategy_ShouldNotAddPrefixToEachToken()
    {
        // Arrange
        var app = CreateTestApp(OrchestrationMode.Sequential);
        var strategy = new SequentialStrategy();
        var userMessages = new List<AIChatMessage>
        {
            new AIChatMessage(Microsoft.Extensions.AI.ChatRole.User, "测试问题")
        };

        var capturedChunks = new List<string>();
        var capturedAgentNames = new List<string>();

        // Mock streaming message function - 模拟 LLM 返回多个 token
        async IAsyncEnumerable<string> MockStreamingMessage(LlmConfig cfg, List<AIChatMessage> msgs, IEnumerable<KernelFunction>? tools)
        {
            var tokens = new[] { "这", "是", "一", "个", "测", "试", "回", "答" };
            foreach (var token in tokens)
            {
                await Task.Delay(1);
                yield return token;
            }
        }

        // Mock regular message function
        Task<ChatResponse> MockMessage(LlmConfig cfg, List<AIChatMessage> msgs, IEnumerable<KernelFunction>? tools)
        {
            return Task.FromResult(new ChatResponse
            {
                Status = "success",
                Message = "完整回答"
            });
        }

        // Progress callback to capture chunks
        async Task ProgressCallback(string agentName, string? role, int step, string text, bool done)
        {
            await Task.CompletedTask;
            capturedAgentNames.Add(agentName);
            capturedChunks.Add(text);
        }

        var mockToolProvider = new Mock<ToolProviderService>(null!, new NullLogger<ToolProviderService>(), null!).Object;

        // Act
        await strategy.ExecuteAsync(
            app,
            userMessages,
            mockToolProvider,
            MockMessage,
            MockStreamingMessage,
            ProgressCallback,
            CancellationToken.None
        );

        // Assert
        Assert.NotEmpty(capturedChunks);
        
        // 验证每个 chunk 都不包含前缀 [Agent]
        foreach (var chunk in capturedChunks)
        {
            Assert.DoesNotContain("[", chunk);
            Assert.DoesNotContain("]", chunk);
        }

        // 验证 chunk 是原始 token，没有被加工
        var expectedTokens = new[] { "这", "是", "一", "个", "测", "试", "回", "答" };
        for (int i = 0; i < expectedTokens.Length; i++)
        {
            Assert.Equal(expectedTokens[i], capturedChunks[i]);
        }
    }

    [Fact]
    public async Task DAGStrategy_ShouldNotAddPrefixToEachToken()
    {
        // Arrange
        var app = CreateTestDagApp();
        var strategy = new DAGStrategy();
        var userMessages = new List<AIChatMessage>
        {
            new AIChatMessage(Microsoft.Extensions.AI.ChatRole.User, "测试 DAG 问题")
        };

        var capturedChunks = new List<string>();
        var capturedAgentNames = new List<string>();

        // Mock streaming message function
        async IAsyncEnumerable<string> MockStreamingMessage(LlmConfig cfg, List<AIChatMessage> msgs, IEnumerable<KernelFunction>? tools)
        {
            var tokens = new[] { "Planner", "输", "出", "内", "容" };
            foreach (var token in tokens)
            {
                await Task.Delay(1);
                yield return token;
            }
        }

        // Mock regular message function
        Task<ChatResponse> MockMessage(LlmConfig cfg, List<AIChatMessage> msgs, IEnumerable<KernelFunction>? tools)
        {
            return Task.FromResult(new ChatResponse
            {
                Status = "success",
                Message = "完整回答"
            });
        }

        // Progress callback
        async Task ProgressCallback(string agentName, string? role, int step, string text, bool done)
        {
            await Task.CompletedTask;
            capturedAgentNames.Add(agentName);
            capturedChunks.Add(text);
        }

        var mockToolProvider = new Mock<ToolProviderService>(null!, new NullLogger<ToolProviderService>(), null!).Object;

        // Act
        await strategy.ExecuteAsync(
            app,
            userMessages,
            mockToolProvider,
            MockMessage,
            MockStreamingMessage,
            ProgressCallback,
            CancellationToken.None
        );

        // Assert - DAG 应该也不在每个 token 上添加前缀
        Assert.NotEmpty(capturedChunks);
        
        // 验证 chunk 不包含方括号前缀
        var chunksWithBrackets = capturedChunks.Where(c => c.StartsWith("[") && c.Contains("]")).ToList();
        Assert.Empty(chunksWithBrackets);
    }

    [Fact]
    public async Task SequentialStrategy_MultipleAgents_EachChunkShouldBeClean()
    {
        // Arrange - 测试多个 Agent 场景
        var app = CreateTestApp(OrchestrationMode.Sequential);
        
        // 添加第二个 agent
        var agent2 = new AgentMember
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Reviewer",
            Role = "reviewer",
            Order = 2,
            LlmAppId = app.Id!,
            LlmPromptId = app.AgentMembers.First().LlmPromptId,
            LlmConfigId = app.AgentMembers.First().LlmConfigId,
            LlmConfig = app.AgentMembers.First().LlmConfig,
            LlmPrompt = new LlmPrompt
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Reviewer-Prompt",
                Content = "你是审核员"
            }
        };
        app.AgentMembers.Add(agent2);

        var strategy = new SequentialStrategy();
        var userMessages = new List<AIChatMessage>
        {
            new AIChatMessage(Microsoft.Extensions.AI.ChatRole.User, "测试多 Agent")
        };

        var capturedByAgent = new Dictionary<string, List<string>>();

        // Mock streaming - 不同 agent 返回不同内容
        int callCount = 0;
        async IAsyncEnumerable<string> MockStreamingMessage(LlmConfig cfg, List<AIChatMessage> msgs, IEnumerable<KernelFunction>? tools)
        {
            var tokens = callCount == 0 
                ? new[] { "计", "划", "完", "成" } 
                : new[] { "审", "核", "通", "过" };
            callCount++;
            
            foreach (var token in tokens)
            {
                await Task.Delay(1);
                yield return token;
            }
        }

        Task<ChatResponse> MockMessage(LlmConfig cfg, List<AIChatMessage> msgs, IEnumerable<KernelFunction>? tools)
        {
            return Task.FromResult(new ChatResponse { Status = "success", Message = "ok" });
        }

        // Progress callback - 按 agent 分组捕获
        async Task ProgressCallback(string agentName, string? role, int step, string text, bool done)
        {
            await Task.CompletedTask;
            if (!capturedByAgent.ContainsKey(agentName))
            {
                capturedByAgent[agentName] = new List<string>();
            }
            capturedByAgent[agentName].Add(text);
        }

        var mockToolProvider = new Mock<ToolProviderService>(null!, new NullLogger<ToolProviderService>(), null!).Object;

        // Act
        await strategy.ExecuteAsync(
            app,
            userMessages,
            mockToolProvider,
            MockMessage,
            MockStreamingMessage,
            ProgressCallback,
            CancellationToken.None
        );

        // Assert
        Assert.Equal(2, capturedByAgent.Count); // 两个 agent
        
        // 验证每个 agent 的输出都不带前缀
        foreach (var kvp in capturedByAgent)
        {
            foreach (var chunk in kvp.Value)
            {
                // 每个 chunk 应该是纯文本，不包含 [AgentName] 这样的前缀
                Assert.DoesNotContain("[", chunk);
                Assert.DoesNotContain("]", chunk);
            }
        }
    }

    [Fact]
    public async Task VerifyNoPrefixDuplication_RealWorldScenario()
    {
        // 这个测试模拟真实场景：验证输出不会是 "[Planner] 任务[Planner] 目标[Planner]" 这种形式
        var app = CreateTestApp(OrchestrationMode.Sequential);
        var strategy = new SequentialStrategy();
        var userMessages = new List<AIChatMessage>
        {
            new AIChatMessage(Microsoft.Extensions.AI.ChatRole.User, "解释手冲咖啡与拿铁咖啡之间的主要区别")
        };

        var allChunks = new List<string>();
        var fullOutput = new StringBuilder();

        // 模拟 LLM 逐 token 返回
        async IAsyncEnumerable<string> MockStreamingMessage(LlmConfig cfg, List<AIChatMessage> msgs, IEnumerable<KernelFunction>? tools)
        {
            var tokens = new[] { "任务", "目标", "：", "解释", "手", "冲", "咖啡", "与", "拿", "铁", "咖啡", "之间的", "主要", "区别", "。" };
            foreach (var token in tokens)
            {
                await Task.Delay(1);
                yield return token;
            }
        }

        Task<ChatResponse> MockMessage(LlmConfig cfg, List<AIChatMessage> msgs, IEnumerable<KernelFunction>? tools)
        {
            return Task.FromResult(new ChatResponse { Status = "success", Message = "完整内容" });
        }

        async Task ProgressCallback(string agentName, string? role, int step, string text, bool done)
        {
            await Task.CompletedTask;
            allChunks.Add(text);
            fullOutput.Append(text);
        }

        var mockToolProvider = new Mock<ToolProviderService>(null!, new NullLogger<ToolProviderService>(), null!).Object;

        // Act
        await strategy.ExecuteAsync(app, userMessages, mockToolProvider, MockMessage, MockStreamingMessage, ProgressCallback);

        // Assert
        var output = fullOutput.ToString();
        
        // 验证输出不是 "[Planner] 任务[Planner] 目标[Planner] ：..." 这种形式
        // 如果修复前，每个 token 都会加 [Planner]，那么会有 15 次重复
        var prefixCount = CountOccurrences(output, "[Planner]");
        Assert.Equal(0, prefixCount); // 修复后应该没有前缀
        
        // 验证完整输出是原始 token 的拼接
        Assert.Equal("任务目标：解释手冲咖啡与拿铁咖啡之间的主要区别。", output);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static LlmApp CreateTestApp(OrchestrationMode mode)
    {
        var config = new LlmConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "TestConfig",
            Model = "gpt-4",
            IsEnabled = true
        };

        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Planner-Prompt",
            Content = "你是规划师"
        };

        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "TestAgentGroup",
            AppType = "AgentGroup",
            OrchestrationMode = mode,
            AgentMembers = new List<AgentMember>()
        };

        var member = new AgentMember
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Planner",
            Role = "planner",
            Order = 1,
            LlmAppId = app.Id!,
            LlmPromptId = prompt.Id!,
            LlmConfigId = config.Id!,
            LlmConfig = config,
            LlmPrompt = prompt
        };

        app.AgentMembers.Add(member);
        return app;
    }

    private static LlmApp CreateTestDagApp()
    {
        var app = CreateTestApp(OrchestrationMode.DAG);
        
        // 设置 DAG 配置
        app.Config = new Dictionary<string, object>
        {
            ["DAGWorkflow"] = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                MaxParallelism = 2,
                GlobalTimeoutSeconds = 300,
                FailureStrategy = "stop-on-first-failure"
            })
        };

        // 给第一个 member 添加 DAG 节点配置
        var member = app.AgentMembers.First();
        member.ConfigJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            Dependencies = Array.Empty<string>(),
            TimeoutSeconds = 60
        });

        return app;
    }
}



