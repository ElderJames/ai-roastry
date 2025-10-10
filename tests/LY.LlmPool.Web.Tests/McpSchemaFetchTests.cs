using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LY.LlmPool.Web.Tests;
#nullable enable

public class McpSchemaFetchTests
{
    private class MapHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public MapHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    private class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) { _client = client; }
        public HttpClient CreateClient(string name = "") => _client;
    }

    private static LlmDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<LlmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new LlmDbContext(opts);
    }

    [Fact]
    public async Task Fetches_schema_and_updates_cache_with_counts()
    {
        var json = "{" + "\"tools\":[{},{},{}],\"prompts\":[{}]" + "}"; // 3 tools, 1 prompt
        var handler = new MapHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/proto"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (req.RequestUri!.AbsolutePath.EndsWith("/schema") || req.RequestUri!.AbsolutePath.EndsWith("/.well-known/mcp/schema"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost")
        };
        var factory = new SingleClientFactory(http);

    // Create a fake IMcpClientFactory to avoid invoking the real SDK in tests.
    var fakeMcpFactory = new TestMcpFactory(3, 1);

    await using var db = CreateDb();
    var svc = new McpServerConfigService(db, NullLogger<McpServerConfigService>.Instance, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, factory, fakeMcpFactory);
        var cfg = new McpServerConfig { Name = "m1", Url = "http://localhost", Description = "test" };
        db.McpServerConfigs.Add(cfg);
        await db.SaveChangesAsync();

        var (tools, prompts) = await svc.FetchAndCacheSchemaAsync(cfg.Id);

        Assert.Equal(3, tools);
        Assert.Equal(1, prompts);

        var reloaded = await db.McpServerConfigs.FirstAsync();
        Assert.False(string.IsNullOrWhiteSpace(reloaded.SchemaCacheJson));
        using var doc = JsonDocument.Parse(reloaded.SchemaCacheJson!);
        Assert.True(doc.RootElement.TryGetProperty("tools", out var toolsElem));
        Assert.Equal(3, toolsElem.GetArrayLength());
        Assert.True(doc.RootElement.TryGetProperty("prompts", out var promptsElem));
        Assert.Equal(1, promptsElem.GetArrayLength());
    }

    private class TestMcpFactory : IMcpClientFactory
    {
        private readonly int _toolsCount;
        private readonly int _promptsCount;
        public TestMcpFactory(int toolsCount, int promptsCount)
        {
            _toolsCount = toolsCount;
            _promptsCount = promptsCount;
        }

        public Task<IMcpClient> CreateAsync(HttpClient client, CancellationToken ct = default)
        {
            var tools = Enumerable.Range(0, _toolsCount).Select(i => new ToolDescriptor($"t{i}", null, null, null));
            var prompts = Enumerable.Range(0, _promptsCount).Select(i => new PromptDescriptor($"p{i}", null, null)).ToList();
            IMcpClient c = new TestMcpClient(tools, prompts);
            return Task.FromResult(c);
        }

        private class TestMcpClient : IMcpClient
        {
            private readonly IEnumerable<ToolDescriptor> _tools;
            private readonly List<PromptDescriptor> _prompts;
            public TestMcpClient(IEnumerable<ToolDescriptor> tools, List<PromptDescriptor> prompts)
            {
                _tools = tools;
                _prompts = prompts;
            }

            public ValueTask DisposeAsync() => new ValueTask(Task.CompletedTask);

            public async IAsyncEnumerable<ToolDescriptor> EnumerateToolsAsync(JsonSerializerOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                foreach (var t in _tools) { yield return t; await Task.Yield(); }
            }

            public Task<List<PromptDescriptor>> ListPromptsAsync(CancellationToken ct = default) => Task.FromResult(_prompts);
        }
    }

    [Fact]
    public void ParseCounts_handles_invalid_json()
    {
        var (t, p) = McpServerConfigService.ParseCounts("not-json");
        Assert.Equal(0, t);
        Assert.Equal(0, p);
    }
}
