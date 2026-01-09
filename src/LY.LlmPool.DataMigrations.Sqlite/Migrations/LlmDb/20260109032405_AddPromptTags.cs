using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LY.LlmPool.DataMigrations.Sqlite.Migrations.LlmDb
{
    /// <inheritdoc />
    public partial class AddPromptTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "llm_prompts",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "tags",
                table: "llm_prompts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "status",
                table: "llm_prompts");

            migrationBuilder.DropColumn(
                name: "tags",
                table: "llm_prompts");
        }
    }
}
