using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LY.LlmPool.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixCallRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CompletionTokens",
                table: "EndpointCallRecords",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PromptTokens",
                table: "EndpointCallRecords",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotalTokens",
                table: "EndpointCallRecords",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletionTokens",
                table: "EndpointCallRecords");

            migrationBuilder.DropColumn(
                name: "PromptTokens",
                table: "EndpointCallRecords");

            migrationBuilder.DropColumn(
                name: "TotalTokens",
                table: "EndpointCallRecords");
        }
    }
}
