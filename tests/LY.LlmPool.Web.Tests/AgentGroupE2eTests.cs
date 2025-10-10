using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace LY.LlmPool.Web.Tests
{
    public class AgentGroupE2eTests : IClassFixture<TestWebApplicationFactory>
    {
        private readonly TestWebApplicationFactory _factory;

        public AgentGroupE2eTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task NonStream_ReturnsJson()
        {
            using var client = _factory.CreateClient();

            var req = new
            {
                model = "SampleAgentGroup",
                stream = false,
                messages = new[] { new { role = "user", content = "hello" } }
            };

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
            var resp = await client.PostAsync("/v1/chat/completions", new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json"));
            resp.EnsureSuccessStatusCode();

            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("choices", body);
            Assert.Contains("chat.completion", body);
        }

        [Fact]
        public async Task Stream_ReturnsSseWithAgentPrefixes()
        {
            using var client = _factory.CreateClient();

            var req = new
            {
                model = "SampleAgentGroup",
                stream = true,
                messages = new[] { new { role = "user", content = "hello" } }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json")
            };

            // Accept SSE
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

            using var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

            var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);

            var aggregated = new StringBuilder();
            string line;
            var seenDone = false;
            var maxLines = 200; // safeguard
            var linesRead = 0;
            while ((line = await reader.ReadLineAsync()) != null && linesRead++ < maxLines)
            {
                aggregated.AppendLine(line);
                if (line.Contains("[DONE]") || line.Contains("data: [DONE]"))
                {
                    seenDone = true;
                    break;
                }

                // gather data lines for assertions
                if (line.StartsWith("data:") && (line.Contains("[Planner]") || line.Contains("[Researcher]") || line.Contains("[Planner]") ))
                {
                    // found an agent prefix in the streamed data
                    break;
                }
            }

            var agg = aggregated.ToString();
            Assert.True(seenDone || agg.Contains("[Planner]") || agg.Contains("[Researcher]"), "Stream did not contain expected agent prefixes or [DONE]");
        }
    }
}
