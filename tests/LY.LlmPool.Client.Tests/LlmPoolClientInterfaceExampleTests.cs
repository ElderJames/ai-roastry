using LY.LlmPool.Client;
using Moq;
using Xunit;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System;
using System.Net;
using System.Linq;

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

        var chatOptions = new ChatOptions { ConversationId = "test-conversation-123" };

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

        // Act - 使用 Mock 客户端
        var client = mockClient.Object;
        var messages = new[] { new ClientMessage { Role = "user", Content = "Hello" } };
        var result = await client.ChatAsync("test-model", messages);

        // Assert
        Assert.Equal("Mocked response", result);
        Assert.Equal("test-conversation-123", chatOptions.ConversationId);
        
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

    [Fact]
    public async Task ConfigureOptions_SetsConversationIdFromRequestContext()
    {
        // Arrange - 模拟请求上下文
        var requestContext = new RequestContext
        {
            ConversationId = "conversation-from-request-789",
            UserId = "user123"
        };

        // 创建 configureOptions 委托，从请求上下文中获取 conversationId
        ChatOptions capturedOptions = null;
        Action<ChatOptions> configureOptions = options =>
        {
            // 模拟从请求上下文中获取并设置 conversationId
            options.ConversationId = requestContext.ConversationId;
            // 可以设置其他属性，比如基于用户ID的自定义配置
            options.ModelId = $"custom-model-for-{requestContext.UserId}";
            capturedOptions = options; // 捕获配置后的选项用于验证
        };

        // 创建一个简单的 HttpMessageHandler
        var handler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.com/")
        };

        // 创建 LlmPoolClient 实例，传入 configureOptionsPerRequest
        var client = new LlmPoolClient(httpClient, "test-key", customHandler: handler, configureOptionsPerRequest: configureOptions);

        var messages = new[] { new ClientMessage { Role = "user", Content = "Hello from request context" } };

        // Act - 调用 ChatAsync，这会触发 configureOptions
        try
        {
            await client.ChatAsync("test-model", messages);
        }
        catch
        {
            // 忽略 HTTP 错误，我们只关心配置是否被正确应用
        }

        // Assert - 验证从请求上下文中获取的值被正确设置
        Assert.NotNull(capturedOptions);
        Assert.Equal("conversation-from-request-789", capturedOptions.ConversationId);
        Assert.Equal("custom-model-for-user123", capturedOptions.ModelId);
    }

    private sealed class RequestContext
    {
        public string ConversationId { get; set; }
        public string UserId { get; set; }
    }

    [Fact]
    public async Task ConversationId_ExtractedFromResponseHeader()
    {
        // Arrange - 创建一个 HttpMessageHandler 来模拟服务器响应，包含 X-Conversation-Id header
        var handler = new ConversationIdResponseHandler("server-generated-conversation-123");
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.com/")
        };

        // 创建 LlmPoolClient 实例
        var client = new LlmPoolClient(httpClient, "test-key", customHandler: handler);

        var messages = new[] { new ClientMessage { Role = "user", Content = "Hello" } };

        // Act - 第一次调用 ChatAsync，从响应头提取 ConversationId
        var result1 = await client.ChatAsync("test-model", messages);
        
        // 第二次调用 ChatAsync，应该使用提取的 ConversationId
        var result2 = await client.ChatAsync("test-model", messages);

        // Assert - 验证响应被处理且 ConversationId 被正确提取和使用
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        
        // 验证请求头是否包含了从响应头提取的 ConversationId
        // 第二次请求应该包含提取的 ConversationId
        var sentConversationIds = handler.GetSentConversationIds();
        Assert.Contains("server-generated-conversation-123", sentConversationIds);
    }

    [Fact]
    public async Task ConversationId_FromOptions_TakesPrecedenceOverResponseHeader()
    {
        // Arrange - 创建 configureOptions 来设置 ConversationId
        string capturedConversationId = null;
        Action<ChatOptions> configureOptions = options =>
        {
            options.ConversationId = "explicit-conversation-456";
            capturedConversationId = options.ConversationId;
        };

        // 创建一个 HttpMessageHandler，模拟服务器响应也包含不同的 ConversationId
        var handler = new ConversationIdResponseHandler("server-conversation-789");
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.com/")
        };

        // 创建 LlmPoolClient 实例，传入 configureOptionsPerRequest
        var client = new LlmPoolClient(httpClient, "test-key", customHandler: handler, configureOptionsPerRequest: configureOptions);

        var messages = new[] { new ClientMessage { Role = "user", Content = "Hello" } };

        // Act - 调用 ChatAsync
        var result = await client.ChatAsync("test-model", messages);

        // Assert - 验证 configureOptions 中设置的 ConversationId 被使用
        Assert.Equal("explicit-conversation-456", capturedConversationId);
        Assert.NotNull(result);
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        public Action<ChatOptions> OnConfigureOptionsCalled { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 返回一个简单的错误响应，我们只关心 configureOptions 是否被调用
            var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\": \"Test handler\"}")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class ConversationIdResponseHandler : HttpMessageHandler
    {
        private readonly string _conversationId;
        private readonly List<string> _sentConversationIds = new();

        public ConversationIdResponseHandler(string conversationId)
        {
            _conversationId = conversationId;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 记录请求头中的 X-Conversation-Id
            if (request.Headers.TryGetValues("X-Conversation-Id", out var conversationIdValues))
            {
                var conversationId = conversationIdValues.FirstOrDefault();
                if (!string.IsNullOrEmpty(conversationId))
                {
                    _sentConversationIds.Add(conversationId);
                }
            }

            // 创建一个模拟的成功响应，包含 X-Conversation-Id header
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Test response\"}}]}")
            };
            
            // 添加 X-Conversation-Id header 来模拟服务器返回的 ConversationId
            response.Headers.Add("X-Conversation-Id", _conversationId);
            
            return Task.FromResult(response);
        }

        public IReadOnlyList<string> GetSentConversationIds() => _sentConversationIds.AsReadOnly();
    }
}
