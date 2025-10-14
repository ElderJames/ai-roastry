using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

#nullable enable

namespace LY.LlmPool.Client.Tests;

public class ParameterInjectionTests
{
    private readonly ITestOutputHelper _output;

    public ParameterInjectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ParameterInjectionHandler_InjectsParametersIntoRequestBody()
    {
        // Arrange - 使用反射访问 file class
        var handlerType = typeof(LlmPoolClient).Assembly.GetTypes()
            .First(t => t.Name == "ParameterInjectionHandler");
        
        var parameters = new Dictionary<string, object>
        {
            ["temperature"] = 0.8,
            ["max_tokens"] = 2000,
            ["custom_param"] = "custom_value"
        };

        var handler = (DelegatingHandler)Activator.CreateInstance(handlerType, parameters)!;
        
        // 设置内部 handler 来捕获请求
        string? capturedContent = null;
        handler.InnerHandler = new CaptureHandler((content) => capturedContent = content);

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };

        // 创建一个模拟的 OpenAI 请求
        var originalRequest = new
        {
            model = "gpt-4",
            messages = new[] { new { role = "user", content = "Hello" } },
            stream = false
        };

        var originalJson = JsonSerializer.Serialize(originalRequest);
        _output.WriteLine("Original Request:");
        _output.WriteLine(originalJson);

        // Act
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(originalJson, Encoding.UTF8, "application/json")
        };

        await client.SendAsync(requestMessage);

        // Assert
        Assert.NotNull(capturedContent);
        _output.WriteLine("\nRequest After Parameter Injection:");
        _output.WriteLine(capturedContent);

        var jsonDoc = JsonDocument.Parse(capturedContent);
        var root = jsonDoc.RootElement;

        // 验证原始字段仍然存在
        Assert.True(root.TryGetProperty("model", out var modelElem));
        Assert.Equal("gpt-4", modelElem.GetString());

        // 验证参数被注入
        Assert.True(root.TryGetProperty("parameters", out var parametersElement));
        Assert.Equal(0.8, parametersElement.GetProperty("temperature").GetDouble());
        Assert.Equal(2000, parametersElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("custom_value", parametersElement.GetProperty("custom_param").GetString());
    }

    // 简单的 handler 用于捕获请求内容
    private class CaptureHandler : HttpMessageHandler
    {
        private readonly Action<string> _onCapture;

        public CaptureHandler(Action<string> onCapture)
        {
            _onCapture = onCapture;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                var content = await request.Content.ReadAsStringAsync(cancellationToken);
                _onCapture(content);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
