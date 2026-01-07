# SQLite Database Support

This project now supports both PostgreSQL and SQLite databases. You can switch between them using configuration.

## Configuration

Edit `appsettings.json` to set the database type:

```json
{
  "Database": {
    "Type": "SQLite"  // or "PostgreSQL"
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=db;Port=5432;Database=LlmPool;Username=user_pg;Password=password_zYHcAJ;",
    "SqliteConnection": "Data Source=llm-pool.db"
  }
}
```

## Creating SQLite Migrations

Use the `LY.LlmPool.DataMigrations.Sqlite` project to create and manage SQLite migrations:

```bash
# Navigate to the SQLite migrations project
cd src/LY.LlmPool.DataMigrations.Sqlite

# Create a new migration for LlmDbContext
dotnet ef migrations add MigrationName --context LlmDbContext --output-dir Migrations/LlmDb

# Create a new migration for ApplicationDbContext
dotnet ef migrations add MigrationName --context ApplicationDbContext --output-dir Migrations/ApplicationDb

# Apply migrations to the database
dotnet ef database update --context LlmDbContext
dotnet ef database update --context ApplicationDbContext
```

## Automatic Migration on Startup

When the application starts, it will automatically apply any pending migrations based on the configured database type. The migrations are applied in the following order:

1. LlmDbContext migrations
2. Seed initial data (model types)
3. ApplicationDbContext migrations (Identity tables)

## Database Files

- **SQLite**: Database file `llm-pool.db` will be created in the application root directory
- **PostgreSQL**: Uses the configured connection string

## Switching Between Databases

1. Update the `Database:Type` in `appsettings.json`
2. Ensure the appropriate connection string is configured
3. Restart the application (migrations will be applied automatically)

Note: When switching from one database type to another, you may need to manually migrate data if required.