using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LY.LlmPool.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateLlmPromptHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "based_on_version",
                table: "llm_prompt_history",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "test_configs",
                table: "llm_prompt_history",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "based_on_version",
                table: "llm_prompt_history");

            migrationBuilder.DropColumn(
                name: "test_configs",
                table: "llm_prompt_history");
        }
    }
}
