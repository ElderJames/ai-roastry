using System.Text.Encodings.Web;
using System.Text.Json;

namespace LY.LlmPool.Web.Common;

/// <summary>
/// JSON 序列化辅助类 - 提供全局统一的序列化选项
/// <para>用法1: JsonHelper.Serialize(obj) - 使用辅助方法</para>
/// <para>用法2: JsonSerializer.Serialize(obj, JsonHelper.Options) - 使用全局配置</para>
/// </summary>
public static class JsonHelper
{
    /// <summary>
    /// 全局 JSON 序列化选项: 支持中文、换行格式化
    /// <para>可用于: JsonSerializer.Serialize(obj, JsonHelper.Options)</para>
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, // 换行格式化
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // 支持中文不转义
    };

    /// <summary>
    /// 序列化对象为 JSON 字符串(使用全局选项)
    /// </summary>
    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Options);
    }

    /// <summary>
    /// 反序列化 JSON 字符串为对象(使用全局选项)
    /// </summary>
    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, Options);
    }
}
