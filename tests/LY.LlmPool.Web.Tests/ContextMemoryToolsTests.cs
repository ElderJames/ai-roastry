#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using LY.LlmPool.Web.Services.Agents;
using LY.LlmPool.Web.Services;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace LY.LlmPool.Web.Tests;

public class ContextMemoryToolsTests
{
    [Fact]
    public async Task ContextExtractorTool_ValidatesAndExecutes()
    {
        var memoryStore = new ContextMemoryStore();
        var chatClient = new MockChatClientService((cfg, msgs) => Task.FromResult(new ChatResponse { Message = "Test summary", Status = "success" }));

        var tool = new ContextExtractorTool("extractor-1", memoryStore, chatClient);

        var args = JsonSerializer.Deserialize<JsonElement>(@"{
            ""conversation_id"": ""conv-123"",
            ""content"": ""This is a test conversation.""
        }");

        var (ok, error) = tool.Validate(args);
        Assert.True(ok);
        Assert.Null(error);

        var result = await tool.ExecuteAsync(args);
        Assert.Contains("extraction started", result.ToLower());

        // 等待后台任务完成
        await Task.Delay(100);

        var memories = await memoryStore.QueryMemoriesAsync("conv-123");
        Assert.Single(memories);
        Assert.Equal("Test summary", memories[0].Summary);
    }

    [Fact]
    public async Task MemoryQueryTool_QueriesStoredMemories()
    {
        var memoryStore = new ContextMemoryStore();
        await memoryStore.StoreSummaryAsync("conv-456", "First summary", "test");
        await memoryStore.StoreSummaryAsync("conv-456", "Second summary", "test");

        var tool = new MemoryQueryTool("query-1", memoryStore);

        var args = JsonSerializer.Deserialize<JsonElement>(@"{
            ""conversation_id"": ""conv-456""
        }");

        var (ok, error) = tool.Validate(args);
        Assert.True(ok);
        Assert.Null(error);

        var result = await tool.ExecuteAsync(args);
        Assert.Contains("Found 2 memory entries", result);
        Assert.Contains("First summary", result);
        Assert.Contains("Second summary", result);
    }

    [Fact]
    public async Task MemoryQueryTool_NoMemories_ReturnsMessage()
    {
        var memoryStore = new ContextMemoryStore();
        var tool = new MemoryQueryTool("query-1", memoryStore);

        var args = JsonSerializer.Deserialize<JsonElement>(@"{
            ""conversation_id"": ""nonexistent""
        }");

        var result = await tool.ExecuteAsync(args);
        Assert.Contains("No stored memories", result);
    }

    [Fact]
    public void ContextExtractorTool_InvalidArgs_ValidationFails()
    {
        var memoryStore = new ContextMemoryStore();
        var chatClient = new MockChatClientService((cfg, msgs) => Task.FromResult(new ChatResponse { Message = "summary", Status = "success" }));

        var tool = new ContextExtractorTool("extractor-1", memoryStore, chatClient);

        // Missing conversation_id
        var args1 = JsonSerializer.Deserialize<JsonElement>(@"{
            ""content"": ""test""
        }");

        var (ok1, error1) = tool.Validate(args1);
        Assert.False(ok1);
        Assert.Contains("conversation_id", error1);

        // Missing content
        var args2 = JsonSerializer.Deserialize<JsonElement>(@"{
            ""conversation_id"": ""conv-123""
        }");

        var (ok2, error2) = tool.Validate(args2);
        Assert.False(ok2);
        Assert.Contains("content", error2);
    }

    // 辅助类：模拟 IChatClientService
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
