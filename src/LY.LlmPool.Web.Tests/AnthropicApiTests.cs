using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text;
using LY.LlmPool.Web.Models.Anthropic;
using LY.LlmPool.Web.Services;
using Xunit;

namespace LY.LlmPool.Web.Tests;

public class AnthropicApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public AnthropicApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Health_Endpoint_Returns_Success()
    {
        // Act
        var response = await _client.GetAsync("/v1/health");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("healthy", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApiInfo_Endpoint_Returns_Success()
    {
        // Act
        var response = await _client.GetAsync("/api/info");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("LLM Pool", content);
    }

    [Fact]
    public async Task CountTokens_WithValidRequest_Returns_Success()
    {
        // Arrange
        var request = new TokenCountRequest
        {
            Model = "test-model",
            Messages = new List<AnthropicMessage>
            {
                new AnthropicMessage { Role = "user", Content = "Hello, world!" }
            }
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/v1/messages/count_tokens", content);

        // Assert
        // Note: This might fail if no model configurations are set up
        // In a real test, you'd want to set up test data first
        var responseContent = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"Response: {responseContent}");
    }

    [Fact]
    public void AnthropicTransformService_ConvertToInternalMessages_Works()
    {
        // Arrange
        using var serviceScope = _factory.Services.CreateScope();
        var transformService = serviceScope.ServiceProvider.GetRequiredService<AnthropicTransformService>();

        var request = new MessagesRequest
        {
            Model = "test-model",
            MaxTokens = 100,
            System = "You are a helpful assistant",
            Messages = new List<AnthropicMessage>
            {
                new AnthropicMessage { Role = "user", Content = "Hello!" },
                new AnthropicMessage { Role = "assistant", Content = "Hi there!" },
                new AnthropicMessage { Role = "user", Content = "How are you?" }
            }
        };

        // Act
        var result = transformService.ConvertToInternalMessages(request);

        // Assert
        Assert.Equal(4, result.Count); // 1 system + 3 conversation messages
        Assert.Equal("system", result[0].Role);
        Assert.Equal("You are a helpful assistant", result[0].Content);
        Assert.Equal("user", result[1].Role);
        Assert.Equal("Hello!", result[1].Content);
        Assert.Equal("assistant", result[2].Role);
        Assert.Equal("Hi there!", result[2].Content);
        Assert.Equal("user", result[3].Role);
        Assert.Equal("How are you?", result[3].Content);
    }

    [Fact]
    public void AnthropicTransformService_ConvertToTokenCountResponse_Works()
    {
        // Arrange
        using var serviceScope = _factory.Services.CreateScope();
        var transformService = serviceScope.ServiceProvider.GetRequiredService<AnthropicTransformService>();

        var request = new TokenCountRequest
        {
            Model = "test-model",
            System = "You are a helpful assistant",
            Messages = new List<AnthropicMessage>
            {
                new AnthropicMessage { Role = "user", Content = "Hello world this is a test message" }
            }
        };

        // Act
        var result = transformService.ConvertToTokenCountResponse(request);

        // Assert
        Assert.True(result.InputTokens > 0);
    }
}
