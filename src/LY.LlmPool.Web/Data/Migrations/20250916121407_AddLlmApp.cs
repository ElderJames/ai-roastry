using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LY.LlmPool.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmApp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "llm_apps",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    app_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    prompt_id = table.Column<string>(type: "text", nullable: true),
                    llm_config_id = table.Column<string>(type: "text", nullable: true),
                    endpoint_id = table.Column<string>(type: "text", nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    config_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_apps", x => x.id);
                    table.ForeignKey(
                        name: "FK_llm_apps_Configs_llm_config_id",
                        column: x => x.llm_config_id,
                        principalTable: "Configs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_llm_apps_Endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalTable: "Endpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_llm_apps_llm_prompts_prompt_id",
                        column: x => x.prompt_id,
                        principalTable: "llm_prompts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_llm_apps_endpoint_id",
                table: "llm_apps",
                column: "endpoint_id");

            migrationBuilder.CreateIndex(
                name: "IX_llm_apps_llm_config_id",
                table: "llm_apps",
                column: "llm_config_id");

            migrationBuilder.CreateIndex(
                name: "IX_llm_apps_name",
                table: "llm_apps",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_llm_apps_prompt_id",
                table: "llm_apps",
                column: "prompt_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "llm_apps");
        }
    }
}
