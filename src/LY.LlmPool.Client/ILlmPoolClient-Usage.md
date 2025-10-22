# ILlmPoolClient 接口使用指南

## 概述

`ILlmPoolClient` 接口提供了 LlmPoolClient 的抽象,便于进行依赖注入和单元测试。

## 依赖注入使用

### 方式 1: 使用 AddLlmPoolClient 扩展方法

```csharp
using LY.LlmPool.Client.Extensions;

// 在 Program.cs 或 Startup.cs 中
services.AddLlmPoolClient(
    baseUrl: "http://localhost:5071/v1",
    apiKey: "your-api-key"
);

// 在服务中注入
public class MyService
{
    private readonly ILlmPoolClient _client;

    public MyService(ILlmPoolClient client)
    {
        _client = client;
    }

    public async Task<string> GetResponseAsync(string prompt)
    {
        var messages = new[] { new ClientMessage { Role = "user", Content = prompt } };
        return await _client.ChatAsync("gpt-4", messages);
    }
}
```

### 方式 2: 手动注册

```csharp
// 注册为单例
services.AddSingleton<ILlmPoolClient>(sp => 
    new LlmPoolClient("http://localhost:5071/v1", "your-api-key"));

// 或注册为作用域服务
services.AddScoped<ILlmPoolClient>(sp => 
    new LlmPoolClient("http://localhost:5071/v1", "your-api-key"));
```

## 单元测试使用

### 使用 Moq 创建 Mock 客户端

```csharp
using Moq;
using LY.LlmPool.Client;

[Fact]
public async Task TestMyService_WithMockedClient()
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
        .ReturnsAsync("Mocked AI response");

    // 注入到被测试的服务
    var service = new MyService(mockClient.Object);

    // Act
    var result = await service.GetResponseAsync("Hello");

    // Assert
    Assert.Equal("Mocked AI response", result);
    
    // 验证调用
    mockClient.Verify(
        x => x.ChatAsync(
            "gpt-4",
            It.Is<IEnumerable<ClientMessage>>(msgs => msgs.First().Content == "Hello"),
            null, null, null, default),
        Times.Once);
}
```

### 测试流式响应

```csharp
[Fact]
public async Task TestStreamingResponse_WithMockedClient()
{
    // Arrange
    var mockClient = new Mock<ILlmPoolClient>();
    
    var streamingUpdates = new[]
    {
        new StreamingChatUpdate { Text = "Hello ", Content = "Hello " },
        new StreamingChatUpdate { Text = "World", Content = "World" }
    };

    mockClient
        .Setup(x => x.ChatStreamAsync(
            It.IsAny<string>(),
            It.IsAny<IEnumerable<ClientMessage>>(),
            It.IsAny<Dictionary<string, object>>(),
            It.IsAny<IEnumerable<object>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
        .Returns(CreateAsyncEnumerable(streamingUpdates));

    var service = new MyService(mockClient.Object);

    // Act
    var results = new List<string>();
    await foreach (var update in mockClient.Object.ChatStreamAsync("gpt-4", messages))
    {
        if (!string.IsNullOrEmpty(update.Text))
        {
            results.Add(update.Text);
        }
    }

    // Assert
    Assert.Equal(2, results.Count);
    Assert.Equal("Hello ", results[0]);
    Assert.Equal("World", results[1]);
}

// 辅助方法
private static async IAsyncEnumerable<T> CreateAsyncEnumerable<T>(IEnumerable<T> items)
{
    foreach (var item in items)
    {
        await Task.Yield();
        yield return item;
    }
}
```

### 测试 ConversationId

```csharp
[Fact]
public async Task TestConversationId_IsPreserved()
{
    // Arrange
    var mockClient = new Mock<ILlmPoolClient>();
    mockClient.SetupProperty(x => x.ConversationId, "test-conversation-123");
    mockClient
        .Setup(x => x.ChatAsync(
            It.IsAny<string>(),
            It.IsAny<IEnumerable<ClientMessage>>(),
            It.IsAny<Dictionary<string, object>>(),
            It.IsAny<IEnumerable<object>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync("Response");

    var client = mockClient.Object;

    // Act
    var conversationId = client.ConversationId;
    await client.ChatAsync("gpt-4", messages);

    // Assert
    Assert.Equal("test-conversation-123", conversationId);
}
```

## 接口定义

```csharp
public interface ILlmPoolClient
{
    /// <summary>
    /// 当前会话的 ConversationId
    /// </summary>
    string? ConversationId { get; set; }

    /// <summary>
    /// 发送非流式聊天请求
    /// </summary>
    Task<string> ChatAsync(
        string model,
        IEnumerable<ClientMessage> messages,
        Dictionary<string, object>? parameters = null,
        IEnumerable<object>? toolObjects = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送流式聊天请求
    /// </summary>
    IAsyncEnumerable<StreamingChatUpdate> ChatStreamAsync(
        string model,
        IEnumerable<ClientMessage> messages,
        Dictionary<string, object>? parameters = null,
        IEnumerable<object>? toolObjects = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

## 测试示例

完整的测试示例可以在 `tests/LY.LlmPool.Client.Tests/LlmPoolClientInterfaceExampleTests.cs` 中找到。

## 注意事项

1. **单例 vs 作用域**: 如果需要管理不同的 ConversationId,建议使用作用域服务
2. **线程安全**: `LlmPoolClient` 内部使用 `HttpClient`,是线程安全的
3. **资源释放**: 通过 DI 容器管理的客户端会自动释放资源
4. **Mock 验证**: 使用 `It.IsAny<T>()` 可以匹配任意参数,使用 `It.Is<T>(predicate)` 可以进行条件匹配
