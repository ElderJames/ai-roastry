using LY.LlmPool.Web.Services;
using Xunit;
using System.Collections.Generic;

namespace LY.LlmPool.Web.Tests;

public class PromptParameterServiceTests
{
    private readonly PromptParameterService _service = new();

    [Fact]
    public void ExtractParameters_WithSimpleParameters_ReturnsParameterNames()
    {
        // Arrange
        var template = "Hello {{name}}, your age is {{age}}";

        // Act
        var parameters = _service.ExtractParameters(template);

        // Assert
        Assert.Equal(2, parameters.Count);
        Assert.Contains(parameters, p => p.Name == "name" && p.Description == null);
        Assert.Contains(parameters, p => p.Name == "age" && p.Description == null);
    }

    [Fact]
    public void ExtractParameters_WithDescriptions_ReturnsParametersWithDescriptions()
    {
        // Arrange
        var template = "Hello {{name|用户姓名}}, your age is {{age|用户年龄}}";

        // Act
        var parameters = _service.ExtractParameters(template);

        // Assert
        Assert.Equal(2, parameters.Count);
        Assert.Contains(parameters, p => p.Name == "name" && p.Description == "用户姓名");
        Assert.Contains(parameters, p => p.Name == "age" && p.Description == "用户年龄");
    }

    [Fact]
    public void ExtractParameters_WithMixedFormat_ReturnsBothTypes()
    {
        // Arrange
        var template = "Hello {{name|用户姓名}}, your age is {{age}}, city: {{city|所在城市}}";

        // Act
        var parameters = _service.ExtractParameters(template);

        // Assert
        Assert.Equal(3, parameters.Count);
        Assert.Contains(parameters, p => p.Name == "name" && p.Description == "用户姓名");
        Assert.Contains(parameters, p => p.Name == "age" && p.Description == null);
        Assert.Contains(parameters, p => p.Name == "city" && p.Description == "所在城市");
    }

    [Fact]
    public void ExtractParameters_WithDuplicateParameters_PrefersDescribedVersion()
    {
        // Arrange
        var template = "{{name}} and {{name|用户姓名}}";

        // Act
        var parameters = _service.ExtractParameters(template);

        // Assert
        Assert.Single(parameters);
        Assert.Equal("name", parameters[0].Name);
        Assert.Equal("用户姓名", parameters[0].Description);
    }

    [Fact]
    public void ExtractParameterNames_BackwardCompatibility_StillWorks()
    {
        // Arrange
        var template = "Hello {{name|用户姓名}}, your age is {{age}}";

        // Act
        var parameterNames = _service.ExtractParameterNames(template);

        // Assert
        Assert.Equal(2, parameterNames.Count);
        Assert.Contains("name", parameterNames);
        Assert.Contains("age", parameterNames);
    }

    [Fact]
    public void ReplaceParameters_WithDescriptions_ReplacesCorrectly()
    {
        // Arrange
        var template = "Hello {{name|用户姓名}}, your age is {{age|年龄}}";
        var parameters = new Dictionary<string, object>
        {
            { "name", "张三" },
            { "age", 25 }
        };

        // Act
        var result = _service.ReplaceParameters(template, parameters);

        // Assert
        Assert.Equal("Hello 张三, your age is 25", result);
    }

    [Fact]
    public void ReplaceParameters_WithComplexDescription_HandlesCorrectly()
    {
        // Arrange
        var template = "{{query|用户的搜索查询,可以包含任何文本}}";
        var parameters = new Dictionary<string, object>
        {
            { "query", "最新的AI技术" }
        };

        // Act
        var result = _service.ReplaceParameters(template, parameters);

        // Assert
        Assert.Equal("最新的AI技术", result);
    }
}
