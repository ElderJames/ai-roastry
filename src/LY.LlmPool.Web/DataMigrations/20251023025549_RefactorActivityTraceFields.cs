using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LY.LlmPool.Web.DataMigrations
{
    /// <inheritdoc />
    public partial class RefactorActivityTraceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppName",
                table: "ActivityTraces");

            migrationBuilder.DropColumn(
                name: "IsAppTool",
                table: "ActivityTraces");

            migrationBuilder.DropColumn(
                name: "IsLlmPoolServer",
                table: "ActivityTraces");

            migrationBuilder.DropColumn(
                name: "IsMcpServer",
                table: "ActivityTraces");

            migrationBuilder.DropColumn(
                name: "IsMcpTool",
                table: "ActivityTraces");

            migrationBuilder.DropColumn(
                name: "ToolArguments",
                table: "ActivityTraces");

            migrationBuilder.DropColumn(
                name: "ToolResult",
                table: "ActivityTraces");

            migrationBuilder.RenameColumn(
                name: "ToolName",
                table: "ActivityTraces",
                newName: "Name");

            migrationBuilder.AddColumn<int>(
                name: "ServerType",
                table: "ActivityTraces",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ToolDataJson",
                table: "ActivityTraces",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ToolType",
                table: "ActivityTraces",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ServerType",
                table: "ActivityTraces");

            migrationBuilder.DropColumn(
                name: "ToolDataJson",
                table: "ActivityTraces");

            migrationBuilder.DropColumn(
                name: "ToolType",
                table: "ActivityTraces");

            migrationBuilder.RenameColumn(
                name: "Name",
                table: "ActivityTraces",
                newName: "ToolName");

            migrationBuilder.AddColumn<string>(
                name: "AppName",
                table: "ActivityTraces",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAppTool",
                table: "ActivityTraces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsLlmPoolServer",
                table: "ActivityTraces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsMcpServer",
                table: "ActivityTraces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsMcpTool",
                table: "ActivityTraces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ToolArguments",
                table: "ActivityTraces",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToolResult",
                table: "ActivityTraces",
                type: "text",
                nullable: true);
        }
    }
}
