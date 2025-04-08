param(
    [Parameter(Mandatory=$true)]
    [string]$MigrationName
)

$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $scriptPath "..\src\LY.LlmPool.Web"

Write-Host "Adding migration '$MigrationName' to project at $projectPath"

# Ensure we're in the project directory
Push-Location $projectPath

try {
    # Install EF Core tools if not already installed
    $efToolsInstalled = dotnet tool list --global | Select-String "dotnet-ef"
    if (-not $efToolsInstalled) {
        Write-Host "Installing EF Core tools..."
        dotnet tool install --global dotnet-ef
    }

    # Add the migration
    Write-Host "Adding migration..."
    dotnet ef migrations add $MigrationName --context LlmDbContext --output-dir Data/Migrations

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Migration '$MigrationName' added successfully!" -ForegroundColor Green
    } else {
        Write-Host "Failed to add migration." -ForegroundColor Red
    }
} finally {
    # Return to original directory
    Pop-Location
} 