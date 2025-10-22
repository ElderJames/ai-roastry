using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LY.LlmPool.Web.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace LY.LlmPool.Web.Tests;

public class ParameterInjectingHandlerTests
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<ILogger<ParameterInjectingHandler>> _mockLogger;

    public ParameterInjectingHandlerTests(ITestOutputHelper output)
    {
        _output = output;
        _mockLogger = new Mock<ILogger<ParameterInjectingHandler>>();
    }

    [Fact]
    public async Task InjectsParameters_IntoParametersField()
    {
        // Arrange
        var parameters = new Dictionary<string, object>
        {
            ["query"] = "90 + 60",
            ["temperature"] = 0.7,
            ["max_tokens"] = 1000
        };

        var handler = new ParameterInjectingHandler(parameters, _mockLogger.Object)
        {
            InnerHandler = new CaptureHandler()
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };

        var originalRequest = new
        {
            model = "calc",
            messages = new[] { new { role = "user", content = "Hello" } },
            stream = true
        };

        var originalJson = JsonSerializer.Serialize(originalRequest);
        _output.WriteLine("Original Request:");
        _output.WriteLine(originalJson);

        // Act
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(originalJson, Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(requestMessage);

        // Assert
        var capturedContent = await response.Content.ReadAsStringAsync();
        _output.WriteLine("\nModified Request:");
        _output.WriteLine(capturedContent);

        var jsonDoc = JsonDocument.Parse(capturedContent);
        var root = jsonDoc.RootElement;

        // 验证原始字段仍然存在
        Assert.True(root.TryGetProperty("model", out var modelElem));
        Assert.Equal("calc", modelElem.GetString());

        // 🎯 验证参数被注入到 parameters 字段中
        Assert.True(root.TryGetProperty("parameters", out var parametersElement), "parameters 字段应该存在");
        Assert.Equal(JsonValueKind.Object, parametersElement.ValueKind);
        
        Assert.True(parametersElement.TryGetProperty("query", out var queryElem));
        Assert.Equal("90 + 60", queryElem.GetString());
        
        Assert.True(parametersElement.TryGetProperty("temperature", out var tempElem));
        Assert.Equal(0.7, tempElem.GetDouble(), precision: 2);
        
        Assert.True(parametersElement.TryGetProperty("max_tokens", out var maxTokensElem));
        Assert.Equal(1000, maxTokensElem.GetInt32());

        // 验证参数不在顶级字段中
        Assert.False(root.TryGetProperty("query", out _), "query 不应该在顶级字段中");
        Assert.False(root.TryGetProperty("temperature", out _), "temperature 不应该在顶级字段中（如果原请求没有）");
    }

    [Fact]
    public async Task MergesWithExistingParameters()
    {
        // Arrange
        var newParameters = new Dictionary<string, object>
        {
            ["query"] = "90 + 60",
            ["userId"] = "test-user"
        };

        var handler = new ParameterInjectingHandler(newParameters, _mockLogger.Object)
        {
            InnerHandler = new CaptureHandler()
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };

        var originalRequest = new
        {
            model = "calc",
            messages = new[] { new { role = "user", content = "Hello" } },
            parameters = new { existingParam = "existing-value" }
        };

        var originalJson = JsonSerializer.Serialize(originalRequest);
        _output.WriteLine("Original Request:");
        _output.WriteLine(originalJson);

        // Act
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(originalJson, Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(requestMessage);

        // Assert
        var capturedContent = await response.Content.ReadAsStringAsync();
        _output.WriteLine("\nModified Request:");
        _output.WriteLine(capturedContent);

        var jsonDoc = JsonDocument.Parse(capturedContent);
        var root = jsonDoc.RootElement;

        Assert.True(root.TryGetProperty("parameters", out var parametersElement));
        
        // 验证已存在的参数被保留
        Assert.True(parametersElement.TryGetProperty("existingParam", out var existingElem));
        Assert.Equal("existing-value", existingElem.GetString());
        
        // 验证新参数被添加
        Assert.True(parametersElement.TryGetProperty("query", out var queryElem));
        Assert.Equal("90 + 60", queryElem.GetString());
        
        Assert.True(parametersElement.TryGetProperty("userId", out var userIdElem));
        Assert.Equal("test-user", userIdElem.GetString());
    }

    [Fact]
    public async Task DoesNotOverrideExistingParameters()
    {
        // Arrange
        var newParameters = new Dictionary<string, object>
        {
            ["query"] = "new-value", // 尝试覆盖
            ["userId"] = "test-user"
        };

        var handler = new ParameterInjectingHandler(newParameters, _mockLogger.Object)
        {
            InnerHandler = new CaptureHandler()
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };

        var originalRequest = new
        {
            model = "calc",
            messages = new[] { new { role = "user", content = "Hello" } },
            parameters = new { query = "original-value" }
        };

        var originalJson = JsonSerializer.Serialize(originalRequest);
        _output.WriteLine("Original Request:");
        _output.WriteLine(originalJson);

        // Act
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(originalJson, Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(requestMessage);

        // Assert
        var capturedContent = await response.Content.ReadAsStringAsync();
        _output.WriteLine("\nModified Request:");
        _output.WriteLine(capturedContent);

        var jsonDoc = JsonDocument.Parse(capturedContent);
        var root = jsonDoc.RootElement;

        Assert.True(root.TryGetProperty("parameters", out var parametersElement));
        
        // 🎯 验证原有参数不被覆盖
        Assert.True(parametersElement.TryGetProperty("query", out var queryElem));
        Assert.Equal("original-value", queryElem.GetString());
        
        // 验证新参数被添加
        Assert.True(parametersElement.TryGetProperty("userId", out var userIdElem));
        Assert.Equal("test-user", userIdElem.GetString());
    }

    [Fact]
    public async Task HandlesEmptyParameters()
    {
        // Arrange
        var parameters = new Dictionary<string, object>();

        var handler = new ParameterInjectingHandler(parameters, _mockLogger.Object)
        {
            InnerHandler = new CaptureHandler()
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };

        var originalRequest = new
        {
            model = "calc",
            messages = new[] { new { role = "user", content = "Hello" } }
        };

        var originalJson = JsonSerializer.Serialize(originalRequest);

        // Act
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(originalJson, Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(requestMessage);

        // Assert
        var capturedContent = await response.Content.ReadAsStringAsync();
        var jsonDoc = JsonDocument.Parse(capturedContent);
        var root = jsonDoc.RootElement;

        // 空参数时，应该直接传递原请求
        Assert.Equal(originalJson, capturedContent);
    }

    [Fact]
    public async Task HandlesNullParameters()
    {
        // Arrange
        var handler = new ParameterInjectingHandler(null, _mockLogger.Object)
        {
            InnerHandler = new CaptureHandler()
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };

        var originalRequest = new
        {
            model = "calc",
            messages = new[] { new { role = "user", content = "Hello" } }
        };

        var originalJson = JsonSerializer.Serialize(originalRequest);

        // Act
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(originalJson, Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(requestMessage);

        // Assert
        var capturedContent = await response.Content.ReadAsStringAsync();

        // null 参数时，应该直接传递原请求
        Assert.Equal(originalJson, capturedContent);
    }

    [Fact]
    public async Task HandlesVariousDataTypes()
    {
        // Arrange
        var parameters = new Dictionary<string, object>
        {
            ["stringParam"] = "test-string",
            ["intParam"] = 42,
            ["longParam"] = 9876543210L,
            ["floatParam"] = 3.14f,
            ["doubleParam"] = 2.718281828,
            ["boolParam"] = true,
            ["arrayParam"] = new[] { "a", "b", "c" },
            ["objectParam"] = new { nested = "value" }
        };

        var handler = new ParameterInjectingHandler(parameters, _mockLogger.Object)
        {
            InnerHandler = new CaptureHandler()
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };

        var originalRequest = new
        {
            model = "test",
            messages = new[] { new { role = "user", content = "Hello" } }
        };

        var originalJson = JsonSerializer.Serialize(originalRequest);

        // Act
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(originalJson, Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(requestMessage);

        // Assert
        var capturedContent = await response.Content.ReadAsStringAsync();
        _output.WriteLine("Modified Request:");
        _output.WriteLine(capturedContent);

        var jsonDoc = JsonDocument.Parse(capturedContent);
        var root = jsonDoc.RootElement;
        Assert.True(root.TryGetProperty("parameters", out var parametersElement));

        // 验证各种数据类型
        Assert.Equal("test-string", parametersElement.GetProperty("stringParam").GetString());
        Assert.Equal(42, parametersElement.GetProperty("intParam").GetInt32());
        Assert.Equal(9876543210L, parametersElement.GetProperty("longParam").GetInt64());
        Assert.Equal(3.14f, parametersElement.GetProperty("floatParam").GetSingle(), precision: 2);
        Assert.Equal(2.718281828, parametersElement.GetProperty("doubleParam").GetDouble(), precision: 6);
        Assert.True(parametersElement.GetProperty("boolParam").GetBoolean());
        
        var arrayElem = parametersElement.GetProperty("arrayParam");
        Assert.Equal(JsonValueKind.Array, arrayElem.ValueKind);
        Assert.Equal(3, arrayElem.GetArrayLength());
        
        var objectElem = parametersElement.GetProperty("objectParam");
        Assert.Equal(JsonValueKind.Object, objectElem.ValueKind);
        Assert.Equal("value", objectElem.GetProperty("nested").GetString());
    }

    [Fact]
    public async Task IgnoresNonPostRequests()
    {
        // Arrange
        var parameters = new Dictionary<string, object>
        {
            ["query"] = "test"
        };

        var handler = new ParameterInjectingHandler(parameters, _mockLogger.Object)
        {
            InnerHandler = new CaptureHandler()
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };

        // Act
        var requestMessage = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        var response = await client.SendAsync(requestMessage);

        // Assert - GET 请求应该不被修改
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// 简单的 HttpMessageHandler，用于捕获并返回请求内容
    /// </summary>
    private class CaptureHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = string.Empty;
            if (request.Content != null)
            {
                content = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            // 返回请求内容作为响应
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        }
    }
}
