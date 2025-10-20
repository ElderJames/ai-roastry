# 方案 A: LlmPool 作为 OTLP Collector - 测试指南

## 🎯 架构概述

```
TestMcpServer (外部进程)                    LlmPool (OTLP Collector)
  ├─ ActivitySource                          ├─ OTLP 接收端点
  ├─ Activity (MCP SDK)                      │   └─ /v1/traces/json (JSON 格式)
  └─ LlmPoolActivityExporter ──HTTP/JSON──>  ├─ ActivityTraceService
                                              │   └─ AddExternalActivity()
                                              └─ AppCallTrace UI
                                                  └─ 显示跨进程 Activity
```

## ✅ 实现完成的功能

### 1. LlmPool (OTLP Collector 端)

**文件**: `src/LY.LlmPool.Web/Controllers/OtlpTraceController.cs`
- ✅ `/v1/traces/json` - 接收 JSON 格式的 Activity 数据
- ✅ `/v1/traces/health` - 健康检查端点

**文件**: `src/LY.LlmPool.Web/Services/Telemetry/ExternalActivityDto.cs`
- ✅ 外部 Activity DTO 定义

**文件**: `src/LY.LlmPool.Web/Services/Telemetry/OtlpTraceParser.cs`
- ⚠️ Protobuf 解析暂未实现 (由于 protobuf 类的访问级别问题)
- ✅ JSON 格式接收已实现

**文件**: `src/LY.LlmPool.Web/Services/Telemetry/ActivityTraceService.cs`
- ✅ `AddExternalActivity()` - 接收并存储外部 Activity
- ✅ 自动关联 TraceId,形成完整调用链
- ✅ 标记外部 Activity (通过 Tags)

**新增 NuGet 包**:
- `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.10.0
- `Google.Protobuf` 3.28.3 (暂未使用)

### 2. TestMcpServer (OTLP Exporter 端)

**文件**: `tests/TestMcpServer/LlmPoolActivityExporter.cs`
- ✅ 自定义 Activity Exporter
- ✅ 将 Activity 序列化为 JSON
- ✅ POST 到 LlmPool 的 `/v1/traces/json` 端点

**文件**: `tests/TestMcpServer/Program.cs`
- ✅ 配置 OpenTelemetry
- ✅ 使用 `LlmPoolActivityExporter`
- ✅ 使用 `BatchActivityExportProcessor` (批量导出)

**新增 NuGet 包**:
- `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.10.0

## 🧪 测试步骤

### 前置条件

1. 确保 LlmPool 数据库已初始化
2. 配置至少一个可用的模型 (如 `gpt-4o-mini`)
3. 创建一个测试 App (可选,用于完整测试)

### 步骤 1: 启动 LlmPool

```bash
cd e:\lianyuan\llm-pool
dotnet run --project src/LY.LlmPool.Web/LY.LlmPool.Web.csproj
```

**验证**:
- ✅ 浏览器访问: `http://localhost:5054`
- ✅ 日志中看到: "ActivityTraceService 已初始化"
- ✅ 访问健康检查: `http://localhost:5054/v1/traces/health`

预期响应:
```json
{
  "status": "healthy",
  "service": "LlmPool OTLP Collector",
  "endpoint": "/v1/traces",
  "supportedFormats": ["application/x-protobuf", "application/json"]
}
```

### 步骤 2: 启动 TestMcpServer

```bash
# 新终端
cd e:\lianyuan\llm-pool\tests\TestMcpServer
dotnet run
```

**验证日志**:
```
🚀 TestMcpServer 启动中...
✅ OpenTelemetry 追踪已启用
等待 5 秒后开始运行示例...
================================
开始运行 LlmPoolClient 示例...
================================

--- Example 1: Call Model ---
🔧 Example 1: Call Model | TraceId: abc123... | SpanId: def456...
📞 Calling LlmPool model 'gpt-4o-mini' directly...
   ActivityPropagationHandler 会自动注入 traceparent header
```

**关键检查点**:

1. **TestMcpServer 日志** - 确认 Activity 导出成功:
   ```
   ✅ Successfully exported 3 activities to LlmPool
   ```

2. **LlmPool 日志** - 确认接收到外部 Activity:
   ```
   📥 External Activity Added: chat gpt-4o-mini | TraceId: abc123... | SpanId: def456... | Source: Experimental.ModelContextProtocol
   ```

### 步骤 3: 查看 AppCallTrace UI

访问: `http://localhost:5054/app-call-trace`

**预期结果**:

1. **显示完整的调用链**:
   ```
   External Request (来自 TestMcpServer)
     └─ OpenAI Controller (LlmPool 进程)
         └─ chat gpt-4o-mini
             └─ ... (模型响应)
   ```

2. **外部 Activity 的标识**:
   - Tags 中包含 `ServiceName: TestMcpServer`
   - Tags 中包含 `ScopeName: Experimental.ModelContextProtocol`
   - Kind: `External`

3. **TraceId 一致性**:
   - 所有 Activity 具有相同的 TraceId
   - ParentSpanId 正确关联

### 步骤 4: 测试递归调用 (App → MCP Server → App)

**前提**: 在 LlmPool 中创建一个名为 `helper-app` 的 App

1. 访问 `http://localhost:5054/prompt-edit`
2. 创建新 App:
   - Name: `helper-app`
   - Model: `gpt-4o-mini`
   - System Prompt: "You are a helpful assistant."
   - Tools: (无需配置)

3. TestMcpServer 会自动调用这个 App (在 Example 2 中)

4. 在 `/app-call-trace` 中查看:
   ```
   TestMcpServer Activity
     └─ OpenAI Controller (第一次调用)
         └─ LlmPoolClient.ChatAsync
             └─ OpenAI Controller (递归调用 helper-app)
                 └─ App: helper-app
                     └─ Model: gpt-4o-mini
   ```

## 🔍 验证要点

### 1. TraceId 传播

所有 Activity (包括外部的) 应具有相同的 TraceId。

**检查方法**:
- 在 AppCallTrace UI 中,点击某个 trace
- 展开查看所有 Activity
- 验证 TraceId 字段相同

### 2. ParentSpanId 关联

子 Activity 的 ParentSpanId 应等于父 Activity 的 SpanId。

**检查方法**:
- 查看 Activity 详情
- 比对 ParentSpanId 与父节点的 SpanId

### 3. 外部 Activity 识别

来自 TestMcpServer 的 Activity 应被正确标识。

**检查方法**:
- Tags 中包含 `ServiceName: TestMcpServer`
- Tags 中包含 `Source: Experimental.ModelContextProtocol`

### 4. 时间线一致性

子 Activity 的时间范围应在父 Activity 的时间范围内。

**检查方法**:
- 查看 StartTime 和 EndTime
- 父 Activity 的 StartTime ≤ 子 Activity 的 StartTime
- 父 Activity 的 EndTime ≥ 子 Activity 的 EndTime

## 📊 性能测试

### 批量导出测试

TestMcpServer 使用 `BatchActivityExportProcessor`,默认配置:
- 批量大小: 512 Activities
- 导出间隔: 5 秒
- 队列大小: 2048 Activities

**测试方法**:
1. 修改 TestMcpServer 循环调用 LlmPoolClient
2. 观察 LlmPool 的 `/v1/traces/json` 端点接收频率
3. 验证 Activity 不丢失

### 压力测试

**步骤**:
1. 同时启动多个 TestMcpServer 实例
2. 每个实例并发调用 LlmPoolClient
3. 观察 LlmPool 的 CPU 和内存使用
4. 验证 AppCallTrace UI 响应性能

## 🐛 故障排查

### 问题 1: TestMcpServer 无法导出 Activity

**症状**:
- TestMcpServer 日志显示: "⚠️ Failed to export activities: 404"

**解决方案**:
1. 确认 LlmPool 已启动并监听 5054 端口
2. 访问 `http://localhost:5054/v1/traces/health` 验证端点可用
3. 检查 TestMcpServer 的配置端点是否正确

### 问题 2: LlmPool 未接收到 Activity

**症状**:
- LlmPool 日志中没有 "📥 External Activity Added" 消息

**解决方案**:
1. 检查 OtlpTraceController 是否正确注册
2. 验证 `/v1/traces/json` 端点可访问
3. 使用 Postman 手动 POST JSON 数据测试

示例 JSON:
```json
[
  {
    "TraceId": "test-trace-id-123",
    "SpanId": "test-span-id-456",
    "ParentSpanId": null,
    "OperationName": "test-operation",
    "StartTimeUtc": "2025-10-19T10:00:00Z",
    "Duration": "00:00:01",
    "Tags": {
      "service.name": "TestService"
    },
    "Source": "TestSource",
    "Status": 1,
    "StatusDescription": null
  }
]
```

### 问题 3: AppCallTrace UI 未显示外部 Activity

**症状**:
- Activity 已接收 (日志确认),但 UI 中看不到

**解决方案**:
1. 刷新 `/app-call-trace` 页面
2. 检查 Activity 的 TraceId 是否与现有 traces 匹配
3. 验证 ActivityTraceService.AddExternalActivity() 是否正确触发事件

### 问题 4: TraceId 不一致

**症状**:
- 外部 Activity 的 TraceId 与 LlmPool 内部 Activity 不同

**解决方案**:
1. 确认 LlmPoolClient 使用了 ActivityPropagationHandler
2. 检查 TestMcpServer 的 Activity.Current 是否存在
3. 验证 traceparent header 是否正确注入

## 🎯 后续改进

### 1. Protobuf 支持 (推荐)

当前仅支持 JSON 格式,JSON 序列化开销较大。

**改进方案**:
- 生成自定义的 Protobuf 类 (使用 protoc)
- 实现 `OtlpTraceParser.ParseProtobuf()` 方法
- 更新 TestMcpServer 使用 Protobuf 格式

**优势**:
- 更小的网络传输量 (约 30-50% 减少)
- 更快的序列化/反序列化速度

### 2. 批量接收优化

当前每个请求单独处理,高并发时可能成为瓶颈。

**改进方案**:
- 使用异步队列接收 Activity
- 批量写入 ActivityTraceService
- 添加背压机制 (Back pressure)

### 3. Activity 过期清理

当前完成的 Activity 保留在内存中 (最多 100 条)。

**改进方案**:
- 添加 TTL (Time To Live) 配置
- 定期清理过期的 Activity
- 可选: 持久化到数据库

### 4. UI 增强

**改进方案**:
- 在 AppCallTrace UI 中高亮显示外部 Activity
- 添加过滤器: 仅显示外部/内部 Activity
- 添加时间线视图 (Timeline View)
- 导出为 Jaeger 格式 (用于离线分析)

### 5. Jaeger 集成 (可选)

**改进方案**:
- LlmPool 同时导出到 Jaeger
- 在 Jaeger UI 中查看完整跨进程链路
- 利用 Jaeger 的高级查询功能

**启动 Jaeger**:
```bash
docker run -d \
  --name jaeger \
  -p 4317:4317 \
  -p 4318:4318 \
  -p 16686:16686 \
  jaegertracing/all-in-one:latest
```

**访问**: `http://localhost:16686`

## 📝 总结

### 已实现功能

| 功能 | 状态 | 说明 |
|-----|------|------|
| OTLP JSON 接收端点 | ✅ 完成 | /v1/traces/json |
| OTLP Protobuf 接收 | ⚠️ 未实现 | 需要生成 protobuf 类 |
| 外部 Activity 存储 | ✅ 完成 | AddExternalActivity() |
| TraceId 传播 | ✅ 完成 | 跨进程一致性 |
| AppCallTrace UI 显示 | ✅ 完成 | 自动关联显示 |
| TestMcpServer 导出 | ✅ 完成 | LlmPoolActivityExporter |

### 架构优势

1. **简化部署**: 无需额外的 Jaeger/Tempo 服务
2. **实时查看**: 直接在 LlmPool UI 中查看追踪
3. **自动关联**: TraceId 自动关联,形成完整链路
4. **灵活扩展**: 可轻松添加更多外部服务

### 下一步

1. ✅ **测试**: 按照本文档完成测试
2. 📝 **文档**: 更新用户文档和 API 文档
3. 🎯 **优化**: 实现 Protobuf 支持
4. 🚀 **生产**: 添加监控和告警

---

**开始测试吧!** 🚀
