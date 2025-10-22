using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace LY.LlmPool.Web.Tests;

public class StreamingMiddlewareTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public StreamingMiddlewareTests(TestWebApplicationFactory factory)
    {
        Environment.SetEnvironmentVariable("USE_INMEMORY_DB", "true");
        _factory = factory;
    }

    [Fact]
    public async Task Streaming_Request_Should_Not_Be_Buffered_By_Middleware()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        
        var json = "{\"model\":\"Local-OpenAI-Compatible\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}";
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        
        Assert.True(resp.IsSuccessStatusCode, $"Expected success status, got {resp.StatusCode}");
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        bool sawAnyData = false;
        bool sawDone = false;
        
        // 读取 SSE 流
        for (int safety = 0; safety < 100; safety++)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break;
            
            if (line.StartsWith("data: "))
            {
                var data = line.Substring(6);
                if (data == "[DONE]")
                {
                    sawDone = true;
                    break;
                }
                else if (data.StartsWith("{"))
                {
                    sawAnyData = true;
                    using var doc = JsonDocument.Parse(data);
                    Assert.True(doc.RootElement.TryGetProperty("id", out _), "Response should have 'id' field");
                }
            }
        }

        Assert.True(sawAnyData, "Should receive at least one SSE data chunk");
        Assert.True(sawDone, "Should receive [DONE] terminator");
    }
}
