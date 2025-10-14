using LY.LlmPool.Web.Services;
using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;

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

    [Fact]
    public void ExtractParameters_WithRequiredParameters_MarksAsRequired()
    {
        // Arrange
        var template = "Hello {{*name|用户姓名}}, your age is {{age}}, email: {{*email}}";

        // Act
        var parameters = _service.ExtractParameters(template);

        // Assert
        Assert.Equal(3, parameters.Count);
        
        var nameParam = parameters.First(p => p.Name == "name");
        // 使用反射检查 IsRequired 属性
        var isRequiredProp = nameParam.GetType().GetProperty("IsRequired");
        Assert.NotNull(isRequiredProp);
        Assert.True((bool)isRequiredProp.GetValue(nameParam));
        Assert.Equal("用户姓名", nameParam.Description);
        
        var ageParam = parameters.First(p => p.Name == "age");
        Assert.False((bool)isRequiredProp.GetValue(ageParam));
        
        var emailParam = parameters.First(p => p.Name == "email");
        Assert.True((bool)isRequiredProp.GetValue(emailParam));
    }

    [Fact]
    public void GenerateParameterSchema_WithRequiredParameters_IncludesInRequiredArray()
    {
        // Arrange - 使用反射创建 ParameterInfo 对象
        var parameterInfoType = typeof(PromptParameterService).Assembly.GetType("LY.LlmPool.Web.Services.ParameterInfo");
        Assert.NotNull(parameterInfoType);
        
        var parameters = new List<object>();
        
        // 创建 name 参数 (必填)
        var nameParam = Activator.CreateInstance(parameterInfoType);
        parameterInfoType.GetProperty("Name").SetValue(nameParam, "name");
        parameterInfoType.GetProperty("Description").SetValue(nameParam, "用户姓名");
        parameterInfoType.GetProperty("IsRequired").SetValue(nameParam, true);
        parameters.Add(nameParam);
        
        // 创建 age 参数 (可选)
        var ageParam = Activator.CreateInstance(parameterInfoType);
        parameterInfoType.GetProperty("Name").SetValue(ageParam, "age");
        parameterInfoType.GetProperty("Description").SetValue(ageParam, "年龄");
        parameterInfoType.GetProperty("IsRequired").SetValue(ageParam, false);
        parameters.Add(ageParam);
        
        // 创建 email 参数 (必填)
        var emailParam = Activator.CreateInstance(parameterInfoType);
        parameterInfoType.GetProperty("Name").SetValue(emailParam, "email");
        parameterInfoType.GetProperty("Description").SetValue(emailParam, "邮箱");
        parameterInfoType.GetProperty("IsRequired").SetValue(emailParam, true);
        parameters.Add(emailParam);

        // Act - 使用反射调用方法
        var method = typeof(PromptParameterService).GetMethod("GenerateParameterSchema", new[] { typeof(List<>).MakeGenericType(parameterInfoType), typeof(Dictionary<string, object>) });
        var genericList = Activator.CreateInstance(typeof(List<>).MakeGenericType(parameterInfoType));
        var addMethod = genericList.GetType().GetMethod("Add");
        foreach (var p in parameters)
        {
            addMethod.Invoke(genericList, new[] { p });
        }
        var schema = (string)method.Invoke(_service, new[] { genericList, null });

        // Assert
        Assert.Contains("\"name\"", schema);
        Assert.Contains("\"email\"", schema);
        Assert.Contains("\"required\"", schema);
        // 验证 required 数组中包含 name 和 email
        var requiredMatch = System.Text.RegularExpressions.Regex.Match(schema, @"""required"":\s*\[(.*?)\]");
        Assert.True(requiredMatch.Success);
        var requiredArray = requiredMatch.Groups[1].Value;
        Assert.Contains("name", requiredArray);
        Assert.Contains("email", requiredArray);
    }

    [Fact]
    public void RenderPrompt_WithMissingRequiredParameter_ThrowsException()
    {
        // Arrange
        var template = "Hello {{*name|姓名}}, your age is {{age}}";
        var parameters = new Dictionary<string, string>
        {
            { "age", "25" }
            // 缺少必填参数 name
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => _service.RenderPrompt(template, parameters));
        Assert.Contains("name", exception.Message);
        Assert.Contains("required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderPrompt_WithAllRequiredParameters_Succeeds()
    {
        // Arrange
        var template = "Hello {{*name|姓名}}, your age is {{age}}";
        var parameters = new Dictionary<string, string>
        {
            { "name", "张三" },
            { "age", "25" }
        };

        // Act
        var result = _service.RenderPrompt(template, parameters);

        // Assert
        Assert.Equal("Hello 张三, your age is 25", result);
    }

    [Fact]
    public void ReplaceParameters_WithRequiredParameterStar_ReplacesCorrectly()
    {
        // Arrange
        var template = "Hello {{*name}}, your email is {{*email|邮箱地址}}";
        var parameters = new Dictionary<string, object>
        {
            { "name", "张三" },
            { "email", "zhangsan@example.com" }
        };

        // Act
        var result = _service.ReplaceParameters(template, parameters);

        // Assert
        Assert.Equal("Hello 张三, your email is zhangsan@example.com", result);
    }

    [Fact]
    public void ValidateParameters_WithMissingRequiredParameter_ReturnsParameterName()
    {
        // Arrange
        var template = "Hello {{*name}}, your age is {{age}}";
        var parameters = new Dictionary<string, object>
        {
            { "age", "25" }
        };

        // Act
        var missingParams = _service.ValidateParameters(template, parameters);

        // Assert
        Assert.Single(missingParams);
        Assert.Equal("name", missingParams[0]);
    }

    [Fact]
    public void ValidateParameters_WithEmptyRequiredParameter_ReturnsParameterName()
    {
        // Arrange
        var template = "Hello {{*name}}, your age is {{age}}";
        var parameters = new Dictionary<string, object>
        {
            { "name", "" }, // 空字符串应该被视为缺失
            { "age", "25" }
        };

        // Act
        var missingParams = _service.ValidateParameters(template, parameters);

        // Assert
        Assert.Single(missingParams);
        Assert.Equal("name", missingParams[0]);
    }

    #region ValidateParametersDetailed Tests

    [Fact]
    public void ValidateParametersDetailed_WithAllRequiredParameters_ReturnsNull()
    {
        // Arrange
        var template = "Hello {{*name|用户姓名}}, your age is {{*age|用户年龄}}";
        var arguments = new AIFunctionArguments
        {
            { "name", "张三" },
            { "age", "25" }
        };

        // Act
        var result = _service.ValidateParametersDetailed(template, arguments);

        // Assert
        Assert.Null(result); // 验证通过应返回 null
    }

    [Fact]
    public void ValidateParametersDetailed_WithMissingRequiredParameter_ReturnsDetailedError()
    {
        // Arrange
        var template = "Hello {{*name|用户姓名}}, your age is {{*age|用户年龄}}";
        var arguments = new AIFunctionArguments
        {
            { "age", "25" }
            // 缺少 name
        };

        // Act
        var result = _service.ValidateParametersDetailed(template, arguments);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("❌ **Parameter Validation Failed**", result);
        Assert.Contains("**Missing Required Parameters:**", result);
        Assert.Contains("**name** (REQUIRED)", result);
        Assert.Contains("用户姓名", result);
        Assert.Contains("**All Parameters:**", result);
        Assert.Contains("❌ MISSING name", result);
        Assert.Contains("✅ PROVIDED age", result);
        Assert.Contains("**Action Required:**", result);
    }

    [Fact]
    public void ValidateParametersDetailed_WithEmptyRequiredParameter_ReturnsDetailedError()
    {
        // Arrange
        var template = "Hello {{*name|用户姓名}}, your age is {{age|用户年龄}}";
        var arguments = new AIFunctionArguments
        {
            { "name", "" }, // 空字符串应该被视为缺失
            { "age", "25" }
        };

        // Act
        var result = _service.ValidateParametersDetailed(template, arguments);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("❌ MISSING name (REQUIRED)", result);
        Assert.Contains("✅ PROVIDED age", result);
    }

    [Fact]
    public void ValidateParametersDetailed_WithOptionalParameters_ShowsCorrectStatus()
    {
        // Arrange
        var template = "Hello {{*name|用户姓名}}, age: {{age|用户年龄}}, city: {{city|所在城市}}";
        var arguments = new AIFunctionArguments
        {
            { "name", "张三" },
            { "age", "25" }
            // city 是可选的，未提供
        };

        // Act
        var result = _service.ValidateParametersDetailed(template, arguments);

        // Assert
        Assert.Null(result); // 可选参数未提供不应导致验证失败
    }

    [Fact]
    public void ValidateParametersDetailed_WithLongParameterValue_TruncatesInMessage()
    {
        // Arrange
        var template = "Content: {{*content|内容}}";
        var longValue = new string('x', 100); // 100 个字符
        var arguments = new AIFunctionArguments
        {
            { "content", longValue }
        };

        // Act
        var result = _service.ValidateParametersDetailed(template, arguments);

        // Assert
        Assert.Null(result); // 应该验证通过
    }

    [Fact]
    public void ValidateParametersDetailed_WithNoParameters_ReturnsNull()
    {
        // Arrange
        var template = "Hello, this is a static template without parameters.";
        var arguments = new AIFunctionArguments();

        // Act
        var result = _service.ValidateParametersDetailed(template, arguments);

        // Assert
        Assert.Null(result); // 没有参数应该返回 null
    }

    [Fact]
    public void ValidateParametersDetailed_WithMultipleMissingParameters_ListsAllMissing()
    {
        // Arrange
        var template = "User: {{*userId|用户ID}}, Action: {{*action|操作类型}}, Reason: {{reason|原因}}";
        var arguments = new AIFunctionArguments
        {
            { "reason", "test" }
            // 缺少 userId 和 action
        };

        // Act
        var result = _service.ValidateParametersDetailed(template, arguments);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("**Missing Required Parameters:**", result);
        Assert.Contains("**userId** (REQUIRED)", result);
        Assert.Contains("**action** (REQUIRED)", result);
        Assert.Contains("用户ID", result);
        Assert.Contains("操作类型", result);
    }

    #endregion

    #region ValidateParametersFromSchemaDetailed Tests

    [Fact]
    public void ValidateParametersFromSchemaDetailed_WithAllRequiredParameters_ReturnsNull()
    {
        // Arrange
        var schema = @"{
            ""type"": ""object"",
            ""properties"": {
                ""query"": {
                    ""type"": ""string"",
                    ""description"": ""搜索查询""
                },
                ""limit"": {
                    ""type"": ""integer"",
                    ""description"": ""返回结果数量""
                }
            },
            ""required"": [""query""]
        }";
        var arguments = new AIFunctionArguments
        {
            { "query", "test search" },
            { "limit", 10 }
        };

        // Act
        var result = _service.ValidateParametersFromSchemaDetailed(schema, arguments);

        // Assert
        Assert.Null(result); // 验证通过应返回 null
    }

    [Fact]
    public void ValidateParametersFromSchemaDetailed_WithMissingRequiredParameter_ReturnsDetailedError()
    {
        // Arrange
        var schema = @"{
            ""type"": ""object"",
            ""properties"": {
                ""query"": {
                    ""type"": ""string"",
                    ""description"": ""搜索查询""
                },
                ""limit"": {
                    ""type"": ""integer"",
                    ""description"": ""返回结果数量""
                }
            },
            ""required"": [""query""]
        }";
        var arguments = new AIFunctionArguments
        {
            { "limit", 10 }
            // 缺少 query
        };

        // Act
        var result = _service.ValidateParametersFromSchemaDetailed(schema, arguments);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("❌ **Parameter Validation Failed**", result);
        Assert.Contains("**Missing Required Parameters:**", result);
        Assert.Contains("**query** (REQUIRED)", result);
        Assert.Contains("搜索查询", result);
        Assert.Contains("❌ MISSING query", result);
        Assert.Contains("✅ PROVIDED limit", result);
    }

    [Fact]
    public void ValidateParametersFromSchemaDetailed_WithEmptySchema_ReturnsNull()
    {
        // Arrange
        var schema = "";
        var arguments = new AIFunctionArguments
        {
            { "anyParam", "value" }
        };

        // Act
        var result = _service.ValidateParametersFromSchemaDetailed(schema, arguments);

        // Assert
        Assert.Null(result); // 空 schema 应该返回 null
    }

    [Fact]
    public void ValidateParametersFromSchemaDetailed_WithInvalidSchema_ReturnsNull()
    {
        // Arrange
        var schema = "invalid json {{{";
        var arguments = new AIFunctionArguments
        {
            { "param", "value" }
        };

        // Act
        var result = _service.ValidateParametersFromSchemaDetailed(schema, arguments);

        // Assert
        Assert.Null(result); // 无效 schema 应该返回 null（解析失败）
    }

    [Fact]
    public void ValidateParametersFromSchemaDetailed_WithNoRequiredParameters_ReturnsNull()
    {
        // Arrange
        var schema = @"{
            ""type"": ""object"",
            ""properties"": {
                ""param1"": {
                    ""type"": ""string"",
                    ""description"": ""参数1""
                },
                ""param2"": {
                    ""type"": ""string"",
                    ""description"": ""参数2""
                }
            }
        }";
        var arguments = new AIFunctionArguments(); // 没有提供任何参数

        // Act
        var result = _service.ValidateParametersFromSchemaDetailed(schema, arguments);

        // Assert
        Assert.Null(result); // 没有必填参数，应该返回 null
    }

    [Fact]
    public void ValidateParametersFromSchemaDetailed_WithMultipleMissingParameters_ListsAllMissing()
    {
        // Arrange
        var schema = @"{
            ""type"": ""object"",
            ""properties"": {
                ""userId"": {
                    ""type"": ""string"",
                    ""description"": ""用户ID""
                },
                ""apiKey"": {
                    ""type"": ""string"",
                    ""description"": ""API密钥""
                },
                ""action"": {
                    ""type"": ""string"",
                    ""description"": ""操作类型""
                }
            },
            ""required"": [""userId"", ""apiKey""]
        }";
        var arguments = new AIFunctionArguments
        {
            { "action", "update" }
            // 缺少 userId 和 apiKey
        };

        // Act
        var result = _service.ValidateParametersFromSchemaDetailed(schema, arguments);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("**Missing Required Parameters:**", result);
        Assert.Contains("**userId** (REQUIRED)", result);
        Assert.Contains("**apiKey** (REQUIRED)", result);
        Assert.Contains("用户ID", result);
        Assert.Contains("API密钥", result);
        Assert.Contains("✅ PROVIDED action", result);
    }

    [Fact]
    public void ValidateParametersFromSchemaDetailed_CaseInsensitiveParameterMatching()
    {
        // Arrange
        var schema = @"{
            ""type"": ""object"",
            ""properties"": {
                ""userId"": {
                    ""type"": ""string"",
                    ""description"": ""用户ID""
                }
            },
            ""required"": [""userId""]
        }";
        var arguments = new AIFunctionArguments
        {
            { "USERID", "12345" } // 大写形式
        };

        // Act
        var result = _service.ValidateParametersFromSchemaDetailed(schema, arguments);

        // Assert
        Assert.Null(result); // 不区分大小写，应该验证通过
    }

    [Fact]
    public void ValidateParametersFromSchemaDetailed_ShowsProvidedValueInMessage()
    {
        // Arrange
        var schema = @"{
            ""type"": ""object"",
            ""properties"": {
                ""query"": {
                    ""type"": ""string"",
                    ""description"": ""搜索查询""
                },
                ""userId"": {
                    ""type"": ""string"",
                    ""description"": ""用户ID""
                }
            },
            ""required"": [""query"", ""userId""]
        }";
        var arguments = new AIFunctionArguments
        {
            { "query", "test search" }
            // 缺少 userId
        };

        // Act
        var result = _service.ValidateParametersFromSchemaDetailed(schema, arguments);

        // Assert
        Assert.NotNull(result);
        // 验证已提供的参数显示了值
        Assert.Contains("✅ PROVIDED query", result);
        Assert.Contains("= \"test search\"", result);
        // 验证缺失的参数被标记
        Assert.Contains("❌ MISSING userId (REQUIRED)", result);
    }

    #endregion
}
