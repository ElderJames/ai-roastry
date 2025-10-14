using LY.LlmPool.Web.Services;
using Xunit;
using System;

namespace LY.LlmPool.Web.Tests;

public class PromptEnvironmentServiceTests
{
    private readonly PromptEnvironmentService _service = new();

    [Fact]
    public void ReplaceEnvironmentVariables_WithDatetime_ReplacesCorrectly()
    {
        // Arrange
        var template = "Current time is @datetime";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert - 验证包含完整信息: 日期、时间、星期、时区
        Assert.DoesNotContain("@datetime", result);
        Assert.Contains(DateTime.Now.Year.ToString(), result);
        Assert.Contains("星期", result); // 包含星期
        Assert.Contains("UTC", result); // 包含时区
        // 完整格式: 2024-10-13 15:30:45 星期日 UTC+08:00
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} 星期[一二三四五六日] UTC[+-]\d{2}:\d{2}", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithCustomDateFormat_ReplacesCorrectly()
    {
        // Arrange
        var template = "Date: @datetime:yyyy/MM/dd";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@datetime", result);
        Assert.Contains("/", result);
        Assert.Matches(@"\d{4}/\d{2}/\d{2}", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithDate_ReplacesCorrectly()
    {
        // Arrange
        var template = "Today is @date";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@date", result);
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"), result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithTime_ReplacesCorrectly()
    {
        // Arrange
        var template = "Time: @time";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@time", result);
        Assert.Matches(@"\d{2}:\d{2}:\d{2}", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithYear_ReplacesCorrectly()
    {
        // Arrange
        var template = "Year: @year";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.Equal($"Year: {DateTime.Now.Year}", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithWeekday_ReplacesCorrectly()
    {
        // Arrange
        var template = "Today is @weekday";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@weekday", result);
        Assert.Contains("星期", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithWeekdayEnglish_ReplacesCorrectly()
    {
        // Arrange
        var template = "Today is @weekday:en";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@weekday", result);
        Assert.Matches(@"Today is (Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithTimezone_ReplacesCorrectly()
    {
        // Arrange
        var template = "Timezone: @timezone";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@timezone", result);
        Assert.Matches(@"Timezone: [+\-]\d{2}:\d{2}", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithTimestamp_ReplacesCorrectly()
    {
        // Arrange
        var template = "Timestamp: @timestamp";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@timestamp", result);
        Assert.Matches(@"Timestamp: \d{10}", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithTimestampMs_ReplacesCorrectly()
    {
        // Arrange
        var template = "Timestamp MS: @timestamp_ms";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@timestamp_ms", result);
        Assert.Matches(@"Timestamp MS: \d{13}", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithUser_ReplacesCorrectly()
    {
        // Arrange
        var template = "User: @user";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@user", result);
        Assert.Contains(Environment.UserName, result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithCustomUser_ReplacesCorrectly()
    {
        // Arrange
        var template = "User: @user";
        var customUser = "TestUser";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template, customUser);
        
        // Assert
        Assert.Equal("User: TestUser", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithMachine_ReplacesCorrectly()
    {
        // Arrange
        var template = "Machine: @machine";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@machine", result);
        Assert.Contains(Environment.MachineName, result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithGuid_ReplacesCorrectly()
    {
        // Arrange
        var template = "ID: @guid";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@guid", result);
        Assert.Matches(@"ID: [a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithGuidNoHyphens_ReplacesCorrectly()
    {
        // Arrange
        var template = "ID: @guid:N";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@guid", result);
        Assert.Matches(@"ID: [a-f0-9]{32}", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithRandom_ReplacesCorrectly()
    {
        // Arrange
        var template = "Random: @random";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@random", result);
        Assert.Matches(@"Random: \d+", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithRandomRange_ReplacesCorrectly()
    {
        // Arrange
        var template = "Random: @random:1000";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@random", result);
        Assert.Matches(@"Random: \d+", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithMultipleVariables_ReplacesAll()
    {
        // Arrange
        var template = "Date: @date, Time: @time, User: @user";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@date", result);
        Assert.DoesNotContain("@time", result);
        Assert.DoesNotContain("@user", result);
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"), result);
        Assert.Contains(Environment.UserName, result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithUnknownVariable_KeepsOriginal()
    {
        // Arrange
        var template = "Unknown: @unknown_var";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.Equal("Unknown: @unknown_var", result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithEmptyString_ReturnsEmpty()
    {
        // Arrange
        var template = "";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void GetSupportedVariables_ReturnsAllVariables()
    {
        // Act
        var variables = _service.GetSupportedVariables();
        
        // Assert
        Assert.NotEmpty(variables);
        Assert.Contains("@datetime", variables.Keys);
        Assert.Contains("@date", variables.Keys);
        Assert.Contains("@user", variables.Keys);
        Assert.Contains("@guid", variables.Keys);
        Assert.Contains("@random", variables.Keys);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_WithUtc_ReplacesCorrectly()
    {
        // Arrange
        var template = "UTC: @utc";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@utc", result);
        Assert.Contains(DateTime.UtcNow.Year.ToString(), result);
    }

    [Fact]
    public void ReplaceEnvironmentVariables_CaseInsensitive_Works()
    {
        // Arrange - 测试大小写不敏感
        var template = "Date: @DATE, User: @USER, Time: @TIME";
        
        // Act
        var result = _service.ReplaceEnvironmentVariables(template);
        
        // Assert
        Assert.DoesNotContain("@DATE", result);
        Assert.DoesNotContain("@USER", result);
        Assert.DoesNotContain("@TIME", result);
    }
}
