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
    public class McpSdkDiscoveryServiceTests
    {
        private static LlmDbContext CreateDb()
        {
            var opts = new DbContextOptionsBuilder<LlmDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new LlmDbContext(opts);
        }

        private class ThrowingFactory : IMcpClientFactory
        {
            public Task<IMcpClient> CreateAsync(HttpClient client, CancellationToken ct = default)
            {
                throw new InvalidOperationException("factory fail");
            }
        }

        private class PartialThrowingClient : IMcpClient
        {
            public ValueTask DisposeAsync() => new ValueTask(Task.CompletedTask);

            public async IAsyncEnumerable<ToolDescriptor> EnumerateToolsAsync(JsonSerializerOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return new ToolDescriptor("ok1", "OK1", "d1", "{\"x\":1}");
                await Task.Yield();
                throw new Exception("boom during enumeration");
            }

            public Task<List<PromptDescriptor>> ListPromptsAsync(CancellationToken ct = default) => Task.FromResult(new List<PromptDescriptor>());
        }

        private class PromptsThrowingClient : IMcpClient
        {
            private readonly IEnumerable<ToolDescriptor> _tools;
            public PromptsThrowingClient(IEnumerable<ToolDescriptor> tools) { _tools = tools; }
            public ValueTask DisposeAsync() => new ValueTask(Task.CompletedTask);
            public async IAsyncEnumerable<ToolDescriptor> EnumerateToolsAsync(JsonSerializerOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                foreach (var t in _tools) { yield return t; await Task.Yield(); }
            }
            public Task<List<PromptDescriptor>> ListPromptsAsync(CancellationToken ct = default) => throw new Exception("prompts failed");
        }

        private class SimpleFactory : IMcpClientFactory
        {
            private readonly IMcpClient _client;
            public SimpleFactory(IMcpClient client) { _client = client; }
            public Task<IMcpClient> CreateAsync(HttpClient client, CancellationToken ct = default) => Task.FromResult(_client);
        }

        [Fact]
        public async Task FetchAndCacheSchemaAsync_WhenFactoryThrows_ThrowsInvalidOperation()
        {
            await using var db = CreateDb();
            var cfg = new McpServerConfig { Name = "mft", Url = "http://localhost", Description = "test" };
            db.McpServerConfigs.Add(cfg);
            await db.SaveChangesAsync();

            var svc = new McpServerConfigService(db, NullLogger<McpServerConfigService>.Instance, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, null, new ThrowingFactory());

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.FetchAndCacheSchemaAsync(cfg.Id));
        }

        [Fact]
        public async Task FetchAndCacheSchemaAsync_WhenEnumerationThrows_StillWritesCollectedTools()
        {
            await using var db = CreateDb();
            var cfg = new McpServerConfig { Name = "partial", Url = "http://localhost", Description = "test" };
            db.McpServerConfigs.Add(cfg);
            await db.SaveChangesAsync();

            var client = new PartialThrowingClient();
            var svc = new McpServerConfigService(db, NullLogger<McpServerConfigService>.Instance, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, null, new SimpleFactory(client));

            var (tools, prompts) = await svc.FetchAndCacheSchemaAsync(cfg.Id);
            Assert.Equal(1, tools);
            Assert.Equal(0, prompts);

            var reloaded = await db.McpServerConfigs.FirstAsync();
            using var doc = JsonDocument.Parse(reloaded.SchemaCacheJson!);
            Assert.True(doc.RootElement.TryGetProperty("tools", out var toolsElem));
            Assert.Equal(1, toolsElem.GetArrayLength());
        }

        [Fact]
        public async Task FetchAndCacheSchemaAsync_WhenPromptsThrows_WritesToolsAndZeroPrompts()
        {
            await using var db = CreateDb();
            var cfg = new McpServerConfig { Name = "pt", Url = "http://localhost", Description = "test" };
            db.McpServerConfigs.Add(cfg);
            await db.SaveChangesAsync();

            var tools = new[] { new ToolDescriptor("t1", "T1", null, "{}"), new ToolDescriptor("t2", "T2", null, "{}") };
            var client = new PromptsThrowingClient(tools);
            var svc = new McpServerConfigService(db, NullLogger<McpServerConfigService>.Instance, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, null, new SimpleFactory(client));

            var (toolsCount, promptsCount) = await svc.FetchAndCacheSchemaAsync(cfg.Id);
            Assert.Equal(2, toolsCount);
            Assert.Equal(0, promptsCount);

            var reloaded = await db.McpServerConfigs.FirstAsync();
            using var doc = JsonDocument.Parse(reloaded.SchemaCacheJson!);
            Assert.True(doc.RootElement.TryGetProperty("tools", out var toolsElem));
            Assert.Equal(2, toolsElem.GetArrayLength());
            Assert.True(doc.RootElement.TryGetProperty("prompts", out var promptsElem));
            Assert.Equal(0, promptsElem.GetArrayLength());
        }
    }
}
