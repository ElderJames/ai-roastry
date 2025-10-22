# 测试 ConversationId 传播 - 多轮对话测试
# 使用相同的 ConversationId 发起两次请求，验证它们是否关联在同一个 conversation

$conversationId = "test-conv-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Write-Host "🧪 测试 ConversationId: $conversationId" -ForegroundColor Green
Write-Host "📝 将发送两次请求，验证多轮对话关联性" -ForegroundColor Cyan
Write-Host ""

# ========== 第一次请求 ==========
Write-Host "📤 [1/2] 发送第一次请求 (2 + 3)..." -ForegroundColor Yellow

$body1 = @{
    messages = @(
        @{
            role = "system"
            content = "你是一个计算器，可帮助用户计算简单的计算。请计算 2 + 3"
        },
        @{
            role = "user"
            content = "query: 2 + 3"
        }
    )
    model = "calc"
    stream = $false
} | ConvertTo-Json -Depth 10

try {
    $response1 = Invoke-RestMethod -Uri "http://localhost:5071/v1/chat/completions" `
        -Method POST `
        -Body $body1 `
        -ContentType "application/json" `
        -Headers @{
            "Authorization" = "Bearer test-api-key"
            "X-Conversation-Id" = $conversationId
        }

    Write-Host "  ✅ 第一次请求完成" -ForegroundColor Green
    Write-Host "  📥 响应: $($response1.choices[0].message.content)" -ForegroundColor White
}
catch {
    Write-Host "  ❌ 第一次请求失败: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  💡 提示：请确保应用正在运行" -ForegroundColor Yellow
    exit 1
}

# 等待 2 秒，让第一次请求的 Activity 完全处理
Write-Host ""
Write-Host "⏳ 等待 2 秒..." -ForegroundColor Gray
Start-Sleep -Seconds 2

# ========== 第二次请求 ==========
Write-Host "📤 [2/2] 发送第二次请求 (3 + 4) - 使用相同的 ConversationId..." -ForegroundColor Yellow

$body2 = @{
    messages = @(
        @{
            role = "system"
            content = "你是一个计算器，可帮助用户计算简单的计算。请计算 3 + 4"
        },
        @{
            role = "user"
            content = "query: 3 + 4"
        }
    )
    model = "calc"
    stream = $false
} | ConvertTo-Json -Depth 10

try {
    $response2 = Invoke-RestMethod -Uri "http://localhost:5071/v1/chat/completions" `
        -Method POST `
        -Body $body2 `
        -ContentType "application/json" `
        -Headers @{
            "Authorization" = "Bearer test-api-key"
            "X-Conversation-Id" = $conversationId
        }

    Write-Host "  ✅ 第二次请求完成" -ForegroundColor Green
    Write-Host "  📥 响应: $($response2.choices[0].message.content)" -ForegroundColor White
}
catch {
    Write-Host "  ❌ 第二次请求失败: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  💡 提示：请确保应用正在运行" -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "🔍 现在查看日志..." -ForegroundColor Cyan
Start-Sleep -Seconds 2

# 查看本次测试的 ConversationId 相关日志
Write-Host ""
Write-Host "=== ConversationId 传播日志 ===" -ForegroundColor Magenta
Write-Host "🔎 搜索: $conversationId" -ForegroundColor Gray
Write-Host ""

Get-Content "E:\lianyuan\llm-pool\src\LY.LlmPool.Web\logs\activity-debug.log" -Tail 500 | 
    Select-String "$conversationId|传播到" | 
    Select-Object -Last 40

Write-Host ""
Write-Host "✨ 测试完成！" -ForegroundColor Green
Write-Host "💡 请在 UI 中验证：" -ForegroundColor Yellow
Write-Host "   1. 两次请求都有 gen_ai.conversation.id: $conversationId" -ForegroundColor White
Write-Host "   2. 它们在 UI 中显示为同一个 conversation" -ForegroundColor White
Write-Host "   3. 所有子节点（llmpool.server）都正确继承了 ConversationId" -ForegroundColor White

Write-Host "`n=== Activity 启动日志 ===" -ForegroundColor Magenta
Get-Content "E:\lianyuan\llm-pool\src\LY.LlmPool.Web\logs\activity-debug.log" -Tail 200 | 
    Select-String "Activity Started" | 
    Select-Object -Last 10
