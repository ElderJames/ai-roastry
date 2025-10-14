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
        var template = "Hello {{*name}}, you are {{*age}} years old";
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
        var template = "Hello {{*name}}";

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


    #endregion

    #region ParameterInfo Tests (Description Support)



    #endregion
}

