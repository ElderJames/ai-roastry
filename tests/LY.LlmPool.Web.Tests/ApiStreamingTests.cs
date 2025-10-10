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

public class ApiStreamingTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ApiStreamingTests(TestWebApplicationFactory factory)
    {
        // Ensure test-friendly environment; upstream calls are intercepted by TestWebApplicationFactory
        Environment.SetEnvironmentVariable("USE_INMEMORY_DB", "true");
        _factory = factory;
    }

    // [Fact]
    public async Task AgentGroup_Stream_Emits_Chunks_And_Done()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        var payload = new
        {
            model = "SampleAgentGroup",
            stream = true,
            messages = new object[]
            {
                new { role = "user", content = "hello" }
            }
        };
        var json = "{\"model\":\"SampleAgentGroup\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}";
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(resp.IsSuccessStatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        bool sawAnyData = false;
        bool sawDone = false;
        for (int safety = 0; safety < 500; safety++)
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
                    Assert.True(doc.RootElement.TryGetProperty("id", out _));
                }
            }
        }

        Assert.True(sawAnyData, "should receive at least one SSE data chunk");
        Assert.True(sawDone, "should receive [DONE] terminator");
    }
}
