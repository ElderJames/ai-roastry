using System;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using System.Net.Http;

namespace LY.LlmPool.Web.Tests;

public class McpServerConfigServiceTests
{
    private class DummyHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name = "") => new System.Net.Http.HttpClient();
    }

    private static LlmDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new LlmDbContext(options);
    }

    [Fact]
    public async Task Can_CRUD_McpServerConfig()
    {
    using var db = CreateDb();
    var svc = new McpServerConfigService(db, NullLogger<McpServerConfigService>.Instance, new DummyHttpClientFactory());

        // Create
        var created = await svc.CreateAsync(new McpServerConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "fileserver",
            Url = "http://localhost:8000",
            Description = "local mcp server",
            SchemaCacheJson = "{\"version\":1}"
        });

        Assert.NotNull(created.Id);

        // Get
        var fetched = await svc.GetAsync(created.Id);
        Assert.NotNull(fetched);
        Assert.Equal("fileserver", fetched!.Name);

        // List
        var list = await svc.ListAsync("file");
        Assert.Single(list);

        // Update
        created.Description = "updated description";
        var updated = await svc.UpdateAsync(created);
        Assert.True(updated);
        var fetched2 = await svc.GetAsync(created.Id);
        Assert.Equal("updated description", fetched2!.Description);

        // Delete
        var deleted = await svc.DeleteAsync(created.Id);
        Assert.True(deleted);
        var fetched3 = await svc.GetAsync(created.Id);
        Assert.Null(fetched3);
    }
}
