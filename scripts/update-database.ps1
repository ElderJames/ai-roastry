param(
    [string]$TargetMigration
)

$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $scriptPath "..\src\LY.LlmPool.Web"

Write-Host "Updating database for project at $projectPath"
if ($TargetMigration) {
    Write-Host "Target migration: $TargetMigration"
}

# Ensure we're in the project directory
Push-Location $projectPath

try {
    # Install EF Core tools if not already installed
    $efToolsInstalled = dotnet tool list --global | Select-String "dotnet-ef"
    if (-not $efToolsInstalled) {
        Write-Host "Installing EF Core tools..."
        dotnet tool install --global dotnet-ef
    }

    # Update database
    Write-Host "Updating database..."
    if ($TargetMigration) {
        dotnet ef database update $TargetMigration --context LlmDbContext
    } else {
        dotnet ef database update --context LlmDbContext
    }

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Database updated successfully!" -ForegroundColor Green
    } else {
        Write-Host "Failed to update database." -ForegroundColor Red
    }
} finally {
    # Return to original directory
    Pop-Location
} 