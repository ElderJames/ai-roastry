using LY.LlmPool.Web.Services;
using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace LY.LlmPool.Web.Tests;

/// <summary>
/// Tests for PromptParameterService's GenerateParameterSchema and RenderPrompt methods
/// (Previously tested via PromptParameterExtractor which has been merged)
/// </summary>
public class PromptParameterExtractorTests
{
    private readonly PromptParameterService _service;

    public PromptParameterExtractorTests()
    {
        _service = new PromptParameterService();
    }

    #region GenerateParameterSchema Tests

    [Fact]
    public void GenerateParameterSchema_DefaultType_GeneratesStringSchema()
    {
        // Arrange
        var parameters = new List<string> { "name", "age" };

        // Act
        var schemaJson = _service.GenerateParameterSchema(parameters);
        var schema = JsonSerializer.Deserialize<JsonElement>(schemaJson);

        // Assert
        Assert.Equal("object", schema.GetProperty("type").GetString());
        
        var properties = schema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("age").GetProperty("type").GetString());

        var required = schema.GetProperty("required");
        Assert.Equal(2, required.GetArrayLength());
    }

    [Fact]
    public void GenerateParameterSchema_WithOverrides_AppliesCustomTypes()
    {
        // Arrange
        var parameters = new List<string> { "age", "isActive" };
        var overrides = new Dictionary<string, object>
        {
            ["age"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "User age in years",
                ["minimum"] = 0
            },
            ["isActive"] = new Dictionary<string, object>
            {
                ["type"] = "boolean",
                ["description"] = "Whether user is active"
            }
        };

        // Act
        var schemaJson = _service.GenerateParameterSchema(parameters, overrides);
        var schema = JsonSerializer.Deserialize<JsonElement>(schemaJson);

        // Assert
        var properties = schema.GetProperty("properties");
        
        var ageProperty = properties.GetProperty("age");
        Assert.Equal("integer", ageProperty.GetProperty("type").GetString());
        Assert.Equal("User age in years", ageProperty.GetProperty("description").GetString());

        var isActiveProperty = properties.GetProperty("isActive");
        Assert.Equal("boolean", isActiveProperty.GetProperty("type").GetString());
    }

    [Fact]
    public void GenerateParameterSchema_EmptyParameters_ReturnsEmptySchema()
    {
        // Arrange
        var parameters = new List<string>();

        // Act
        var schemaJson = _service.GenerateParameterSchema(parameters);
        var schema = JsonSerializer.Deserialize<JsonElement>(schemaJson);

        // Assert
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(0, schema.GetProperty("required").GetArrayLength());
    }

    [Fact]
    public void GenerateParameterSchema_WithJsonElementOverride_AppliesCorrectly()
    {
        // Arrange
        var parameters = new List<string> { "score" };
        var overrideJson = JsonSerializer.Serialize(new
        {
            type = "number",
            minimum = 0.0,
            maximum = 100.0
        });
        var overrides = new Dictionary<string, object>
        {
            ["score"] = JsonSerializer.Deserialize<JsonElement>(overrideJson)
        };

        // Act
        var schemaJson = _service.GenerateParameterSchema(parameters, overrides);
        var schema = JsonSerializer.Deserialize<JsonElement>(schemaJson);

        // Assert
        var scoreProperty = schema.GetProperty("properties").GetProperty("score");
        Assert.Equal("number", scoreProperty.GetProperty("type").GetString());
    }

    #endregion

    #region RenderPrompt Tests

    [Fact]
    public void RenderPrompt_ValidParameters_RendersCorrectly()
    {
        // Arrange
        var template = "Hello {{name}}, welcome to {{place}}!";
        var parameters = new Dictionary<string, string>
        {
            ["name"] = "Alice",
            ["place"] = "Wonderland"
        };

        // Act
        var result = _service.RenderPrompt(template, parameters);

        // Assert
        Assert.Equal("Hello Alice, welcome to Wonderland!", result);
    }

    [Fact]
    public void RenderPrompt_MultipleOccurrences_ReplacesAll()
    {
        // Arrange
        var template = "{{name}} said hello. {{name}} is happy.";
        var parameters = new Dictionary<string, string>
        {
            ["name"] = "Bob"
        };

        // Act
        var result = _service.RenderPrompt(template, parameters);

        // Assert
        Assert.Equal("Bob said hello. Bob is happy.", result);
    }

    [Fact]
    public void RenderPrompt_MissingParameter_ThrowsArgumentException()
    {
        // Arrange
        var template = "Hello {{name}}, you are {{age}} years old";
        var parameters = new Dictionary<string, string>
        {
            ["name"] = "Charlie"
            // Missing "age" parameter
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            _service.RenderPrompt(template, parameters)
        );
        Assert.Contains("Missing required parameters: age", exception.Message);
    }

    [Fact]
    public void RenderPrompt_EmptyTemplate_ThrowsArgumentException()
    {
        // Arrange
        var template = "";
        var parameters = new Dictionary<string, string>();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            _service.RenderPrompt(template, parameters)
        );
    }

    [Fact]
    public void RenderPrompt_NullParameters_ThrowsForMissingParams()
    {
        // Arrange
        var template = "Hello {{name}}";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            _service.RenderPrompt(template, null!)
        );
        Assert.Contains("Missing required parameters", exception.Message);
    }

    [Fact]
    public void RenderPrompt_CaseInsensitive_ReplacesCorrectly()
    {
        // Arrange
        var template = "Hello {{Name}} and {{NAME}}";
        var parameters = new Dictionary<string, string>
        {
            ["name"] = "David"
        };

        // Act
        var result = _service.RenderPrompt(template, parameters);

        // Assert
        Assert.Equal("Hello David and David", result);
    }

    [Fact]
    public void RenderPrompt_NestedParameters_RendersCorrectly()
    {
        // Arrange
        var template = "User {{user.name}} has email {{user.email}}";
        var parameters = new Dictionary<string, string>
        {
            ["user.name"] = "Eve",
            ["user.email"] = "eve@example.com"
        };

        // Act
        var result = _service.RenderPrompt(template, parameters);

        // Assert
        Assert.Equal("User Eve has email eve@example.com", result);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void EndToEnd_ExtractGenerateAndRender_WorksTogether()
    {
        // Arrange
        var template = "Translate '{{text}}' to {{language}}";

        // Step 1: Extract parameters (returning List<ParameterInfo>)
        var extractedParamInfos = _service.ExtractParameters(template);
        Assert.Equal(2, extractedParamInfos.Count);

        // Step 2: Generate schema from ParameterInfo list
        var schemaJson = _service.GenerateParameterSchema(extractedParamInfos);
        var schema = JsonSerializer.Deserialize<JsonElement>(schemaJson);
        Assert.Equal(2, schema.GetProperty("required").GetArrayLength());

        // Step 3: Render prompt
        var renderParams = new Dictionary<string, string>
        {
            ["text"] = "Hello",
            ["language"] = "Chinese"
        };
        var rendered = _service.RenderPrompt(template, renderParams);
        Assert.Equal("Translate 'Hello' to Chinese", rendered);
    }

    #endregion

    #region ParameterInfo Tests (Description Support)

    [Fact]
    public void GenerateParameterSchema_WithParameterInfo_IncludesDescription()
    {
        // Arrange
        var parameters = new List<ParameterInfo>
        {
            new ParameterInfo { Name = "name", Description = "用户名称" },
            new ParameterInfo { Name = "age", Description = "用户年龄" }
        };

        // Act
        var schemaJson = _service.GenerateParameterSchema(parameters, null);
        var schema = JsonDocument.Parse(schemaJson);

        // Assert
        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        
        var properties = schema.RootElement.GetProperty("properties");
        
        // 检查 name 参数的描述
        var nameProperty = properties.GetProperty("name");
        Assert.Equal("string", nameProperty.GetProperty("type").GetString());
        Assert.Equal("用户名称", nameProperty.GetProperty("description").GetString());
        
        // 检查 age 参数的描述
        var ageProperty = properties.GetProperty("age");
        Assert.Equal("string", ageProperty.GetProperty("type").GetString());
        Assert.Equal("用户年龄", ageProperty.GetProperty("description").GetString());
        
        // 检查 required 数组
        var required = schema.RootElement.GetProperty("required");
        Assert.Equal(2, required.GetArrayLength());
    }

    [Fact]
    public void GenerateParameterSchema_WithParameterInfoWithoutDescription_UsesDefaultDescription()
    {
        // Arrange
        var parameters = new List<ParameterInfo>
        {
            new ParameterInfo { Name = "query", Description = null }
        };

        // Act
        var schemaJson = _service.GenerateParameterSchema(parameters, null);
        var schema = JsonDocument.Parse(schemaJson);

        // Assert
        var properties = schema.RootElement.GetProperty("properties");
        var queryProperty = properties.GetProperty("query");
        
        // 没有描述时应使用默认格式
        Assert.Equal("Parameter: query", queryProperty.GetProperty("description").GetString());
    }

    [Fact]
    public void GenerateParameterSchema_WithEmptyDescription_UsesDefaultDescription()
    {
        // Arrange
        var parameters = new List<ParameterInfo>
        {
            new ParameterInfo { Name = "data", Description = "   " } // 空白描述
        };

        // Act
        var schemaJson = _service.GenerateParameterSchema(parameters, null);
        var schema = JsonDocument.Parse(schemaJson);

        // Assert
        var properties = schema.RootElement.GetProperty("properties");
        var dataProperty = properties.GetProperty("data");
        
        // 空白描述应使用默认格式
        Assert.Equal("Parameter: data", dataProperty.GetProperty("description").GetString());
    }

    [Fact]
    public void GenerateParameterSchema_WithParameterOverrides_OverridesDescription()
    {
        // Arrange
        var parameters = new List<ParameterInfo>
        {
            new ParameterInfo { Name = "userId", Description = "用户ID" }
        };
        
        var overrides = new Dictionary<string, object>
        {
            ["userId"] = new Dictionary<string, object>
            {
                ["type"] = "number",
                ["description"] = "被覆盖的用户ID描述"
            }
        };

        // Act
        var schemaJson = _service.GenerateParameterSchema(parameters, overrides);
        var schema = JsonDocument.Parse(schemaJson);

        // Assert
        var properties = schema.RootElement.GetProperty("properties");
        var userIdProperty = properties.GetProperty("userId");
        
        // Override 应该覆盖原始描述
        Assert.Equal("被覆盖的用户ID描述", userIdProperty.GetProperty("description").GetString());
        Assert.Equal("number", userIdProperty.GetProperty("type").GetString());
    }

    [Fact]
    public void GenerateParameterSchema_BackwardCompatibility_WithStringList()
    {
        // Arrange
        var stringParams = new List<string> { "param1", "param2" };

        // Act
        var schemaJson = _service.GenerateParameterSchema(stringParams, null);
        var schema = JsonDocument.Parse(schemaJson);

        // Assert
        var properties = schema.RootElement.GetProperty("properties");
        
        // 向后兼容: 字符串参数列表应该使用默认描述
        var param1Property = properties.GetProperty("param1");
        Assert.Equal("Parameter: param1", param1Property.GetProperty("description").GetString());
        
        var param2Property = properties.GetProperty("param2");
        Assert.Equal("Parameter: param2", param2Property.GetProperty("description").GetString());
    }

    #endregion
}

