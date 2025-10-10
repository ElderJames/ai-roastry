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

public class MultiAgentStreamingTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public MultiAgentStreamingTests(TestWebApplicationFactory factory)
    {
        Environment.SetEnvironmentVariable("USE_INMEMORY_DB", "true");
        _factory = factory;
    }

    // [Fact]
    public async Task AgentGroup_Stream_Includes_Both_Agent_Prefixes()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        req.Content = new StringContent("{\"model\":\"SampleAgentGroup\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(resp.IsSuccessStatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var combined = new StringBuilder();
        for (int i = 0; i < 500; i++)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break;
            if (!line.StartsWith("data: ")) continue;
            var data = line.Substring(6);
            if (data == "[DONE]") break;
            if (!data.StartsWith("{")) continue;
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                {
                    combined.Append(contentEl.GetString());
                }
            }
        }

        var text = combined.ToString();
        Assert.Contains("[Planner]", text);
        Assert.Contains("[Researcher]", text);
    }
}
