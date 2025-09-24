using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace LY.LlmPool.Web.Services
{
    public class BuiltinTools
    {
        [KernelFunction("echo")]
        [Description("回显输入的文本")]
        public string Echo([Description("要回显的内容")] string text)
        {
            return text;
        }

        [KernelFunction("get_time")]
        [Description("返回当前时间，可选格式化")]
        public string GetTime([Description(".NET 日期时间格式化字符串")] string? format = "yyyy-MM-dd HH:mm:ss")
        {
            var fmt = string.IsNullOrWhiteSpace(format) ? "yyyy-MM-dd HH:mm:ss" : format;
            return DateTime.Now.ToString(fmt);
        }

        [KernelFunction("sum")]
        [Description("计算两个整数的和")]
        public int Sum(
            [Description("加数 A")] int a,
            [Description("加数 B")] int b)
        {
            return a + b;
        }
    }
}