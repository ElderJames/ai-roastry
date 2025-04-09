using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LY.LlmPool.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCallRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EndpointCallRecords",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    EndpointId = table.Column<string>(type: "text", nullable: false),
                    RequestReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    WaitTime = table.Column<TimeSpan>(type: "interval", nullable: false),
                    LlmConfigId = table.Column<string>(type: "text", nullable: true),
                    ModelCallStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ModelResponseStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ModelResponseEndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsSuccessful = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    RequestDataJson = table.Column<string>(type: "jsonb", nullable: true),
                    ResponseDataJson = table.Column<string>(type: "jsonb", nullable: true),
                    ParentCallId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointCallRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EndpointCallRecords_Configs_LlmConfigId",
                        column: x => x.LlmConfigId,
                        principalTable: "Configs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_EndpointCallRecords_EndpointCallRecords_ParentCallId",
                        column: x => x.ParentCallId,
                        principalTable: "EndpointCallRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EndpointCallRecords_Endpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalTable: "Endpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointCallRecords_EndpointId",
                table: "EndpointCallRecords",
                column: "EndpointId");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointCallRecords_LlmConfigId",
                table: "EndpointCallRecords",
                column: "LlmConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointCallRecords_ParentCallId",
                table: "EndpointCallRecords",
                column: "ParentCallId");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointCallRecords_RequestReceivedAt",
                table: "EndpointCallRecords",
                column: "RequestReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EndpointCallRecords");
        }
    }
}
