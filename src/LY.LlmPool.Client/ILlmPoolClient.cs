namespace LY.LlmPool.Client;

/// <summary>
/// LlmPool 客户端接口,用于依赖注入和单元测试
/// </summary>
public interface ILlmPoolClient
{
    /// <summary>
    /// 当前会话的 ConversationId（从服务端响应中获取或设置）
    /// </summary>
    string? ConversationId { get; set; }

    /// <summary>
    /// 发送非流式聊天请求
    /// </summary>
    /// <param name="model">模型名称</param>
    /// <param name="messages">聊天消息列表</param>
    /// <param name="parameters">参数字典(用于 prompt 参数替换)</param>
    /// <param name="toolObjects">工具对象列表</param>
    /// <param name="options">聊天选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>AI 响应文本</returns>
    Task<string> ChatAsync(
        string model,
        IEnumerable<ClientMessage> messages,
        Dictionary<string, object>? parameters = null,
        IEnumerable<object>? toolObjects = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送流式聊天请求
    /// </summary>
    /// <param name="model">模型名称</param>
    /// <param name="messages">聊天消息列表</param>
    /// <param name="parameters">参数字典(用于 prompt 参数替换)</param>
    /// <param name="toolObjects">工具对象列表</param>
    /// <param name="options">聊天选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>流式更新的异步枚举</returns>
    IAsyncEnumerable<StreamingChatUpdate> ChatStreamAsync(
        string model,
        IEnumerable<ClientMessage> messages,
        Dictionary<string, object>? parameters = null,
        IEnumerable<object>? toolObjects = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);
}
