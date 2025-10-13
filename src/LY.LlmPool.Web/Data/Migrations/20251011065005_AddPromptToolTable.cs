using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LY.LlmPool.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptToolTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentTools");

            migrationBuilder.CreateTable(
                name: "prompt_tools",
                columns: table => new
                {
                    prompt_id = table.Column<string>(type: "text", nullable: false),
                    tool_id = table.Column<string>(type: "text", nullable: false),
                    tool_type = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prompt_tools", x => new { x.prompt_id, x.tool_id, x.tool_type });
                    table.ForeignKey(
                        name: "FK_prompt_tools_llm_prompts_prompt_id",
                        column: x => x.prompt_id,
                        principalTable: "llm_prompts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "prompt_tools");

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
        }
    }
}
