using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LY.LlmPool.DataMigrations.Sqlite.Migrations.LlmDb
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityTraces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActivityId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TraceId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SpanId = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ParentSpanId = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    OperationName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    StartTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    StatusDescription = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ErrorStackTrace = table.Column<string>(type: "TEXT", nullable: true),
                    OperationType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ResponseModelId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ProviderName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ResponseId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    FinishReason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Temperature = table.Column<float>(type: "REAL", nullable: true),
                    MaxTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    ServerAddress = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ServerPort = table.Column<int>(type: "INTEGER", nullable: true),
                    IsAppCall = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsToolCall = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ToolType = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolDataJson = table.Column<string>(type: "jsonb", nullable: true),
                    ServerType = table.Column<int>(type: "INTEGER", nullable: false),
                    McpServerName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    InputMessagesJson = table.Column<string>(type: "jsonb", nullable: true),
                    OutputContent = table.Column<string>(type: "TEXT", nullable: true),
                    TagsJson = table.Column<string>(type: "jsonb", nullable: true),
                    EventsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityTraces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "chat_execution_records",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    request_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    request_model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    model_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    start_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    end_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    total_duration_ms = table.Column<double>(type: "REAL", nullable: true),
                    ttfb_ms = table.Column<double>(type: "REAL", nullable: true),
                    message_count = table.Column<int>(type: "INTEGER", nullable: false),
                    tool_count = table.Column<int>(type: "INTEGER", nullable: false),
                    tool_call_count = table.Column<int>(type: "INTEGER", nullable: false),
                    chunk_count = table.Column<int>(type: "INTEGER", nullable: false),
                    total_characters = table.Column<int>(type: "INTEGER", nullable: false),
                    is_successful = table.Column<bool>(type: "INTEGER", nullable: false),
                    error_message = table.Column<string>(type: "TEXT", nullable: true),
                    summary = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_execution_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "Endpoints",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Endpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "llm_prompts",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    model_parameters = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_prompts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "McpServerConfigs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Command = table.Column<string>(type: "TEXT", nullable: true),
                    Args = table.Column<string>(type: "TEXT", nullable: true),
                    Env = table.Column<string>(type: "TEXT", nullable: true),
                    Headers = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SchemaCacheJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpServerConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModelTypes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Icon = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DefaultEndpoint = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "chat_execution_timeline_nodes",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    execution_record_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    node_type = table.Column<string>(type: "TEXT", nullable: false),
                    sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    elapsed_ms = table.Column<double>(type: "REAL", nullable: false),
                    delta_ms = table.Column<double>(type: "REAL", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_execution_timeline_nodes", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_execution_timeline_nodes_chat_execution_records_execution_record_id",
                        column: x => x.execution_record_id,
                        principalTable: "chat_execution_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "llm_prompt_history",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    prompt_id = table.Column<string>(type: "TEXT", nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    based_on_version = table.Column<int>(type: "INTEGER", nullable: true),
                    model_parameters = table.Column<string>(type: "TEXT", nullable: true),
                    test_configs = table.Column<string>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
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

            migrationBuilder.CreateTable(
                name: "prompt_tools",
                columns: table => new
                {
                    prompt_id = table.Column<string>(type: "TEXT", nullable: false),
                    tool_id = table.Column<string>(type: "TEXT", nullable: false),
                    tool_type = table.Column<string>(type: "TEXT", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "Configs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ModelTypeId = table.Column<string>(type: "TEXT", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AdditionalHeadersJson = table.Column<string>(type: "jsonb", nullable: true),
                    additional_parameters = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Configs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Configs_ModelTypes_ModelTypeId",
                        column: x => x.ModelTypeId,
                        principalTable: "ModelTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EndpointCallRecords",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EndpointId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    WaitTime = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    LlmConfigId = table.Column<string>(type: "TEXT", nullable: true),
                    ModelCallStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModelResponseStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModelResponseEndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsSuccessful = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    RequestDataJson = table.Column<string>(type: "jsonb", nullable: true),
                    ResponseDataJson = table.Column<string>(type: "jsonb", nullable: true),
                    ParentCallId = table.Column<string>(type: "TEXT", nullable: true),
                    PromptTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletionTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    TotalTokens = table.Column<long>(type: "INTEGER", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "EndpointConfigs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EndpointId = table.Column<string>(type: "TEXT", nullable: false),
                    LlmConfigId = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EndpointConfigs_Configs_LlmConfigId",
                        column: x => x.LlmConfigId,
                        principalTable: "Configs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EndpointConfigs_Endpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalTable: "Endpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "llm_apps",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    app_type = table.Column<string>(type: "TEXT", nullable: false),
                    orchestration_mode = table.Column<string>(type: "TEXT", nullable: true),
                    llm_prompt_id = table.Column<string>(type: "TEXT", nullable: true),
                    llm_config_id = table.Column<string>(type: "TEXT", nullable: true),
                    endpoint_id = table.Column<string>(type: "TEXT", nullable: true),
                    is_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    config_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_apps", x => x.id);
                    table.ForeignKey(
                        name: "FK_llm_apps_Configs_llm_config_id",
                        column: x => x.llm_config_id,
                        principalTable: "Configs",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_llm_apps_Endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalTable: "Endpoints",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_llm_apps_llm_prompts_llm_prompt_id",
                        column: x => x.llm_prompt_id,
                        principalTable: "llm_prompts",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "AgentMembers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LlmAppId = table.Column<string>(type: "TEXT", nullable: false),
                    LlmPromptId = table.Column<string>(type: "TEXT", nullable: true),
                    LlmConfigId = table.Column<string>(type: "TEXT", nullable: true)
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

            migrationBuilder.CreateIndex(
                name: "IX_Configs_ModelTypeId",
                table: "Configs",
                column: "ModelTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Configs_Name",
                table: "Configs",
                column: "Name",
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_EndpointConfigs_EndpointId_LlmConfigId",
                table: "EndpointConfigs",
                columns: new[] { "EndpointId", "LlmConfigId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EndpointConfigs_LlmConfigId",
                table: "EndpointConfigs",
                column: "LlmConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_Endpoints_Name",
                table: "Endpoints",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_llm_apps_endpoint_id",
                table: "llm_apps",
                column: "endpoint_id");

            migrationBuilder.CreateIndex(
                name: "IX_llm_apps_llm_config_id",
                table: "llm_apps",
                column: "llm_config_id");

            migrationBuilder.CreateIndex(
                name: "IX_llm_apps_llm_prompt_id",
                table: "llm_apps",
                column: "llm_prompt_id");

            migrationBuilder.CreateIndex(
                name: "IX_llm_apps_name",
                table: "llm_apps",
                column: "name",
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_McpServerConfigs_Name",
                table: "McpServerConfigs",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModelTypes_Name",
                table: "ModelTypes",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityTraces");

            migrationBuilder.DropTable(
                name: "AgentMembers");

            migrationBuilder.DropTable(
                name: "chat_execution_timeline_nodes");

            migrationBuilder.DropTable(
                name: "EndpointCallRecords");

            migrationBuilder.DropTable(
                name: "EndpointConfigs");

            migrationBuilder.DropTable(
                name: "llm_prompt_history");

            migrationBuilder.DropTable(
                name: "McpServerConfigs");

            migrationBuilder.DropTable(
                name: "prompt_tools");

            migrationBuilder.DropTable(
                name: "llm_apps");

            migrationBuilder.DropTable(
                name: "chat_execution_records");

            migrationBuilder.DropTable(
                name: "Configs");

            migrationBuilder.DropTable(
                name: "Endpoints");

            migrationBuilder.DropTable(
                name: "llm_prompts");

            migrationBuilder.DropTable(
                name: "ModelTypes");
        }
    }
}
