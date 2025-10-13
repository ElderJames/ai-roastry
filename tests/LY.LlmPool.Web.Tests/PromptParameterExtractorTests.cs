using LY.LlmPool.Web.Services.Tools;
using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace LY.LlmPool.Web.Tests;

public class PromptParameterExtractorTests
{
    private readonly PromptParameterExtractor _extractor;

    public PromptParameterExtractorTests()
    {
        _extractor = new PromptParameterExtractor();
    }

    #region ExtractParameters Tests

    [Fact]
    public void ExtractParameters_SingleParameter_ReturnsCorrectParameter()
    {
        // Arrange
        var template = "Hello {{name}}";

        // Act
        var result = _extractor.ExtractParameters(template);

        // Assert
        Assert.Single(result);
        Assert.Contains("name", result);
    }

    [Fact]
    public void ExtractParameters_MultipleParameters_ReturnsAllParameters()
    {
        // Arrange
        var template = "{{greeting}} {{name}}, you are {{age}} years old";

        // Act
        var result = _extractor.ExtractParameters(template);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Contains("greeting", result);
        Assert.Contains("name", result);
        Assert.Contains("age", result);
    }

    [Fact]
    public void ExtractParameters_NestedParameter_ReturnsNestedParameter()
    {
        // Arrange
        var template = "User name is {{user.name}} and email is {{user.email}}";

        // Act
        var result = _extractor.ExtractParameters(template);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("user.name", result);
        Assert.Contains("user.email", result);
    }

    [Fact]
    public void ExtractParameters_DuplicateParameters_ReturnsDeduplicated()
    {
        // Arrange
        var template = "{{name}} {{name}} {{name}}";

        // Act
        var result = _extractor.ExtractParameters(template);

        // Assert
        Assert.Single(result);
        Assert.Contains("name", result);
    }

    [Fact]
    public void ExtractParameters_EmptyTemplate_ReturnsEmptyList()
    {
        // Arrange
        var template = "";

        // Act
        var result = _extractor.ExtractParameters(template);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractParameters_NoParameters_ReturnsEmptyList()
    {
        // Arrange
        var template = "This is a plain text without parameters";

        // Act
        var result = _extractor.ExtractParameters(template);

        // Assert
        Assert.Empty(result);
    }

    #endregion

    #region GenerateParameterSchema Tests

    [Fact]
    public void GenerateParameterSchema_DefaultType_GeneratesStringSchema()
    {
        // Arrange
        var parameters = new List<string> { "name", "age" };

        // Act
        var schemaJson = _extractor.GenerateParameterSchema(parameters);
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
        var schemaJson = _extractor.GenerateParameterSchema(parameters, overrides);
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
        var schemaJson = _extractor.GenerateParameterSchema(parameters);
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
        var schemaJson = _extractor.GenerateParameterSchema(parameters, overrides);
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
        var result = _extractor.RenderPrompt(template, parameters);

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
        var result = _extractor.RenderPrompt(template, parameters);

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
            _extractor.RenderPrompt(template, parameters)
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
            _extractor.RenderPrompt(template, parameters)
        );
    }

    [Fact]
    public void RenderPrompt_NullParameters_ThrowsForMissingParams()
    {
        // Arrange
        var template = "Hello {{name}}";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            _extractor.RenderPrompt(template, null!)
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
        var result = _extractor.RenderPrompt(template, parameters);

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
        var result = _extractor.RenderPrompt(template, parameters);

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

        // Step 1: Extract parameters
        var extractedParams = _extractor.ExtractParameters(template);
        Assert.Equal(2, extractedParams.Count);

        // Step 2: Generate schema
        var schemaJson = _extractor.GenerateParameterSchema(extractedParams);
        var schema = JsonSerializer.Deserialize<JsonElement>(schemaJson);
        Assert.Equal(2, schema.GetProperty("required").GetArrayLength());

        // Step 3: Render prompt
        var renderParams = new Dictionary<string, string>
        {
            ["text"] = "Hello",
            ["language"] = "Chinese"
        };
        var rendered = _extractor.RenderPrompt(template, renderParams);
        Assert.Equal("Translate 'Hello' to Chinese", rendered);
    }

    #endregion
}
