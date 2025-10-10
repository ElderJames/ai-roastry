using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LY.LlmPool.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdditionalParametersToLlmConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxTokens",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "Temperature",
                table: "Configs");

            migrationBuilder.AddColumn<string>(
                name: "model_parameters",
                table: "llm_prompts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model_parameters",
                table: "llm_prompt_history",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_parameters",
                table: "Configs",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "model_parameters",
                table: "llm_prompts");

            migrationBuilder.DropColumn(
                name: "model_parameters",
                table: "llm_prompt_history");

            migrationBuilder.DropColumn(
                name: "additional_parameters",
                table: "Configs");

            migrationBuilder.AddColumn<int>(
                name: "MaxTokens",
                table: "Configs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<float>(
                name: "Temperature",
                table: "Configs",
                type: "real",
                nullable: false,
                defaultValue: 0f);
        }
    }
}
