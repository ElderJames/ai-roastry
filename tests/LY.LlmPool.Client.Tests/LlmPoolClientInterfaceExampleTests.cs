using LY.LlmPool.Client;
using Moq;
using Xunit;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LY.LlmPool.Client.Tests;

/// <summary>
/// 演示如何使用 ILlmPoolClient 接口进行单元测试
/// </summary>
public class LlmPoolClientInterfaceExampleTests
{
    [Fact]
    public async Task ExampleTest_UsingMockedClient()
    {
        // Arrange - 创建 Mock 客户端
        var mockClient = new Mock<ILlmPoolClient>();
        
        // 设置 Mock 行为
        mockClient
            .Setup(x => x.ChatAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ClientMessage>>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<IEnumerable<object>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Mocked response");

        // 设置 ConversationId
        mockClient.SetupProperty(x => x.ConversationId, "test-conversation-123");

        // Act - 使用 Mock 客户端
        var client = mockClient.Object;
        var messages = new[] { new ClientMessage { Role = "user", Content = "Hello" } };
        var result = await client.ChatAsync("test-model", messages);

        // Assert
        Assert.Equal("Mocked response", result);
        Assert.Equal("test-conversation-123", client.ConversationId);
        
        // 验证方法被调用
        mockClient.Verify(
            x => x.ChatAsync(
                "test-model",
                It.IsAny<IEnumerable<ClientMessage>>(),
                null,
                null,
                null,
                default),
            Times.Once);
    }

    [Fact]
    public async Task ExampleTest_UsingMockedStreamingClient()
    {
        // Arrange - 创建 Mock 客户端
        var mockClient = new Mock<ILlmPoolClient>();
        
        // 创建模拟的流式响应
        var streamingUpdates = new[]
        {
            new StreamingChatUpdate { Text = "Hello ", Content = "Hello " },
            new StreamingChatUpdate { Text = "World", Content = "World" }
        };

        // 设置 Mock 行为 - 返回异步流
        mockClient
            .Setup(x => x.ChatStreamAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ClientMessage>>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<IEnumerable<object>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateAsyncEnumerable(streamingUpdates));

        // Act - 使用 Mock 客户端
        var client = mockClient.Object;
        var messages = new[] { new ClientMessage { Role = "user", Content = "Hello" } };
        
        var receivedTexts = new List<string>();
        await foreach (var update in client.ChatStreamAsync("test-model", messages))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                receivedTexts.Add(update.Text);
            }
        }

        // Assert
        Assert.Equal(2, receivedTexts.Count);
        Assert.Equal("Hello ", receivedTexts[0]);
        Assert.Equal("World", receivedTexts[1]);
    }

    /// <summary>
    /// 辅助方法：将数组转换为异步枚举
    /// </summary>
    private static async IAsyncEnumerable<T> CreateAsyncEnumerable<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            await Task.Yield(); // 模拟异步行为
            yield return item;
        }
    }

    /// <summary>
    /// 演示如何在服务类中使用 ILlmPoolClient
    /// </summary>
    public class MyService
    {
        private readonly ILlmPoolClient _client;

        public MyService(ILlmPoolClient client)
        {
            _client = client;
        }

        public async Task<string> ProcessUserQueryAsync(string query)
        {
            var messages = new[] { new ClientMessage { Role = "user", Content = query } };
            var response = await _client.ChatAsync("gpt-4", messages);
            return response.ToUpperInvariant();
        }
    }

    [Fact]
    public async Task ServiceTest_UsingDependencyInjection()
    {
        // Arrange - 创建 Mock 客户端
        var mockClient = new Mock<ILlmPoolClient>();
        mockClient
            .Setup(x => x.ChatAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ClientMessage>>(),
                null, null, null, default))
            .ReturnsAsync("hello world");

        // 将 Mock 注入到服务中
        var service = new MyService(mockClient.Object);

        // Act
        var result = await service.ProcessUserQueryAsync("test query");

        // Assert
        Assert.Equal("HELLO WORLD", result);
        mockClient.Verify(
            x => x.ChatAsync("gpt-4", It.IsAny<IEnumerable<ClientMessage>>(), null, null, null, default),
            Times.Once);
    }
}
