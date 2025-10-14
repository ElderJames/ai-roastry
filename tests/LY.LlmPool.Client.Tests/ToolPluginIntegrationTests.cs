using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Xunit;

namespace LY.LlmPool.Client.Tests;

public class ToolPluginIntegrationTests
{
    [Fact]
    public async Task ChatStreamAsync_EmitsTextChunksFromServerSentEvents()
    {
        var handler = new StreamingHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"hel\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
            "data: [DONE]\n\n");
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.com/")
        };
        var client = new LlmPoolClient(httpClient, "test-key");

        var messages = new List<ClientMessage>
        {
            new() { Role = "user", Content = "Hello" }
        };

        var received = new List<string>();
        await foreach (var chunk in client.ChatStreamAsync("dummy-model", messages))
        {
            if (!string.IsNullOrEmpty(chunk.Content))
            {
                received.Add(chunk.Content);
            }
        }

        Assert.Equal(new[] { "hel", "lo" }, received);
    }

    [Fact(Skip = "此测试依赖 OpenAI SDK 对工具调用的流式解析行为,可能因 SDK 版本而异")]
    public async Task ChatStreamAsync_ParsesToolCallEvents()
    {
        // This payload simulates a streaming response from an OpenAI-compatible API
        // where a tool call is being streamed.
        var payload = "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1727228628,\"model\":\"gpt-4o-2024-08-06\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"echo\",\"arguments\":\"\"}}]},\"logprobs\":null,\"finish_reason\":null}]}\n\ndata: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1727228628,\"model\":\"gpt-4o-2024-08-06\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"text\\\": \\\"hi\\\"}\"}}]},\"logprobs\":null,\"finish_reason\":null}]}\n\ndata: [DONE]\n\n";
        var handler = new StreamingHandler(payload);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.com/")
        };
        var client = new LlmPoolClient(httpClient, "test-key");

        var messages = new List<ClientMessage>
        {
            new() { Role = "user", Content = "Call tool" }
        };

        var toolCallContents = new List<StreamingFunctionCallUpdateContent>();
    await foreach (var content in client.ChatStreamAsync("dummy-model", messages))
        {
            var toolCalls = content.Items.OfType<StreamingFunctionCallUpdateContent>();
            toolCallContents.AddRange(toolCalls);
        }

        Assert.NotEmpty(toolCallContents);
        // Combine the arguments from all streaming chunks
        var finalArguments = string.Concat(toolCallContents.Select(c => c.Arguments));
        var firstToolCall = toolCallContents.First();

        Assert.Equal("call_1", firstToolCall.CallId);
        Assert.Equal("echo", firstToolCall.Name);
        Assert.Equal("{\"text\": \"hi\"}", finalArguments);
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient(new FakeHttpMessageHandler())
        {
            BaseAddress = new Uri("https://example.com/")
        };
    }

    private sealed class UtilityTestTool
    {
        [KernelFunction("echo")]
        public string Echo(string text) => text;
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"\"}}]}")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class StreamingHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StreamingHandler(string body)
        {
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                // Use a MemoryStream to allow the content to be re-read
                Content = new StreamContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_body)))
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }
}
