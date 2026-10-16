using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ochoko.Core;

/// <summary>引数が不正。JSON-RPC の -32602 で返す。</summary>
public sealed class InvalidParamsException : Exception
{
    public InvalidParamsException(string message) : base(message) { }
}

public static class Tools
{
    public const string Synthesize = "synthesize";
    public const string ListPresets = "list_presets";
    public const string KeepAwake = "keep_awake";

    public const int MaxTextLength = 2000;
    public const int MaxTtlSeconds = 900;
    public const int MaxPartnerIdLength = 64;

    public static JsonArray List()
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["name"] = Synthesize,
                ["title"] = "Synthesize speech",
                ["description"] = "Synthesize text with A.I.VOICE and return a WAV. preset_overrides and master_overrides take raw A.I.VOICE values.",
                ["inputSchema"] = SynthesizeInputSchema(),
                ["outputSchema"] = Schema(
                    ("duration_ms", new JsonObject { ["type"] = "integer" }),
                    ("bytes", new JsonObject { ["type"] = "integer" })),
            },
            new JsonObject
            {
                ["name"] = ListPresets,
                ["title"] = "List voice presets",
                ["description"] = "List the A.I.VOICE presets (raw JSON) and the baseline MasterControl captured at connect time.",
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
                ["outputSchema"] = Schema(
                    ("presets", new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["name"] = new JsonObject { ["type"] = "string" },
                                ["preset"] = new JsonObject { ["type"] = "object" },
                            },
                            ["required"] = new JsonArray("name", "preset"),
                        },
                    }),
                    ("master_control", new JsonObject { ["type"] = "object" })),
            },
            new JsonObject
            {
                ["name"] = KeepAwake,
                ["title"] = "Keep the machine awake",
                ["description"] = "Hold a power request so the machine does not sleep until the lease expires. Calling again with the same caller and partner_id replaces that lease; leases of other partners are untouched.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["ttl_seconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxTtlSeconds },
                        ["partner_id"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MaxPartnerIdLength },
                    },
                    ["required"] = new JsonArray("ttl_seconds", "partner_id"),
                    ["additionalProperties"] = false,
                },
                ["outputSchema"] = Schema(
                    ("caller", new JsonObject { ["type"] = "string" }),
                    ("partner_id", new JsonObject { ["type"] = "string" }),
                    ("expires_at", new JsonObject { ["type"] = "string", ["format"] = "date-time" })),
            },
        };
    }

    private static JsonObject Schema(params (string Name, JsonObject Schema)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema) in props)
        {
            properties[name] = schema;
            // Add(string) は Add<T> に解決され、System.Text.Json 8 では TypeInfoResolver のない options で書けない値になる
            required.Add(JsonValue.Create(name));
        }

        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    private static JsonObject SynthesizeInputSchema()
    {
        JsonObject Numbers(string[] keys)
        {
            var o = new JsonObject();
            foreach (var k in keys)
            {
                o[k] = new JsonObject { ["type"] = "number" };
            }

            return o;
        }

        var presetProps = Numbers(ParamKeys.PresetNumeric);
        presetProps[ParamKeys.Styles] = new JsonObject
        {
            ["type"] = "array",
            ["description"] = "Replaces the preset's Styles wholesale.",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    [ParamKeys.StyleName] = new JsonObject { ["type"] = "string" },
                    [ParamKeys.StyleValue] = new JsonObject { ["type"] = "number" },
                },
                ["required"] = new JsonArray(ParamKeys.StyleName, ParamKeys.StyleValue),
                ["additionalProperties"] = false,
            },
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["text"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MaxTextLength },
                ["preset"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Base voice preset name (see list_presets)." },
                ["preset_overrides"] = new JsonObject { ["type"] = "object", ["properties"] = presetProps, ["additionalProperties"] = false },
                ["master_overrides"] = new JsonObject { ["type"] = "object", ["properties"] = Numbers(ParamKeys.MasterNumeric), ["additionalProperties"] = false },
            },
            ["required"] = new JsonArray("text", "preset"),
            ["additionalProperties"] = false,
        };
    }

    public static SynthesisRequest ParseSynthesize(JsonElement? arguments)
    {
        var args = RequireObject(arguments, "arguments");
        RejectUnknown(args, "arguments", "text", "preset", "preset_overrides", "master_overrides");

        var text = RequireString(args, "text");
        if (text.Length < 1 || text.Length > MaxTextLength)
        {
            throw new InvalidParamsException("text must be 1.." + MaxTextLength + " characters");
        }

        var preset = RequireString(args, "preset");
        if (preset.Length == 0)
        {
            throw new InvalidParamsException("preset must not be empty");
        }

        if (preset.StartsWith(ParamKeys.WorkingPresetPrefix, StringComparison.Ordinal))
        {
            throw new InvalidParamsException("preset must not start with '" + ParamKeys.WorkingPresetPrefix + "'");
        }

        var presetNumbers = new Dictionary<string, double>(StringComparer.Ordinal);
        List<StyleValue>? styles = null;
        if (args.TryGetProperty("preset_overrides", out var po))
        {
            var obj = RequireObject(po, "preset_overrides");
            var allowed = new List<string>(ParamKeys.PresetNumeric) { ParamKeys.Styles };
            RejectUnknown(obj, "preset_overrides", allowed.ToArray());
            foreach (var key in ParamKeys.PresetNumeric)
            {
                if (obj.TryGetProperty(key, out var v))
                {
                    presetNumbers[key] = RequireNumber(v, "preset_overrides." + key);
                }
            }

            if (obj.TryGetProperty(ParamKeys.Styles, out var sv))
            {
                styles = ParseStyles(sv);
            }
        }

        var master = new Dictionary<string, double>(StringComparer.Ordinal);
        if (args.TryGetProperty("master_overrides", out var mo))
        {
            var obj = RequireObject(mo, "master_overrides");
            RejectUnknown(obj, "master_overrides", ParamKeys.MasterNumeric);
            foreach (var key in ParamKeys.MasterNumeric)
            {
                if (obj.TryGetProperty(key, out var v))
                {
                    master[key] = RequireNumber(v, "master_overrides." + key);
                }
            }
        }

        return new SynthesisRequest(text, preset, presetNumbers, styles, master);
    }

    public static (string PartnerId, int TtlSeconds) ParseKeepAwake(JsonElement? arguments)
    {
        var args = RequireObject(arguments, "arguments");
        RejectUnknown(args, "arguments", "ttl_seconds", "partner_id");
        if (!args.TryGetProperty("ttl_seconds", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var ttl))
        {
            throw new InvalidParamsException("ttl_seconds must be an integer");
        }

        if (ttl < 1 || ttl > MaxTtlSeconds)
        {
            throw new InvalidParamsException("ttl_seconds must be 1.." + MaxTtlSeconds);
        }

        if (!args.TryGetProperty("partner_id", out var p) || p.ValueKind != JsonValueKind.String)
        {
            throw new InvalidParamsException("partner_id must be a string");
        }

        var raw = p.GetString()!;
        var partnerId = raw.Trim();
        if (partnerId.Length == 0 || raw.Length > MaxPartnerIdLength)
        {
            throw new InvalidParamsException("partner_id must be 1.." + MaxPartnerIdLength + " characters");
        }

        return (partnerId, ttl);
    }

    public static void RequireNoArguments(JsonElement? arguments)
    {
        if (arguments == null || arguments.Value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        RejectUnknown(RequireObject(arguments, "arguments"), "arguments");
    }

    private static List<StyleValue> ParseStyles(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidParamsException("preset_overrides.Styles must be an array");
        }

        var list = new List<StyleValue>();
        foreach (var item in el.EnumerateArray())
        {
            var obj = RequireObject(item, "preset_overrides.Styles[]");
            RejectUnknown(obj, "preset_overrides.Styles[]", ParamKeys.StyleName, ParamKeys.StyleValue);
            var name = RequireString(obj, ParamKeys.StyleName);
            if (!obj.TryGetProperty(ParamKeys.StyleValue, out var value))
            {
                throw new InvalidParamsException("preset_overrides.Styles[]." + ParamKeys.StyleValue + " is required");
            }

            list.Add(new StyleValue(name, RequireNumber(value, "preset_overrides.Styles[]." + ParamKeys.StyleValue)));
        }

        return list;
    }

    private static JsonElement RequireObject(JsonElement? el, string name)
    {
        if (el == null || el.Value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidParamsException(name + " must be an object");
        }

        return el.Value;
    }

    private static void RejectUnknown(JsonElement obj, string name, params string[] allowed)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (Array.IndexOf(allowed, prop.Name) < 0)
            {
                throw new InvalidParamsException("unknown key '" + prop.Name + "' in " + name);
            }
        }
    }

    private static string RequireString(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String)
        {
            throw new InvalidParamsException(key + " must be a string");
        }

        return v.GetString() ?? "";
    }

    private static double RequireNumber(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var d) || double.IsNaN(d) || double.IsInfinity(d))
        {
            throw new InvalidParamsException(name + " must be a number");
        }

        return d;
    }
}
