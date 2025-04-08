using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LY.LlmPool.Web.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LlmDbContext>
{
    public LlmDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LlmDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=llmpool;Username=postgres;Password=postgres");

        return new LlmDbContext(optionsBuilder.Options);
    }
} 