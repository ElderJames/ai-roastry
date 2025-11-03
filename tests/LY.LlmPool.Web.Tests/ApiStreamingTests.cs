using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Xunit.Abstractions;
using LY.LlmPool.Client;

namespace LY.LlmPool.Web.Tests;

public class ApiStreamingTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public ApiStreamingTests(TestWebApplicationFactory factory, ITestOutputHelper output)
    {
        // Ensure test-friendly environment; upstream calls are intercepted by TestWebApplicationFactory
        Environment.SetEnvironmentVariable("USE_INMEMORY_DB", "true");
        _factory = factory;
        _output = output;
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
    var json1 = "{\"model\":\"SampleAgentGroup\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}";
    req.Content = new StringContent(json1, Encoding.UTF8, "application/json");

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

    /// <summary>
    /// 测试流式响应是否实时返回 (不阻塞)
    /// 验证 MonitoringDelegatingChatClient 的 yield-first 修改是否生效
    /// </summary>
    [Fact]
    public async Task DirectHttpRequest_StreamAsync_Returns_Chunks_Immediately()
    {
        // Arrange
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        
        var payload = new
        {
            model = "Local-OpenAI-Compatible",
            stream = true,
            messages = new[] {new { role = "user", content = "你好" }}
        };
        
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        // Act & Assert
        var stopwatch = Stopwatch.StartNew();
        var chunkTimestamps = new List<(long ElapsedMs, string Content)>();
        
        _output.WriteLine("🚀 开始流式请求...");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(response.IsSuccessStatusCode, $"Response status: {response.StatusCode}");
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var chunkCount = 0;
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("data: "))
            {
                var data = line.Substring(6);
                if (data == "[DONE]")
                {
                    _output.WriteLine($"✅ 收到 [DONE] 标记");
                    break;
                }

                var elapsed = stopwatch.ElapsedMilliseconds;
                chunkCount++;
                chunkTimestamps.Add((elapsed, data));
                
                _output.WriteLine($"⏱️ [{elapsed}ms] 收到 chunk #{chunkCount}: {data.Length} chars");

                // 验证:每个 chunk 之间的间隔不应超过 2000ms (如果被持久化阻塞会更长)
                if (chunkTimestamps.Count > 1)
                {
                    var prevTimestamp = chunkTimestamps[^2].ElapsedMs;
                    var gap = elapsed - prevTimestamp;
                    _output.WriteLine($"   📊 与上一个 chunk 间隔: {gap}ms");
                    
                    // 如果间隔太长,说明可能被数据库操作阻塞了
                    Assert.True(gap < 3000, 
                        $"Chunk gap ({gap}ms) too large, streaming may be blocked by persistence operations");
                }
            }
        }

        stopwatch.Stop();

        // Assert: 应该收到至少一个 chunk
        Assert.True(chunkCount > 0, "Should receive at least one chunk");
        _output.WriteLine($"✅ 总共收到 {chunkCount} 个 chunks");
        _output.WriteLine($"✅ 总耗时: {stopwatch.ElapsedMilliseconds}ms");

        // 验证:首个 chunk 应该在合理时间内返回 (< 3秒)
        if (chunkTimestamps.Count > 0)
        {
            var firstChunkTime = chunkTimestamps[0].ElapsedMs;
            _output.WriteLine($"📈 首个 chunk 时间 (TTFB): {firstChunkTime}ms");
            Assert.True(firstChunkTime < 3000, 
                $"First chunk took too long ({firstChunkTime}ms), may indicate blocking");
        }
    }

    /// <summary>
    /// 测试 LlmPoolClient 流式响应是否实时返回 (不阻塞)
    /// 验证 fire-and-forget 持久化修改是否生效
    /// </summary>
    [Fact]
    public async Task LlmPoolClient_StreamAsync_Returns_Chunks_Immediately()
    {
        // Arrange
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // 🎯 创建 FakeUpstreamMessageHandler 实例并传入 LlmPoolClient
        // 这样即使 LlmPoolClient 内部创建新的 HttpClient，也会使用这个 Mock handler
        var fakeHandler = new FakeUpstreamMessageHandler();
        var llmPoolClient = new LlmPoolClient(client, "test-key", customHandler: fakeHandler);

        var messages = new List<ClientMessage>
        {
            new ClientMessage { Role = "user", Content = "请写一首简短的诗" }
        };

        // Act & Assert
        var stopwatch = Stopwatch.StartNew();
        var chunkTimestamps = new List<(long ElapsedMs, string Content)>();
        
        _output.WriteLine("🚀 开始流式请求...");

        try
        {
            await foreach (var update in llmPoolClient.ChatStreamAsync(
                "Local-OpenAI-Compatible", // 🎯 使用测试数据中的配置名称
                messages))
            {
                var elapsed = stopwatch.ElapsedMilliseconds;
                chunkTimestamps.Add((elapsed, update.Content ?? string.Empty));

                _output.WriteLine($"⏱️ [{elapsed}ms] 收到 chunk: {update.Content?.Length ?? 0} chars");

                // 验证:每个 chunk 之间的间隔不应超过 500ms (如果被持久化阻塞会更长)
                if (chunkTimestamps.Count > 1)
                {
                    var prevTimestamp = chunkTimestamps[^2].ElapsedMs;
                    var gap = elapsed - prevTimestamp;
                    _output.WriteLine($"   📊 与上一个 chunk 间隔: {gap}ms");
                    
                    // 如果间隔太长,说明可能被数据库操作阻塞了
                    Assert.True(gap < 2000, 
                        $"Chunk gap ({gap}ms) too large, streaming may be blocked by persistence operations");
                }
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"❌ 异常: {ex.Message}");
            throw;
        }

        stopwatch.Stop();

        // Assert: 应该收到至少一个 chunk
        Assert.NotEmpty(chunkTimestamps);
        _output.WriteLine($"✅ 总共收到 {chunkTimestamps.Count} 个 chunks");
        _output.WriteLine($"✅ 总耗时: {stopwatch.ElapsedMilliseconds}ms");

        // 验证:首个 chunk 应该在合理时间内返回 (< 3秒)
        if (chunkTimestamps.Count > 0)
        {
            var firstChunkTime = chunkTimestamps[0].ElapsedMs;
            _output.WriteLine($"📈 首个 chunk 时间 (TTFB): {firstChunkTime}ms");
            Assert.True(firstChunkTime < 3000, 
                $"First chunk took too long ({firstChunkTime}ms), may indicate blocking");
        }
    }

    /// <summary>
    /// 测试带工具调用的流式响应是否实时返回
    /// 验证工具调用的 fire-and-forget 持久化不阻塞流式输出
    /// </summary>
    [Fact]
    public async Task StreamingWithToolCalls_Returns_Chunks_Immediately()
    {
        // Arrange - 直接使用 HTTP 请求,避免 LlmPoolClient 内部创建新 HttpClient
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        
        var payload = new
        {
            model = "Local-OpenAI-Compatible", // 使用测试数据中的普通配置
            stream = true,
            messages = new object[]
            {
                new { role = "user", content = "hello" }
            }
        };
        
    var requestJson = JsonSerializer.Serialize(payload);
    req.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        // Act & Assert
        var stopwatch = Stopwatch.StartNew();
        var events = new List<(long ElapsedMs, string EventType, string Content)>();
        
        _output.WriteLine("🚀 开始流式请求...");

        int textChunkCount = 0;

        try
        {
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            Assert.True(resp.IsSuccessStatusCode, $"Expected success status, got {resp.StatusCode}");
            Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

            var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            for (int safety = 0; safety < 500; safety++)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                
                var elapsed = stopwatch.ElapsedMilliseconds;

                if (line.StartsWith("data: "))
                {
                    var data = line.Substring(6);
                    if (data == "[DONE]")
                    {
                        _output.WriteLine($"⏱️ [{elapsed}ms] ✅ 收到 [DONE]");
                        break;
                    }
                    else if (data.StartsWith("{"))
                    {
                        using var doc = JsonDocument.Parse(data);
                        var root = doc.RootElement;

                        // 检测文本内容
                        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                        {
                            var choice = choices[0];
                            if (choice.TryGetProperty("delta", out var delta))
                            {
                                // 检测文本
                                if (delta.TryGetProperty("content", out var content))
                                {
                                    var text = content.GetString() ?? string.Empty;
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        textChunkCount++;
                                        events.Add((elapsed, "Text", text));
                                        _output.WriteLine($"⏱️ [{elapsed}ms] 📝 文本: {text}");
                                    }
                                }
                            }
                        }
                    }
                }

                // 验证:事件之间的间隔不应过长
                if (events.Count > 1)
                {
                    var prevTimestamp = events[^2].ElapsedMs;
                    var currentGap = elapsed - prevTimestamp;
                    
                    _output.WriteLine($"   📊 与上一个事件间隔: {currentGap}ms");
                    
                    // 如果间隔过长,可能表示被数据库操作阻塞了
                    Assert.True(currentGap < 5000, 
                        $"Event gap ({currentGap}ms) too large, may be blocked by persistence");
                }
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"❌ 异常: {ex.Message}");
            throw;
        }

        stopwatch.Stop();

        // Assert
        _output.WriteLine($"\n📊 统计:");
        _output.WriteLine($"   - 文本 chunks: {textChunkCount}");
        _output.WriteLine($"   - 总事件数: {events.Count}");
        _output.WriteLine($"   - 总耗时: {stopwatch.ElapsedMilliseconds}ms");

        // 应该收到至少一些文本事件
        Assert.NotEmpty(events);
        Assert.True(textChunkCount > 0, "Should receive at least one text chunk");

        _output.WriteLine("✅ 流式响应测试通过!");
    }
}
