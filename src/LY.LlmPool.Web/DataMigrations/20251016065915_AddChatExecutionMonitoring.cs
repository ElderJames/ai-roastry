using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LY.LlmPool.Web.DataMigrations
{
    /// <inheritdoc />
    public partial class AddChatExecutionMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_execution_records",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    request_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    model_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    start_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    end_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    total_duration_ms = table.Column<double>(type: "double precision", nullable: true),
                    ttfb_ms = table.Column<double>(type: "double precision", nullable: true),
                    message_count = table.Column<int>(type: "integer", nullable: false),
                    tool_count = table.Column<int>(type: "integer", nullable: false),
                    tool_call_count = table.Column<int>(type: "integer", nullable: false),
                    chunk_count = table.Column<int>(type: "integer", nullable: false),
                    total_characters = table.Column<int>(type: "integer", nullable: false),
                    is_successful = table.Column<bool>(type: "boolean", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    summary = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_execution_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chat_execution_timeline_nodes",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    execution_record_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    node_type = table.Column<string>(type: "text", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    elapsed_ms = table.Column<double>(type: "double precision", nullable: false),
                    delta_ms = table.Column<double>(type: "double precision", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_execution_timeline_nodes", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_execution_timeline_nodes_chat_execution_records_execut~",
                        column: x => x.execution_record_id,
                        principalTable: "chat_execution_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_execution_records_model_name",
                table: "chat_execution_records",
                column: "model_name");

            migrationBuilder.CreateIndex(
                name: "IX_chat_execution_records_request_id",
                table: "chat_execution_records",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_execution_records_start_time",
                table: "chat_execution_records",
                column: "start_time");

            migrationBuilder.CreateIndex(
                name: "IX_chat_execution_timeline_nodes_execution_record_id",
                table: "chat_execution_timeline_nodes",
                column: "execution_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_chat_execution_timeline_nodes_execution_record_id_sequence",
                table: "chat_execution_timeline_nodes",
                columns: new[] { "execution_record_id", "sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_execution_timeline_nodes");

            migrationBuilder.DropTable(
                name: "chat_execution_records");
        }
    }
}
