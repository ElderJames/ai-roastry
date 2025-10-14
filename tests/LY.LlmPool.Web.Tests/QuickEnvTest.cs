using Xunit;
using LY.LlmPool.Web.Services;

namespace LY.LlmPool.Web.Tests;

/// <summary>
/// 快速验证环境变量服务的基本功能
/// </summary>
public class QuickEnvTest
{
    [Fact]
    public void BasicEnvironmentVariablesWork()
    {
        var service = new PromptEnvironmentService();
        var input = "Today is @date at @time by @user";
        var result = service.ReplaceEnvironmentVariables(input);
        
        // 验证变量已被替换(不再包含原始的@变量)
        Assert.DoesNotContain("@date", result);
        Assert.DoesNotContain("@time", result);
        Assert.DoesNotContain("@user", result);
    }

    [Fact]
    public void DatetimeShowsFullInformation()
    {
        var service = new PromptEnvironmentService();
        var input = "Now: @datetime";
        var result = service.ReplaceEnvironmentVariables(input);
        
        // @datetime 应包含完整信息: 日期、时间、星期、时区
        Assert.DoesNotContain("@datetime", result);
        Assert.Contains("星期", result);
        Assert.Contains("UTC", result);
        // 格式: 2024-10-13 15:30:45 星期日 UTC+08:00
    }

    [Fact]
    public void CustomFormatsWork()
    {
        var service = new PromptEnvironmentService();
        var input = "Date: @datetime:yyyy/MM/dd";
        var result = service.ReplaceEnvironmentVariables(input);
        
        // 验证格式已被应用(应该包含斜杠)
        Assert.Contains("/", result);
        Assert.DoesNotContain("@datetime", result);
    }

    [Fact]
    public void GuidAndRandomWork()
    {
        var service = new PromptEnvironmentService();
        var input = "ID: @guid, Random: @random:100";
        var result = service.ReplaceEnvironmentVariables(input);
        
        // 验证已替换
        Assert.DoesNotContain("@guid", result);
        Assert.DoesNotContain("@random", result);
        Assert.Contains("ID:", result);
    }

    [Fact]
    public void CaseInsensitiveWorks()
    {
        var service = new PromptEnvironmentService();
        var input = "@DATE @Time @USER";
        var result = service.ReplaceEnvironmentVariables(input);
        
        // 所有大小写都应该被替换
        Assert.DoesNotContain("@DATE", result);
        Assert.DoesNotContain("@Time", result);
        Assert.DoesNotContain("@USER", result);
    }

    [Fact]
    public void UnknownVariablesAreKept()
    {
        var service = new PromptEnvironmentService();
        var input = "Unknown: @unknown_variable";
        var result = service.ReplaceEnvironmentVariables(input);
        
        // 未知变量应保持原样
        Assert.Contains("@unknown_variable", result);
    }
}
