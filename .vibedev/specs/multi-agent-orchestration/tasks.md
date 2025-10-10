# 实施任务清单：多 Agent 编排框架

> 基于 requirements.md 与 design.md，以下任务将以小步快跑、可测试的方式推进，每步均可由编码代理独立完成。

1. [x] 新增 `AppType` 与编排字段到 `LlmApp`
   - 目标：为 `LlmApp` 增加 `AppType` 与可选的 `OrchestrationMode`，并在 DbContext 中建模（REQ-API-103, REQ-ORCH-201）。
   - 子项：
  - 1.1 [x] 修改 `Data/Entities/LlmApp.cs` 增加字段与导航属性（参见设计 4.1）。
  - 1.2 [x] 更新 `Data/LlmDbContext.cs`：配置枚举、关系与索引。
  - 1.3 [x] 添加/更新 EF 迁移与数据库（执行脚本）。
  - 1.4 [x] 添加最小单元测试：确认模型可创建并保存（覆盖 AppType=Prompt 与 AgentGroup）。

2. [x] 新增 `AgentMember` 实体并与 `LlmApp` 关联
   - 目标：为 AgentGroup 类型的 App 配置成员，绑定各自的 Prompt 与 LlmConfig（REQ-ORCH-101, REQ-ORCH-103）。
   - 子项：
  - 2.1 [x] 创建 `Data/Entities/AgentMember.cs`（参见设计 4.2）。
  - 2.2 [x] 更新 `LlmDbContext.cs` 关系映射（App->Members, Member->Prompt/Config）。
  - 2.3 [x] 添加 EF 迁移与数据库更新。
  - 2.4 [x] 添加单元测试：创建含两个成员的 AgentGroup App 并校验持久化关系。

3. [x] 调整 `AgentTool`，改为关联 `AgentMember`
   - 目标：让工具清单挂到具体的成员 Agent 上（REQ-AGENT-301, REQ-AGENT-302, REQ-AGENT-305）。
   - 子项：
  - 3.1 [x] 修改 `Data/Entities/AgentTool.cs`：将 `AgentId` 改为 `AgentMemberId`（参见设计 4.4）。
  - 3.2 [x] 更新 `LlmDbContext.cs` 关系映射与复合键配置。
  - 3.3 [x] 添加迁移并更新数据库。
   - 3.4 [x] 添加单元测试：为某成员配置内部与 MCP 工具并验证保存。（注意：InMemory 提供程序对复合键重复由跟踪器抛 InvalidOperationException，测试已按此验证）

4. [x] 保持 `McpServerConfig` 实体与 CRUD 能力
   - 目标：提供持久化 MCP Server 元数据的能力（REQ-AGENT-303, REQ-AGENT-304）。
   - 子项：
  - 4.1 [x] 确认/创建 `Data/Entities/McpServerConfig.cs`（设计 4.3）。
  - 4.2 [x] 在 `LlmDbContext.cs` 中添加 DbSet 与索引。
   - 4.3 [x] 添加最小仓储/服务或现有服务扩展的代码。
   - 4.4 [x] 添加单元测试：创建、查询、更新、删除。

5. [x] 运行时 `Agent` 模型与 `IAgentTool` 接口
   - 目标：定义运行时 Agent 结构与工具接口（REQ-AGENT-201~205, REQ-AGENT-301~305）。
   - 子项：
   - 5.1 [x] 新建 `Services/Agents/Agent.cs`（运行时类），从 `AgentMember` 构造。
   - 5.2 [x] 新建 `Services/Agents/IAgentTool.cs` 与参数 schema 接口。
   - 5.3 [x] 实现 `InternalPluginTool` 与 `McpTool` 框架（占位实现）。
   - 5.4 [x] 添加单元测试：构造 Agent，注册工具，验证基本行为（调用签名与参数校验）。

6. [x] ReActEngine 框架与工具执行器
   - 目标：实现思考-行动循环骨架，支持工具调用与降级（REQ-AGENT-201~205）。
   - 子项：
     - 6.1 [x] 新建 `Services/Agents/ReActEngine.cs`，集成 `ChatClientService` 占位接口。
     - 6.2 [x] 新建 `Services/Agents/ToolExecutor.cs`，路由 Internal/MCP/ContextExtractor。
     - 6.3 [x] 实现降级逻辑：无 Tool Calling 则文本模式。
     - 6.4 [x] 单元测试：模拟不同返回，验证循环与降级分支。

7. [x] Orchestrator 与策略模式
   - 目标：按 `Sequential` 与 `GroupChat` 模式编排（REQ-ORCH-201~206）。
   - 子项：
  - 7.1 [x] 新建 `Services/Agents/AgentOrchestratorService.cs` 与 `IOrchestrationStrategy`。
  - 7.2 [x] 实现 `SequentialStrategy` 与 `GroupChatStrategy` 骨架。
     - 7.3 [x] 单元测试：构造伪 Agent，验证顺序传递与共享历史。

8. [x] API 接入：OpenAI chat.completions 兼容
   - 目标：对外统一为 `POST /v1/chat/completions`，内部区分 App 类型（REQ-API-101~104，对齐 OpenAI）。
   - 子项：
  - 8.1 [x] 扩展现有 OpenAI 兼容控制器，解析 `model` 为 `LlmApp`（支持 Prompt/AgentGroup）。
  - 8.2 [x] 将 `messages` 作为历史，`tools/tool_choice` 用于覆盖工具与策略（可选）。
  - 8.3 [x] 当 AppType=Prompt 时走现有路径；为 AgentGroup 时调用 `AgentOrchestratorService`。
   - 8.4 [x] 返回 OpenAI `chat.completions` 兼容结构；保留 `stream` 支持与 SSE。

9. [x] ContextExtractor 与 MemoryQuery 工具
   - 目标：实现后台摘要与查询（REQ-AGENT-401~405）。
   - 子项：
     - 9.1 [x] 新建 `Services/Agents/ContextMemoryStore`（可用 EF 或内存实现）。
     - 9.2 [x] 新建 `ContextExtractorTool` 与 `MemoryQueryTool`。
     - 9.3 [x] 后台执行：使用 `Task.Run` 或队列；添加单元测试验证异步不阻塞主流程。

10. [ ] 示例与冒烟测试
   - 目标：提供一个最小可运行示例，串起端到端路径（不等同用户 UAT）（覆盖多条需求）。
   - 子项：
      - 10.1 [x] Seed 数据：一个 AgentGroup App，含两成员与一内部工具。
         - 10.2 [x] 集成测试：调用 API 并断言结果结构与关键路径执行。（已通过 ApiStreamingTests 验证 SSE/AgentGroup 关键路径）

11. [x] 迁移与兼容性检查
   - 目标：确保旧数据与新结构兼容，迁移安全（总体保障）。
   - 子项：
     - 11.1 [x] 审查现有 `LlmApp` 数据使用场景，添加必要的 `null` 处理或默认值。
     - 11.2 [x] 升级脚本：如需默认 `AppType=Prompt` 的迁移脚本。
     - 11.3 [x] 回滚验证：确保迁移可还原。

12. [x] 管理界面：AgentGroup App 与成员配置 UI
   - 目标：提供 App=AgentGroup 的可视化配置界面（REQ-ORCH-104）。
   - 子项：
     - 12.1 [x] 在 `LY.LlmPool.Web/Components/Pages/` 新建或扩展页面：创建/编辑 AgentGroup App（名称、描述、编排模式）。
     - 12.2 [x] 成员管理 UI：增删改查 `AgentMember`，选择绑定 `LlmPrompt`、`LlmConfig`，设置顺序/角色。
     - 12.3 [x] 工具清单 UI：为成员勾选内部工具与 MCP 工具（与 `AgentTool` 持久化对接）。
     - 12.4 [x] 数据获取服务：新增/扩展 API/Controller/Endpoints 供前端调用。

13. [x] 管理界面：MCP Server 配置 UI
   - 目标：提供 `McpServerConfig` 的增删改查界面（REQ-AGENT-304）。
   - 子项：
   - 13.1 [x] 在 `LY.LlmPool.Web/Components/Pages/` 新建 MCP 配置页面：列表、创建、编辑、删除。（已实现 `McpConfig.razor` 并接入导航）
     - 13.2 [x] 前端调用服务：封装 CRUD 请求；后端 Controller/Endpoints 支持。
     - 13.3 [x] 验证工具可见性：`AgentTool` 选择器能展示 MCP 工具来源列表。

14. [ ] 管理界面：Agent 统一管理入口
   - 目标：提供面向 `LlmApp` 的统一视图，区分 `Prompt` 与 `AgentGroup`（REQ-AGENT-103）。
   - 子项：
  - 14.1 [x] App 列表页：展示 AppType；支持创建两种类型的 App。
  - 14.2 [x] Prompt App 编辑页：选择 `LlmPrompt` 与 `LlmConfig`（沿用现有 UI/增强）。
  - 14.3 [x] 细化路由与导航：在 `Routes.razor` 加入入口；侧边栏/菜单项调整。

15. [ ] 自动化测试：单元测试覆盖核心模块
   - 目标：以 TDD 为导向覆盖关键逻辑（多条需求）。
   - 子项：
     - 15.1 [ ] ReActEngine 解析/循环测试：工具调用分支与降级（REQ-AGENT-201~205）。
     - 15.2 [ ] ToolExecutor 路由测试：Internal/MCP/ContextExtractor（REQ-AGENT-301~305, REQ-AGENT-401~405）。
     - 15.3 [ ] Orchestration 策略测试：Sequential 与 GroupChat（REQ-ORCH-201~206）。
   - 15.4 [x] 数据模型测试：LlmApp(AppType/OrchestrationMode)、AgentMember、AgentTool、McpServerConfig（REQ-AGENT-303）。

16. [ ] 自动化测试：存储层与迁移
   - 目标：验证 EF 迁移与基本 CRUD 正确性（稳定性保障）。
   - 子项：
     - 16.1 [ ] 使用 InMemory/SQLite 模式验证迁移应用、回滚（与 11.* 呼应）。
     - 16.2 [ ] CRUD 测试：McpServerConfig、AgentMember、AgentTool（REQ-AGENT-303/304, REQ-ORCH-103）。

17. [ ] 自动化测试：API 集成测试
   - 目标：验证 `POST /v1/apps/{appId}/invoke` 行为（REQ-API-101~104）。
   - 子项：
     - 17.1 [ ] 使用 TestServer 构建最小 WebHost，注入内存数据源与模拟 ChatClientService。
     - 17.2 [ ] 场景 A：AppType=Prompt，直接调用 Prompt 路径。
     - 17.3 [ ] 场景 B：AppType=AgentGroup，顺序编排路径；验证输出摘要。
     - 17.4 [ ] 场景 C：GroupChat 模式；验证共享历史与终止条件。

18. [ ] 自动化测试：上下文/记忆工具
   - 目标：验证后台提取与查询的可用性（REQ-AGENT-401~405）。
   - 子项：
     - 18.1 [ ] ContextExtractorTool：后台执行不阻塞主循环；完成后可被查询。
     - 18.2 [ ] MemoryQueryTool：查询到已写入的摘要；并发/顺序访问可靠性。

19. [ ] 自动化测试：示例与冒烟
   - 目标：提供端到端自动化冒烟（非用户 UAT）（综合覆盖）。
   - 子项：
     - 19.1 [ ] 种子数据示例：两成员 Sequential，含一个内部工具；从 API 调用验证关键路径。
     - 19.2 [ ] 错误路径测试：工具失败、配置缺失、轮次上限触发（REQ-ORCH-205）。

20. [ ] 端到端 E2E 测试
    - 目标：在测试项目中添加一个端到端（E2E）测试，用最小的种子数据（SampleAgentGroup）验证从 HTTP API 到编排执行（包含流式 SSE 与非流式响应）的完整路径。
    - 子项：
       - 20.1 [ ] 新建测试 `AgentGroupE2eTests`，使用现有 `TestWebApplicationFactory` 启动测试服务器并注入内存数据库与模拟上游 LLM（Fake upstream 已有基础设施可复用）。
       - 20.2 [ ] 场景 A：stream=false，断言返回的 JSON 包含 choices[0].message.content 与模型标识。
       - 20.3 [ ] 场景 B：stream=true，断言响应 Content-Type 为 text/event-stream，并且流中包含至少一次 agent 前缀（例如 "[Planner]" 或 "[Researcher]"），以及最终的 "[DONE]" 标记。
       - 20.4 [ ] 将测试加入 CI 过滤（由维护方在 pipeline 中启用），并确保本地可快速运行（单测运行时间短）。
