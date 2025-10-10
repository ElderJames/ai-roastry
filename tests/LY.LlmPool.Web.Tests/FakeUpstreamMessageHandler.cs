using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LY.LlmPool.Web.Tests;

/// <summary>
/// A delegating handler that simulates upstream responses during tests.
/// - If request body contains "stream":true: returns minimal SSE chunks and [DONE].
/// - Otherwise: returns minimal OpenAI chat completion JSON with a FINAL answer.
/// </summary>
public class FakeUpstreamMessageHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var wantsSse = false;
        
        // 检查请求体中是否有 stream: true
        if (request.Content != null)
        {
            // 读取内容但保留原始请求体
            var contentBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var content = Encoding.UTF8.GetString(contentBytes);
            
            if (!string.IsNullOrEmpty(content))
            {
                try
                {
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("stream", out var streamProp) && streamProp.GetBoolean())
                    {
                        wantsSse = true;
                    }
                }
                catch { /* ignore parse errors */ }
            }
            
            // 恢复请求内容以便后续处理
            request.Content = new ByteArrayContent(contentBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        if (wantsSse)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello from fake\"}}]}\n\n" +
                    "data: [DONE]\n\n")
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            resp.Headers.ConnectionClose = false;
            resp.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            return resp;
        }
        else
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var json = $"{{\"id\":\"chatcmpl-test\",\"_object\":\"chat.completion\",\"created\":{now},\"model\":\"fake-model\",\"choices\":[{{\"index\":0,\"message\":{{\"role\":\"assistant\",\"content\":\"FINAL: hello from fake\"}},\"finish_reason\":\"stop\"}}],\"usage\":{{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}}}";
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return resp;
        }
    }
}
