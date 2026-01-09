param(
    [string]$TargetMigration,
    [string]$EfVersion = "9.0.5",
    [switch]$SkipSqlite,
    [switch]$SkipWeb
    ,[switch]$AddMigration
    ,[string]$MigrationName
)

$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path

function Ensure-EfTool([string]$version) {
    try {
        $current = & dotnet ef --version 2>&1
    } catch {
        $current = $null
    }

    if (-not $current -or -not $current.Contains($version)) {
        Write-Host "dotnet-ef $version is required. Installing/updating..."
        try {
            dotnet tool update --global dotnet-ef --version $version 2>$null
        } catch {
            dotnet tool install --global dotnet-ef --version $version
        }
        Write-Host "dotnet-ef $version installed/updated."
    } else {
        Write-Host "dotnet-ef $version already present.";
    }
}

function Update-Db([string]$projPath, [string]$startupPath, [string]$context) {
    Write-Host "Updating database for project: $projPath (startup: $startupPath)"
    Push-Location $projPath
    try {
        if ($TargetMigration) {
            dotnet ef database update $TargetMigration --context $context --startup-project $startupPath
        } else {
            dotnet ef database update --context $context --startup-project $startupPath
        }

        if ($LASTEXITCODE -eq 0) {
            Write-Host "Database updated for $projPath" -ForegroundColor Green
        } else {
            Write-Host "Database update FAILED for $projPath (exit code $LASTEXITCODE)" -ForegroundColor Red
            throw "dotnet ef returned exit code $LASTEXITCODE"
        }
    } finally {
        Pop-Location
    }
}

function Add-Migration([string]$projPath, [string]$startupPath, [string]$context, [string]$outputDir) {
    if (-not $MigrationName -and -not $TargetMigration) {
        throw "Migration name not provided. Use -MigrationName <Name> or -TargetMigration <Name>."
    }

    $name = if ($MigrationName) { $MigrationName } else { $TargetMigration }

    Write-Host "Adding migration '$name' to project: $projPath (output dir: $outputDir)"

    # quick check: does a migration with this name already exist in the output dir?
    $outFull = Join-Path $projPath $outputDir
    $exists = $false
    if (Test-Path $outFull) {
        $pattern = "*_${name}.cs"
        $match = Get-ChildItem -Path $outFull -Filter $pattern -File -ErrorAction SilentlyContinue
        if ($match) { $exists = $true }
    }

    if ($exists) {
        Write-Host "Migration '$name' already appears to exist in $outFull. Skipping add." -ForegroundColor Yellow
        return
    }

    Push-Location $projPath
    try {
        dotnet ef migrations add $name --output-dir $outputDir --context $context --startup-project $startupPath

        if ($LASTEXITCODE -eq 0) {
            Write-Host "Migration '$name' added for $projPath" -ForegroundColor Green
        } else {
            Write-Host "Migration add FAILED for $projPath (exit code $LASTEXITCODE)" -ForegroundColor Red
            throw "dotnet ef returned exit code $LASTEXITCODE"
        }
    } finally {
        Pop-Location
    }
}

Write-Host "Ensuring dotnet-ef version $EfVersion..."
Ensure-EfTool $EfVersion

if (-not $SkipSqlite) {
    Write-Host "\n--- Applying migrations in SQLite migrations project ---"
    $sqliteProj = Join-Path $scriptPath "..\src\LY.LlmPool.DataMigrations.Sqlite"
    # Ensure project directory exists
    if (Test-Path $sqliteProj) {
        # Use the Web startup project for applying migrations so the connection strings defined there are respected
        $sqliteStartup = Join-Path $scriptPath "..\src\LY.LlmPool.Web\LY.LlmPool.Web.csproj"

        if ($AddMigration) {
            Add-Migration -projPath $sqliteProj -startupPath $sqliteStartup -context "LlmDbContext" -outputDir "Migrations/LlmDb"
            Add-Migration -projPath $sqliteProj -startupPath $sqliteStartup -context "ApplicationDbContext" -outputDir "Migrations/ApplicationDb"
        }

        # Update migrations for LlmDb (using Web startup project's connection strings)
        Update-Db -projPath $sqliteProj -startupPath $sqliteStartup -context "LlmDbContext"

        # Update migrations for ApplicationDb (Identity) (using Web startup project's connection strings)
        Update-Db -projPath $sqliteProj -startupPath $sqliteStartup -context "ApplicationDbContext"
    } else {
        Write-Host "SQLite migrations project not found at $sqliteProj" -ForegroundColor Yellow
    }
}

if (-not $SkipWeb) {
    Write-Host "\n--- Applying migrations in Web project ---"
    $webProj = Join-Path $scriptPath "..\src\LY.LlmPool.Web"
    if (Test-Path $webProj) {
        if ($AddMigration) {
            Add-Migration -projPath $webProj -startupPath $webProj -context "LlmDbContext" -outputDir "Data/Migrations"
            Add-Migration -projPath $webProj -startupPath $webProj -context "ApplicationDbContext" -outputDir "Data/Migrations"
        }

        # Update migrations for LlmDb
        Update-Db -projPath $webProj -startupPath $webProj -context "LlmDbContext"

        # Update migrations for ApplicationDb (Identity)
        Update-Db -projPath $webProj -startupPath $webProj -context "ApplicationDbContext"
    } else {
        Write-Host "Web project not found at $webProj" -ForegroundColor Yellow
    }
}

Write-Host "All done." -ForegroundColor Cyan
