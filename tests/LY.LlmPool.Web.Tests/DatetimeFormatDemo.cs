using Xunit;
using Xunit.Abstractions;
using LY.LlmPool.Web.Services;

namespace LY.LlmPool.Web.Tests;

/// <summary>
/// 演示 @datetime 完整格式输出
/// </summary>
public class DatetimeFormatDemo
{
    private readonly ITestOutputHelper _output;

    public DatetimeFormatDemo(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Demo_DatetimeFullFormat()
    {
        var service = new PromptEnvironmentService();
        
        // 演示默认完整格式
        var prompt1 = "现在是 @datetime";
        var result1 = service.ReplaceEnvironmentVariables(prompt1);
        _output.WriteLine("完整格式:");
        _output.WriteLine(result1);
        _output.WriteLine("");
        
        // 演示各种组件
        var prompt2 = @"
日期: @date
时间: @time
星期: @weekday
时区: @timezone
完整: @datetime
";
        var result2 = service.ReplaceEnvironmentVariables(prompt2);
        _output.WriteLine("各组件对比:");
        _output.WriteLine(result2);
        _output.WriteLine("");
        
        // 演示自定义格式仍然可用
        var prompt3 = "自定义格式: @datetime:yyyy年MM月dd日 HH时mm分ss秒";
        var result3 = service.ReplaceEnvironmentVariables(prompt3);
        _output.WriteLine("自定义格式:");
        _output.WriteLine(result3);
        
        // 验证完整格式包含所有信息
        Assert.Contains("星期", result1);
        Assert.Contains("UTC", result1);
        Assert.DoesNotContain("@datetime", result1);
    }

    [Fact]
    public void Demo_AllEnvironmentVariables()
    {
        var service = new PromptEnvironmentService();
        
        var prompt = @"
=== 时间信息 ===
完整时间: @datetime
仅日期: @date
仅时间: @time
年份: @year
月份: @month
日期: @day
星期: @weekday
时区: @timezone

=== 时间戳 ===
Unix时间戳(秒): @timestamp
Unix时间戳(毫秒): @timestamp_ms

=== UTC时间 ===
UTC完整: @utc
UTC日期: @utc_date
UTC时间: @utc_time

=== 系统信息 ===
用户: @user
主机: @machine
系统: @os

=== 随机值 ===
GUID: @guid
随机数: @random
随机0-1000: @random:1000
";
        
        var result = service.ReplaceEnvironmentVariables(prompt);
        _output.WriteLine("所有环境变量示例:");
        _output.WriteLine(result);
        
        // 验证所有变量都被替换
        Assert.DoesNotContain("@datetime", result);
        Assert.DoesNotContain("@date", result);
        Assert.DoesNotContain("@user", result);
    }
}
