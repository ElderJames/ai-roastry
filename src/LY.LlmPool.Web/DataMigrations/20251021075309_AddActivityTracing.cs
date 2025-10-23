using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LY.LlmPool.Web.DataMigrations
{
    /// <inheritdoc />
    public partial class AddActivityTracing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityTraces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TraceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SpanId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ParentSpanId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ConversationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OperationName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StatusDescription = table.Column<string>(type: "text", nullable: true),
                    ErrorType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ErrorStackTrace = table.Column<string>(type: "text", nullable: true),
                    OperationType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ModelId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResponseModelId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ProviderName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ResponseId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    FinishReason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Temperature = table.Column<float>(type: "real", nullable: true),
                    MaxTokens = table.Column<int>(type: "integer", nullable: true),
                    InputTokens = table.Column<int>(type: "integer", nullable: false),
                    OutputTokens = table.Column<int>(type: "integer", nullable: false),
                    ServerAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ServerPort = table.Column<int>(type: "integer", nullable: true),
                    IsAppCall = table.Column<bool>(type: "boolean", nullable: false),
                    AppName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsToolCall = table.Column<bool>(type: "boolean", nullable: false),
                    ToolName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ToolArguments = table.Column<string>(type: "text", nullable: true),
                    ToolResult = table.Column<string>(type: "text", nullable: true),
                    IsAppTool = table.Column<bool>(type: "boolean", nullable: false),
                    IsMcpTool = table.Column<bool>(type: "boolean", nullable: false),
                    IsLlmPoolServer = table.Column<bool>(type: "boolean", nullable: false),
                    IsMcpServer = table.Column<bool>(type: "boolean", nullable: false),
                    McpServerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    InputMessagesJson = table.Column<string>(type: "jsonb", nullable: true),
                    OutputContent = table.Column<string>(type: "text", nullable: true),
                    TagsJson = table.Column<string>(type: "jsonb", nullable: true),
                    EventsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityTraces", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityTraces_ActivityId",
                table: "ActivityTraces",
                column: "ActivityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityTraces_ConversationId",
                table: "ActivityTraces",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityTraces_ConversationId_StartTime",
                table: "ActivityTraces",
                columns: new[] { "ConversationId", "StartTime" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityTraces_StartTime",
                table: "ActivityTraces",
                column: "StartTime");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityTraces_TraceId",
                table: "ActivityTraces",
                column: "TraceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityTraces");
        }
    }
}
