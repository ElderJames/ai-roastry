using LY.LlmPool.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("SQLite Migration Tool for LY.LlmPool");
        Console.WriteLine("Use 'dotnet ef migrations add <MigrationName>' to create migrations");
        Console.WriteLine("Use 'dotnet ef database update' to apply migrations");
    }
}

public class LlmDbContextFactory : IDesignTimeDbContextFactory<LlmDbContext>
{
    public LlmDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var connectionString = configuration.GetConnectionString("SqliteConnection") ?? "Data Source=llm-pool.db";

        var optionsBuilder = new DbContextOptionsBuilder<LlmDbContext>();
        optionsBuilder.UseSqlite(connectionString, b => b.MigrationsAssembly("LY.LlmPool.DataMigrations.Sqlite"));

        return new LlmDbContext(optionsBuilder.Options);
    }
}

public class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var connectionString = configuration.GetConnectionString("SqliteConnection") ?? "Data Source=llm-pool.db";

        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseSqlite(connectionString, b => b.MigrationsAssembly("LY.LlmPool.DataMigrations.Sqlite"));

        return new ApplicationDbContext(optionsBuilder.Options);
    }
}