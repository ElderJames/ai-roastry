using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#nullable enable

namespace LY.LlmPool.Web.Tests
{
    public class McpSdkDiscoveryTests
    {
        private static LlmDbContext CreateDb()
        {
            var opts = new DbContextOptionsBuilder<LlmDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new LlmDbContext(opts);
        }

        private class FakeMcpClient : IMcpClient
        {
            private readonly IEnumerable<ToolDescriptor> _tools;
            private readonly List<PromptDescriptor> _prompts;

            public FakeMcpClient(IEnumerable<ToolDescriptor> tools, IEnumerable<PromptDescriptor> prompts)
            {
                _tools = tools;
                _prompts = prompts.ToList();
            }

            public ValueTask DisposeAsync() => new ValueTask(Task.CompletedTask);

            public async IAsyncEnumerable<ToolDescriptor> EnumerateToolsAsync(JsonSerializerOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                foreach (var t in _tools) { yield return t; await Task.Yield(); }
            }

            public Task<List<PromptDescriptor>> ListPromptsAsync(CancellationToken ct = default) => Task.FromResult(_prompts);
        }

        private class FakeFactory : IMcpClientFactory
        {
            private readonly IMcpClient _client;
            public FakeFactory(IMcpClient client) { _client = client; }
            public Task<IMcpClient> CreateAsync(HttpClient client, CancellationToken ct = default) => Task.FromResult(_client);
        }

        [Fact]
        public async Task FetchAndCacheSchemaAsync_UsesSdkFactory_WriteSchemaJson()
        {
            await using var db = CreateDb();
            var cfg = new McpServerConfig { Name = "m1", Url = "http://localhost", Description = "test" };
            db.McpServerConfigs.Add(cfg);
            await db.SaveChangesAsync();

            var tools = new[] { new ToolDescriptor("t1", "T1", "d1", "{\"type\":\"object\"}" ) };
            var prompts = new[] { new PromptDescriptor("p1", "P1", "pd1") };
            var fakeClient = new FakeMcpClient(tools, prompts);
            var factory = new FakeFactory(fakeClient);

            var svc = new McpServerConfigService(db, NullLogger<McpServerConfigService>.Instance, NullLoggerFactory.Instance, null, factory);

            var (tcount, pcount) = await svc.FetchAndCacheSchemaAsync(cfg.Id);

            Assert.Equal(1, tcount);
            Assert.Equal(1, pcount);

            var reloaded = await db.McpServerConfigs.FirstAsync();
            Assert.False(string.IsNullOrWhiteSpace(reloaded.SchemaCacheJson));
            using var doc = JsonDocument.Parse(reloaded.SchemaCacheJson!);
            Assert.True(doc.RootElement.TryGetProperty("tools", out var toolsElem));
            Assert.Equal(1, toolsElem.GetArrayLength());
            Assert.True(doc.RootElement.TryGetProperty("prompts", out var promptsElem));
            Assert.Equal(1, promptsElem.GetArrayLength());
        }
    }
}
