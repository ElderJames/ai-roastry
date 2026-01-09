#if false
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using LY.LlmPool.Web.Data;

namespace LY.LlmPool.DataMigrations.Sqlite;

/// <summary>
/// Design-time factory so migrations can be scaffolded into the DataMigrations.Sqlite assembly.
/// Disabled temporarily because Program.cs already provides a factory and duplicate discovery breaks 'dotnet ef'.
/// </summary>
public class LlmDbContextDesignTimeFactory : IDesignTimeDbContextFactory<LlmDbContext>
{
    public LlmDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LlmDbContext>();

        // Use a local sqlite file for design-time; important: specify MigrationsAssembly to this project
        optionsBuilder.UseSqlite("Data Source=__design_time_migrations.db", b =>
        {
            b.MigrationsAssembly(typeof(LlmDbContextDesignTimeFactory).Assembly.GetName().Name);
        });

        return new LlmDbContext(optionsBuilder.Options);
    }
}
#endif
