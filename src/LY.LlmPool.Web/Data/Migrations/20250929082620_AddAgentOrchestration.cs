using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LY.LlmPool.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentOrchestration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_llm_apps_Configs_llm_config_id",
                table: "llm_apps");

            migrationBuilder.DropForeignKey(
                name: "FK_llm_apps_Endpoints_endpoint_id",
                table: "llm_apps");

            migrationBuilder.DropForeignKey(
                name: "FK_llm_apps_llm_prompts_prompt_id",
                table: "llm_apps");

            migrationBuilder.DropIndex(
                name: "IX_llm_apps_prompt_id",
                table: "llm_apps");

            migrationBuilder.RenameColumn(
                name: "prompt_id",
                table: "llm_apps",
                newName: "orchestration_mode");

            migrationBuilder.AlterColumn<string>(
                name: "app_type",
                table: "llm_apps",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<string>(
                name: "llm_prompt_id",
                table: "llm_apps",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentMembers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ConfigJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LlmAppId = table.Column<string>(type: "text", nullable: false),
                    LlmPromptId = table.Column<string>(type: "text", nullable: true),
                    LlmConfigId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentMembers_Configs_LlmConfigId",
                        column: x => x.LlmConfigId,
                        principalTable: "Configs",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AgentMembers_llm_apps_LlmAppId",
                        column: x => x.LlmAppId,
                        principalTable: "llm_apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentMembers_llm_prompts_LlmPromptId",
                        column: x => x.LlmPromptId,
                        principalTable: "llm_prompts",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "McpServerConfigs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Command = table.Column<string>(type: "text", nullable: true),
                    Args = table.Column<string>(type: "text", nullable: true),
                    Env = table.Column<string>(type: "text", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ConfigJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SchemaCacheJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpServerConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentTools",
                columns: table => new
                {
                    AgentMemberId = table.Column<string>(type: "text", nullable: false),
                    ToolId = table.Column<string>(type: "text", nullable: false),
                    ToolType = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTools", x => new { x.AgentMemberId, x.ToolId, x.ToolType });
                    table.ForeignKey(
                        name: "FK_AgentTools_AgentMembers_AgentMemberId",
                        column: x => x.AgentMemberId,
                        principalTable: "AgentMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_llm_apps_llm_prompt_id",
                table: "llm_apps",
                column: "llm_prompt_id");

            migrationBuilder.CreateIndex(
                name: "IX_AgentMembers_LlmAppId",
                table: "AgentMembers",
                column: "LlmAppId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentMembers_LlmConfigId",
                table: "AgentMembers",
                column: "LlmConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentMembers_LlmPromptId",
                table: "AgentMembers",
                column: "LlmPromptId");

            migrationBuilder.CreateIndex(
                name: "IX_McpServerConfigs_Name",
                table: "McpServerConfigs",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_llm_apps_Configs_llm_config_id",
                table: "llm_apps",
                column: "llm_config_id",
                principalTable: "Configs",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_llm_apps_Endpoints_endpoint_id",
                table: "llm_apps",
                column: "endpoint_id",
                principalTable: "Endpoints",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_llm_apps_llm_prompts_llm_prompt_id",
                table: "llm_apps",
                column: "llm_prompt_id",
                principalTable: "llm_prompts",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_llm_apps_Configs_llm_config_id",
                table: "llm_apps");

            migrationBuilder.DropForeignKey(
                name: "FK_llm_apps_Endpoints_endpoint_id",
                table: "llm_apps");

            migrationBuilder.DropForeignKey(
                name: "FK_llm_apps_llm_prompts_llm_prompt_id",
                table: "llm_apps");

            migrationBuilder.DropTable(
                name: "AgentTools");

            migrationBuilder.DropTable(
                name: "McpServerConfigs");

            migrationBuilder.DropTable(
                name: "AgentMembers");

            migrationBuilder.DropIndex(
                name: "IX_llm_apps_llm_prompt_id",
                table: "llm_apps");

            migrationBuilder.DropColumn(
                name: "llm_prompt_id",
                table: "llm_apps");

            migrationBuilder.RenameColumn(
                name: "orchestration_mode",
                table: "llm_apps",
                newName: "prompt_id");

            migrationBuilder.AlterColumn<string>(
                name: "app_type",
                table: "llm_apps",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateIndex(
                name: "IX_llm_apps_prompt_id",
                table: "llm_apps",
                column: "prompt_id");

            migrationBuilder.AddForeignKey(
                name: "FK_llm_apps_Configs_llm_config_id",
                table: "llm_apps",
                column: "llm_config_id",
                principalTable: "Configs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_llm_apps_Endpoints_endpoint_id",
                table: "llm_apps",
                column: "endpoint_id",
                principalTable: "Endpoints",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_llm_apps_llm_prompts_prompt_id",
                table: "llm_apps",
                column: "prompt_id",
                principalTable: "llm_prompts",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
