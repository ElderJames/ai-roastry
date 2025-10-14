using System.Collections.Generic;
using LY.LlmPool.Web.Services;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Xunit;

namespace LY.LlmPool.Web.Tests;

public class ModelParameterTests
{
    [Fact]
    public void ParseFromJson_ValidJson_ReturnsParameters()
    {
        // Arrange
        var json = @"{""max_tokens"": 2000, ""temperature"": 0.7, ""top_p"": 0.9, ""thinking_enabled"": true}";

        // Act
        var result = ModelParameterHelper.ParseFromJson(json);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(4, result.Count);
        
        // 检查类型和值
        var maxTokens = result["max_tokens"];
        Assert.IsType<int>(maxTokens);
        Assert.Equal(2000, (int)maxTokens);
        
        var temperature = result["temperature"];
        Assert.IsType<double>(temperature);
        Assert.Equal(0.7, (double)temperature);
        
        var topP = result["top_p"];
        Assert.IsType<double>(topP);
        Assert.Equal(0.9, (double)topP);
        
        var thinkingEnabled = result["thinking_enabled"];
        Assert.IsType<bool>(thinkingEnabled);
        Assert.True((bool)thinkingEnabled);
    }

    [Fact]
    public void ParseFromJson_InvalidJson_ReturnsNull()
    {
        // Arrange
        var json = @"{invalid json}";

        // Act
        var result = ModelParameterHelper.ParseFromJson(json);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseFromJson_EmptyString_ReturnsNull()
    {
        // Act
        var result = ModelParameterHelper.ParseFromJson("");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseFromJson_Null_ReturnsNull()
    {
        // Act
        var result = ModelParameterHelper.ParseFromJson(null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseFromKeyValueString_ValidString_ReturnsParameters()
    {
        // Arrange
        var input = "temperature=0.7,max_tokens=100,top_p=0.9";

        // Act
        var result = ModelParameterHelper.ParseFromKeyValueString(input);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal(0.7, result["temperature"]);
        Assert.Equal(100, result["max_tokens"]);
        Assert.Equal(0.9, result["top_p"]);
    }

    [Fact]
    public void ParseFromKeyValueString_DifferentSeparators_ReturnsParameters()
    {
        // Arrange
        var input = "temp:0.5;tokens=200";

        // Act
        var result = ModelParameterHelper.ParseFromKeyValueString(input);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(0.5, result["temp"]);
        Assert.Equal(200, result["tokens"]);
    }

    [Fact]
    public void ParseFromKeyValueString_BooleanValues_ReturnsParameters()
    {
        // Arrange
        var input = "thinking_enabled=true,verbose=false";

        // Act
        var result = ModelParameterHelper.ParseFromKeyValueString(input);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(true, result["thinking_enabled"]);
        Assert.Equal(false, result["verbose"]);
    }

    [Fact]
    public void ParseFromKeyValueString_EmptyString_ReturnsNull()
    {
        // Act
        var result = ModelParameterHelper.ParseFromKeyValueString("");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Merge_MultipleDictionaries_MergesCorrectly()
    {
        // Arrange
        var dict1 = new Dictionary<string, object> { ["temperature"] = 0.5, ["max_tokens"] = 100 };
        var dict2 = new Dictionary<string, object> { ["temperature"] = 0.7, ["top_p"] = 0.9 };
        var dict3 = new Dictionary<string, object> { ["custom"] = "value" };

        // Act
        var result = ModelParameterHelper.Merge(dict1, dict2, dict3);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(4, result.Count);
        Assert.Equal(0.7, result["temperature"]); // dict2 覆盖 dict1
        Assert.Equal(100, result["max_tokens"]); // 来自 dict1
        Assert.Equal(0.9, result["top_p"]); // 来自 dict2
        Assert.Equal("value", result["custom"]); // 来自 dict3
    }

    [Fact]
    public void Merge_WithNullDictionaries_IgnoresNull()
    {
        // Arrange
        var dict1 = new Dictionary<string, object> { ["temperature"] = 0.5 };

        // Act
        var result = ModelParameterHelper.Merge(dict1, null, dict1);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(0.5, result["temperature"]);
    }

    [Fact]
    public void Merge_AllNull_ReturnsNull()
    {
        // Act
        var result = ModelParameterHelper.Merge(null, null, null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ValidateJson_ValidJson_ReturnsTrue()
    {
        // Arrange
        var json = @"{""max_tokens"": 2000, ""temperature"": 0.7}";

        // Act
        var isValid = ModelParameterHelper.ValidateJson(json, out var errorMessage);

        // Assert
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidateJson_InvalidJson_ReturnsFalse()
    {
        // Arrange
        var json = @"{invalid}";

        // Act
        var isValid = ModelParameterHelper.ValidateJson(json, out var errorMessage);

        // Assert
        Assert.False(isValid);
        Assert.NotNull(errorMessage);
        Assert.Contains("Invalid JSON", errorMessage);
    }

    [Fact]
    public void ValidateJson_NotAnObject_ReturnsFalse()
    {
        // Arrange
        var json = @"[1, 2, 3]"; // Array instead of object

        // Act
        var isValid = ModelParameterHelper.ValidateJson(json, out var errorMessage);

        // Assert
        Assert.False(isValid);
        Assert.Equal("Must be a JSON object", errorMessage);
    }

    [Fact]
    public void ValidateJson_EmptyString_ReturnsTrue()
    {
        // Act
        var isValid = ModelParameterHelper.ValidateJson("", out var errorMessage);

        // Assert
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidateJson_Null_ReturnsTrue()
    {
        // Act
        var isValid = ModelParameterHelper.ValidateJson(null, out var errorMessage);

        // Assert
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ApplyToExecutionSettings_Temperature_AppliesCorrectly()
    {
        // Arrange
        var settings = new OpenAIPromptExecutionSettings();
        var parameters = new Dictionary<string, object> { ["temperature"] = 0.8 };

        // Act
        ModelParameterHelper.ApplyToExecutionSettings(settings, parameters);

        // Assert
        Assert.Equal(0.8, settings.Temperature);
    }

    [Fact]
    public void ApplyToExecutionSettings_MaxTokens_AppliesCorrectly()
    {
        // Arrange
        var settings = new OpenAIPromptExecutionSettings();
        var parameters = new Dictionary<string, object> { ["max_tokens"] = 1500 };

        // Act
        ModelParameterHelper.ApplyToExecutionSettings(settings, parameters);

        // Assert
        Assert.Equal(1500, settings.MaxTokens);
    }

    [Fact]
    public void ApplyToExecutionSettings_TopP_AppliesCorrectly()
    {
        // Arrange
        var settings = new OpenAIPromptExecutionSettings();
        var parameters = new Dictionary<string, object> { ["top_p"] = 0.95 };

        // Act
        ModelParameterHelper.ApplyToExecutionSettings(settings, parameters);

        // Assert
        Assert.Equal(0.95, settings.TopP);
    }

    [Fact]
    public void ApplyToExecutionSettings_AllParameters_AppliesCorrectly()
    {
        // Arrange
        var settings = new OpenAIPromptExecutionSettings();
        var parameters = new Dictionary<string, object>
        {
            ["temperature"] = 0.7,
            ["max_tokens"] = 2000,
            ["top_p"] = 0.9
        };

        // Act
        ModelParameterHelper.ApplyToExecutionSettings(settings, parameters);

        // Assert
        Assert.Equal(0.7, settings.Temperature);
        Assert.Equal(2000, settings.MaxTokens);
        Assert.Equal(0.9, settings.TopP);
    }

    [Fact]
    public void ApplyToExecutionSettings_AlternativeNames_AppliesCorrectly()
    {
        // Arrange
        var settings = new OpenAIPromptExecutionSettings();
        var parameters = new Dictionary<string, object>
        {
            ["temp"] = 0.6,          // alternative for temperature
            ["tokens"] = 1000,       // alternative for max_tokens
            ["topp"] = 0.85         // alternative for top_p
        };

        // Act
        ModelParameterHelper.ApplyToExecutionSettings(settings, parameters);

        // Assert
        Assert.Equal(0.6, settings.Temperature);
        Assert.Equal(1000, settings.MaxTokens);
        Assert.Equal(0.85, settings.TopP);
    }

    [Fact]
    public void ApplyToExecutionSettings_StringNumbers_ParsesCorrectly()
    {
        // Arrange
        var settings = new OpenAIPromptExecutionSettings();
        var parameters = new Dictionary<string, object>
        {
            ["temperature"] = "0.5",
            ["max_tokens"] = "500"
        };

        // Act
        ModelParameterHelper.ApplyToExecutionSettings(settings, parameters);

        // Assert
        Assert.Equal(0.5, settings.Temperature);
        Assert.Equal(500, settings.MaxTokens);
    }

    [Fact]
    public void ApplyToExecutionSettings_NullParameters_DoesNotThrow()
    {
        // Arrange
        var settings = new OpenAIPromptExecutionSettings();

        // Act & Assert - should not throw
        ModelParameterHelper.ApplyToExecutionSettings(settings, null);
    }

    [Fact]
    public void ApplyToExecutionSettings_EmptyDictionary_DoesNotThrow()
    {
        // Arrange
        var settings = new OpenAIPromptExecutionSettings();
        var parameters = new Dictionary<string, object>();

        // Act & Assert - should not throw
        ModelParameterHelper.ApplyToExecutionSettings(settings, parameters);
    }

    [Fact]
    public void Integration_ParseJsonAndApply_WorksEndToEnd()
    {
        // Arrange
        var json = @"{""max_tokens"": 1500, ""temperature"": 0.6, ""top_p"": 0.92}";
        var settings = new OpenAIPromptExecutionSettings();

        // Act
        var parameters = ModelParameterHelper.ParseFromJson(json);
        ModelParameterHelper.ApplyToExecutionSettings(settings, parameters);

        // Assert
        Assert.Equal(1500, settings.MaxTokens);
        Assert.Equal(0.6, settings.Temperature);
        Assert.Equal(0.92, settings.TopP);
    }

    [Fact]
    public void Integration_ParseKeyValueStringAndApply_WorksEndToEnd()
    {
        // Arrange
        var input = "temp=0.3,tokens=800,top_p=0.88";
        var settings = new OpenAIPromptExecutionSettings();

        // Act
        var parameters = ModelParameterHelper.ParseFromKeyValueString(input);
        ModelParameterHelper.ApplyToExecutionSettings(settings, parameters);

        // Assert
        Assert.Equal(0.3, settings.Temperature);
        Assert.Equal(800, settings.MaxTokens);
        Assert.Equal(0.88, settings.TopP);
    }

    [Fact]
    public void Integration_MergeAndApply_WorksEndToEnd()
    {
        // Arrange
        var configParams = new Dictionary<string, object> { ["temperature"] = 0.5, ["max_tokens"] = 1000 };
        var promptParams = new Dictionary<string, object> { ["temperature"] = 0.7 }; // Override
        var settings = new OpenAIPromptExecutionSettings();

        // Act
        var merged = ModelParameterHelper.Merge(configParams, promptParams);
        ModelParameterHelper.ApplyToExecutionSettings(settings, merged);

        // Assert
        Assert.Equal(0.7, settings.Temperature); // Prompt overrides config
        Assert.Equal(1000, settings.MaxTokens);  // From config
    }
}
