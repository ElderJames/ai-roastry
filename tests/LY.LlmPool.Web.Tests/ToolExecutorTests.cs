#nullable enable
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LY.LlmPool.Web.Services.Agents;
using Xunit;

namespace LY.LlmPool.Web.Tests;

public class ToolExecutorTests
{
    private sealed class TestInternalTool : IAgentTool
    {
        public string Id => "test-internal";
        public string Name => "TestTool";
        public string? Description => "Test internal tool";
        public JsonElement? ParametersSchema => JsonDocument.Parse("{}").RootElement;

        public (bool ok, string? error) Validate(JsonElement? args) => (true, null);

        public Task<string> ExecuteAsync(JsonElement? args, CancellationToken ct = default)
        {
            return Task.FromResult("internal-result");
        }
    }

    private sealed class TestMcpTool : IAgentTool
    {
        public string Id => "test-mcp";
        public string Name => "TestMcpTool";
        public string? Description => "Test MCP tool";
        public JsonElement? ParametersSchema => JsonDocument.Parse("{}").RootElement;

        public (bool ok, string? error) Validate(JsonElement? args) => (true, null);

        public Task<string> ExecuteAsync(JsonElement? args, CancellationToken ct = default)
        {
            return Task.FromResult("mcp-result");
        }
    }

    private sealed class TestUnknownTool : IAgentTool
    {
        public string Id => "test-unknown";
        public string Name => "UnknownTool";
        public string? Description => "Unknown tool";
        public JsonElement? ParametersSchema => JsonDocument.Parse("{}").RootElement;

        public (bool ok, string? error) Validate(JsonElement? args) => (true, null);

        public Task<string> ExecuteAsync(JsonElement? args, CancellationToken ct = default)
        {
            return Task.FromResult("unknown-result");
        }
    }

    [Fact]
    public async Task ExecuteAsync_InternalTool_Returns_Result()
    {
        var executor = new ToolExecutor();
        var tool = new TestInternalTool();
        var args = JsonDocument.Parse("{}").RootElement;

        var (ok, output, error) = await executor.ExecuteAsync(tool, args);

        Assert.True(ok);
        Assert.Equal("internal-result", output);
        Assert.Null(error);
    }

    [Fact]
    public async Task ExecuteAsync_McpTool_Returns_Result()
    {
        var executor = new ToolExecutor();
        var tool = new TestMcpTool();
        var args = JsonDocument.Parse("{}").RootElement;

        var (ok, output, error) = await executor.ExecuteAsync(tool, args);

        Assert.True(ok);
        Assert.Equal("mcp-result", output);
        Assert.Null(error);
    }

    [Fact]
    public async Task ExecuteAsync_ValidTool_Returns_Success()
    {
        var executor = new ToolExecutor();
        var tool = new TestUnknownTool();

        var (ok, output, error) = await executor.ExecuteAsync(tool, null);

        Assert.True(ok);
        Assert.Equal("unknown-result", output);
        Assert.Null(error);
    }

    [Fact]
    public async Task ExecuteAsync_InternalTool_InvalidArgs_Returns_Error()
    {
        var executor = new ToolExecutor();
        // Create a tool that requires arguments but we'll pass null
        var schema = JsonDocument.Parse("{\"type\":\"object\",\"required\":[\"param\"]}").RootElement;
        var tool = new InternalPluginTool("test", "TestTool", null, schema);

        var (ok, output, error) = await executor.ExecuteAsync(tool, null);

        Assert.False(ok);
        Assert.Equal(string.Empty, output);
        Assert.NotNull(error);
    }
}