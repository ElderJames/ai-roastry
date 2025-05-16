using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LY.LlmPool.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLlmConfigId1Shadow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EndpointConfigs_Configs_LlmConfigId1",
                table: "EndpointConfigs");

            migrationBuilder.DropIndex(
                name: "IX_EndpointConfigs_LlmConfigId1",
                table: "EndpointConfigs");

            migrationBuilder.DropColumn(
                name: "LlmConfigId1",
                table: "EndpointConfigs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LlmConfigId1",
                table: "EndpointConfigs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EndpointConfigs_LlmConfigId1",
                table: "EndpointConfigs",
                column: "LlmConfigId1");

            migrationBuilder.AddForeignKey(
                name: "FK_EndpointConfigs_Configs_LlmConfigId1",
                table: "EndpointConfigs",
                column: "LlmConfigId1",
                principalTable: "Configs",
                principalColumn: "Id");
        }
    }
}
