using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LY.LlmPool.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "llm_prompts",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    update_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_prompts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "llm_prompt_history",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    prompt_id = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    create_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_prompt_history", x => x.id);
                    table.ForeignKey(
                        name: "FK_llm_prompt_history_llm_prompts_prompt_id",
                        column: x => x.prompt_id,
                        principalTable: "llm_prompts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_llm_prompt_history_create_time",
                table: "llm_prompt_history",
                column: "create_time");

            migrationBuilder.CreateIndex(
                name: "IX_llm_prompt_history_prompt_id",
                table: "llm_prompt_history",
                column: "prompt_id");

            migrationBuilder.CreateIndex(
                name: "IX_llm_prompts_name",
                table: "llm_prompts",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "llm_prompt_history");

            migrationBuilder.DropTable(
                name: "llm_prompts");
        }
    }
}
