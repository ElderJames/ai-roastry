using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LY.LlmPool.Web.Services
{
    /// <summary>
    /// Helper for parsing MCP schema/proto JSON stored in McpServerConfig.SchemaCacheJson.
    /// Provides pure, static parsing functions so they can be unit-tested in isolation.
    /// </summary>
    public static class McpSchemaParser
    {
        public record ToolInfo(string Id, string Name, string? Description, string? JsonSchema);

        public static (int tools, int prompts) ParseCounts(string json)
        {
            try
            {
                var opts = new JsonDocumentOptions { AllowTrailingCommas = true };
                using var doc = JsonDocument.Parse(json, opts);
                int tools = 0, prompts = 0;
                if (doc.RootElement.TryGetProperty("tools", out var toolsElem) && toolsElem.ValueKind == JsonValueKind.Array)
                {
                    tools = toolsElem.GetArrayLength();
                }
                if (doc.RootElement.TryGetProperty("prompts", out var promptsElem) && promptsElem.ValueKind == JsonValueKind.Array)
                {
                    prompts = promptsElem.GetArrayLength();
                }
                return (tools, prompts);
            }
            catch
            {
                return (0, 0);
            }
        }

        public static (int tools, int prompts, int resources) McpParseCounts(string json)
        {
            try
            {
                var opts = new JsonDocumentOptions { AllowTrailingCommas = true };
                using var doc = JsonDocument.Parse(json, opts);
                int tools = 0, prompts = 0, resources = 0;
                if (doc.RootElement.TryGetProperty("tools", out var toolsElem) && toolsElem.ValueKind == JsonValueKind.Array)
                {
                    tools = toolsElem.GetArrayLength();
                }
                if (doc.RootElement.TryGetProperty("prompts", out var promptsElem) && promptsElem.ValueKind == JsonValueKind.Array)
                {
                    prompts = promptsElem.GetArrayLength();
                }
                if (doc.RootElement.TryGetProperty("resources", out var resourcesElem) && promptsElem.ValueKind == JsonValueKind.Array)
                {
                    resources = resourcesElem.GetArrayLength();
                }
                return (tools, prompts, resources);
            }
            catch
            {
                return (0, 0,0);
            }
        }

        public static List<(string id, string name)> GetTools(string json)
        {
            var list = new List<(string id, string name)>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("tools", out var toolsElem) && toolsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in toolsElem.EnumerateArray())
                    {
                        string tid = string.Empty;
                        string tname = string.Empty;
                        if (t.ValueKind == JsonValueKind.Object)
                        {
                            if (t.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.String)
                                tid = idp.GetString() ?? string.Empty;
                            if (t.TryGetProperty("name", out var namep) && namep.ValueKind == JsonValueKind.String)
                                tname = namep.GetString() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(tid) && t.TryGetProperty("tool_id", out var tip) && tip.ValueKind == JsonValueKind.String)
                                tid = tip.GetString() ?? string.Empty;
                        }
                        if (string.IsNullOrWhiteSpace(tid) && !string.IsNullOrWhiteSpace(tname)) tid = tname;
                        if (!string.IsNullOrWhiteSpace(tid)) list.Add((tid, string.IsNullOrWhiteSpace(tname) ? tid : tname));
                    }
                }
            }
            catch
            {
                // ignore parse errors
            }
            return list;
        }

        public static List<ToolInfo> GetToolsDetailed(string json)
        {
            var list = new List<ToolInfo>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("tools", out var toolsElem) && toolsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in toolsElem.EnumerateArray())
                    {
                        if (t.ValueKind != JsonValueKind.Object) continue;

                        string tid = string.Empty;
                        string tname = string.Empty;
                        string? tdesc = null;
                        string? tschema = null;

                        if (t.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.String)
                            tid = idp.GetString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(tid) && t.TryGetProperty("tool_id", out var tip) && tip.ValueKind == JsonValueKind.String)
                            tid = tip.GetString() ?? string.Empty;
                        if (t.TryGetProperty("name", out var namep) && namep.ValueKind == JsonValueKind.String)
                            tname = namep.GetString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(tname) && t.TryGetProperty("title", out var titlep) && titlep.ValueKind == JsonValueKind.String)
                            tname = titlep.GetString() ?? string.Empty;

                        if (t.TryGetProperty("description", out var descp) && descp.ValueKind == JsonValueKind.String)
                            tdesc = descp.GetString();

                        if (t.TryGetProperty("json_schema", out var schemap))
                        {
                            tschema = schemap.ValueKind == JsonValueKind.String ? schemap.GetString() : schemap.GetRawText();
                        }
                        else if (t.TryGetProperty("jsonSchema", out var schemap2))
                        {
                            tschema = schemap2.ValueKind == JsonValueKind.String ? schemap2.GetString() : schemap2.GetRawText();
                        }
                        else if (t.TryGetProperty("schema", out var schemap3))
                        {
                            tschema = schemap3.ValueKind == JsonValueKind.String ? schemap3.GetString() : schemap3.GetRawText();
                        }

                        if (string.IsNullOrWhiteSpace(tid) && !string.IsNullOrWhiteSpace(tname)) tid = tname;
                        if (string.IsNullOrWhiteSpace(tname) && !string.IsNullOrWhiteSpace(tid)) tname = tid;

                        if (!string.IsNullOrWhiteSpace(tid)) list.Add(new ToolInfo(tid, string.IsNullOrWhiteSpace(tname) ? tid : tname, tdesc, tschema));
                    }
                }
            }
            catch
            {
                // ignore parse errors
            }
            return list;
        }
    }
}
