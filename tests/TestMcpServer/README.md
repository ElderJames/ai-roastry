# TestMcpServer - 完整 MCP Server 实现

 已注册真实的 MCP Tools (llmpool_call_app, llmpool_call_model, get_current_time)
 使用 LlmPoolClient 实现递归调用
 通过 OpenTelemetry 导出追踪到 LlmPool
 完整的 Activity 跨进程传播

##  快速开始

### 步骤 1: 启动 LlmPool
```powershell
dotnet run --project src/LY.LlmPool.Web/LY.LlmPool.Web.csproj
```

### 步骤 2: 配置 TestMcpServer
访问: http://localhost:5054/mcp-config
- Name: TestMcpServer
- Type: Stdio
- Command: dotnet
- Args: run --project E:\lianyuan\llm-pool\tests\TestMcpServer\TestMcpServer.csproj

### 步骤 3: 创建 App
访问: http://localhost:5054/apps
- App Name: recursive-test-app
- Select Tools: llmpool_call_app, get_current_time

### 步骤 4: 测试
```bash
curl http://localhost:5054/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -H 'Authorization: Bearer test-key' \
  -d '{\"model\": \"recursive-test-app\", \"messages\": [{\"role\": \"user\", \"content\": \"Use llmpool_call_app to call weather-app\"}]}'
```

### 步骤 5: 查看追踪
访问: http://localhost:5054/app-call-trace

##  已注册的 Tools

1. **llmpool_call_app** (推荐) - 调用 LlmPool 中的 App
2. **llmpool_call_model** - 直接调用 Model
3. **get_current_time** - 演示用简单 Tool

##  相关文档
- docs/OTLP-Collector-Testing-Guide.md
- docs/Cross-Process-Tracing-Architecture.md
- docs/How-To-Create-Real-MCP-Server.md
