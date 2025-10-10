using System;
using System.Linq;
using LY.LlmPool.Web.Services;
using Xunit;
using System.Collections.Generic;

namespace LY.LlmPool.Web.Tests
{
    public class McpSchemaParserTests
    {
        [Fact]
        public void ParseCounts_standard_json()
        {
            var json = "{" + "\"tools\":[{},{},{}],\"prompts\":[{}]" + "}";
            var (t, p) = McpSchemaParser.ParseCounts(json);
            Assert.Equal(3, t);
            Assert.Equal(1, p);
        }

        [Fact]
        public void ParseCounts_invalid_returns_zero()
        {
            var (t, p) = McpSchemaParser.ParseCounts("not-json");
            Assert.Equal(0, t);
            Assert.Equal(0, p);
        }

        [Fact]
        public void GetTools_extracts_ids_and_names()
        {
            var json = "{" +
                "\"tools\":[{\"id\":\"t1\",\"name\":\"Tool One\"},{\"name\":\"Tool Two\"},{\"tool_id\":\"t3\"}]" +
                "}";
            var list = McpSchemaParser.GetTools(json);
            Assert.Equal(3, list.Count);
            Assert.Contains(list, x => x.id == "t1" && x.name == "Tool One");
            Assert.Contains(list, x => x.id == "Tool Two" && x.name == "Tool Two");
            Assert.Contains(list, x => x.id == "t3" && x.name == "t3");
        }

        [Fact]
        public void GetToolsDetailed_parses_full_tool_objects()
        {
            var json = "{" +
                "\"tools\":[{" +
                    "\"id\":\"tool-abc\",\"name\":\"Tool ABC\",\"description\":\"A test tool\",\"json_schema\":{\"type\":\"object\"}}" +
                "]" +
                "}";

            var detailed = McpSchemaParser.GetToolsDetailed(json);
            Assert.Single(detailed);
            var t = detailed.First();
            Assert.Equal("tool-abc", t.Id);
            Assert.Equal("Tool ABC", t.Name);
            Assert.Equal("A test tool", t.Description);
            Assert.NotNull(t.JsonSchema);
            Assert.Contains("type", t.JsonSchema!);
        }
    }
}
