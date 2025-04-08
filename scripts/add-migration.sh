#!/bin/bash

# Check if migration name is provided
if [ -z "$1" ]; then
    echo "Error: Migration name is required"
    echo "Usage: ./add-migration.sh <migration-name>"
    exit 1
fi

MIGRATION_NAME=$1
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/../src/LY.LlmPool.Web"

echo "Adding migration '$MIGRATION_NAME' to project at $PROJECT_DIR"

# Ensure we're in the project directory
cd "$PROJECT_DIR" || exit 1

# Install EF Core tools if not already installed
if ! dotnet tool list --global | grep "dotnet-ef" > /dev/null; then
    echo "Installing EF Core tools..."
    dotnet tool install --global dotnet-ef
fi

# Add required packages
echo "Ensuring required packages are installed..."
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL

# Add the migration
echo "Adding migration..."
dotnet ef migrations add "$MIGRATION_NAME" --context LlmDbContext --output-dir Data/Migrations

if [ $? -eq 0 ]; then
    echo -e "\e[32mMigration '$MIGRATION_NAME' added successfully!\e[0m"
else
    echo -e "\e[31mFailed to add migration.\e[0m"
    exit 1
fi 