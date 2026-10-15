using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Ochoko.Core;

public sealed class McpResponse
{
    public McpResponse(int status, string? body)
    {
        Status = status;
        Body = body;
    }

    public int Status { get; }

    /// <summary>null なら本文なし(202)。</summary>
    public string? Body { get; }
}

/// <summary>Streamable HTTP の POST 1 件分を処理する。セッションは持たない。</summary>
public sealed class McpServer
{
    public const string LatestProtocolVersion = "2025-06-18";

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;

    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IVoiceEngine _engine;
    private readonly KeepAwakeManager _keepAwake;
    private readonly string _version;
    private readonly ILog _log;

    public McpServer(IVoiceEngine engine, KeepAwakeManager keepAwake, string version, ILog log)
    {
        _engine = engine;
        _keepAwake = keepAwake;
        _version = version;
        _log = log;
    }

    public async Task<McpResponse> HandleAsync(string body, string caller, CancellationToken ct, string? protocolVersionHeader = null)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Error(null, ParseError, "Parse error", 400);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                return Error(null, InvalidRequest, "Batch requests are not supported", 400);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error(null, InvalidRequest, "Invalid Request", 400);
            }

            var hasId = root.TryGetProperty("id", out var idEl);
            JsonNode? id = null;
            if (hasId)
            {
                if (idEl.ValueKind != JsonValueKind.String && idEl.ValueKind != JsonValueKind.Number)
                {
                    return Error(null, InvalidRequest, "id must be a string or a number", 400);
                }

                id = JsonNode.Parse(idEl.GetRawText());
            }

            if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
            {
                // クライアントからの応答(result / error)は受け取るだけ
                if (hasId && (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _)))
                {
                    return new McpResponse(202, null);
                }

                return Error(id, InvalidRequest, "Invalid Request", 400);
            }

            var method = methodEl.GetString()!;

            // ヘッダーがなければ仕様どおり LatestProtocolVersion とみなす。initialize は交渉前なので見ない
            if (method != "initialize" && protocolVersionHeader != null && protocolVersionHeader != LatestProtocolVersion)
            {
                return Error(id, InvalidRequest, "Unsupported MCP-Protocol-Version: " + protocolVersionHeader, 400);
            }

            if (!hasId)
            {
                return new McpResponse(202, null);
            }

            JsonElement? parameters = root.TryGetProperty("params", out var p) ? p : (JsonElement?)null;
            try
            {
                switch (method)
                {
                    case "initialize":
                        return Result(id, Initialize());
                    case "ping":
                        return Result(id, new JsonObject());
                    case "tools/list":
                        return Result(id, new JsonObject { ["tools"] = Tools.List() });
                    case "tools/call":
                        return Result(id, await CallToolAsync(parameters, caller, ct).ConfigureAwait(false));
                    default:
                        return Error(id, MethodNotFound, "Method not found: " + method, 200);
                }
            }
            catch (InvalidParamsException e)
            {
                return Error(id, InvalidParams, e.Message, 200);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _log.Error("internal error: " + e);
                return Error(id, InternalError, "Internal error", 200);
            }
        }
    }

    private JsonObject Initialize()
    {
        return new JsonObject
        {
            ["protocolVersion"] = LatestProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "ochoko", ["version"] = _version },
        };
    }

    private async Task<JsonObject> CallToolAsync(JsonElement? parameters, string caller, CancellationToken ct)
    {
        if (parameters is not JsonElement p || p.ValueKind != JsonValueKind.Object
            || !p.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidParamsException("params.name is required");
        }

        var name = nameEl.GetString()!;
        JsonElement? arguments = p.TryGetProperty("arguments", out var a) ? a : (JsonElement?)null;
        var sw = Stopwatch.StartNew();
        try
        {
            switch (name)
            {
                case Tools.Synthesize:
                    return await SynthesizeAsync(Tools.ParseSynthesize(arguments), caller, sw, ct).ConfigureAwait(false);
                case Tools.ListPresets:
                    Tools.RequireNoArguments(arguments);
                    return await ListPresetsAsync(caller, sw, ct).ConfigureAwait(false);
                case Tools.KeepAwake:
                    return KeepAwake(Tools.ParseKeepAwake(arguments), caller, sw);
                default:
                    throw new InvalidParamsException("Unknown tool: " + name);
            }
        }
        catch (EngineBusyException)
        {
            _log.Warn($"caller={caller} tool={name} busy ms={sw.ElapsedMilliseconds}");
            return ToolError("busy");
        }
        catch (EngineException e)
        {
            _log.Warn($"caller={caller} tool={name} engine error: {e.Message} ms={sw.ElapsedMilliseconds}");
            return ToolError(e.Message);
        }
    }

    private async Task<JsonObject> SynthesizeAsync(SynthesisRequest request, string caller, Stopwatch sw, CancellationToken ct)
    {
        var wav = await _engine.SynthesizeAsync(request, ct).ConfigureAwait(false);
        var duration = WavInfo.TryGetDurationMs(wav);
        if (duration == null)
        {
            throw new EngineException("the engine returned audio that is not a readable WAV");
        }

        _log.Info($"caller={caller} tool=synthesize preset={request.Preset} text_len={request.Text.Length} bytes={wav.Length} duration_ms={duration} ms={sw.ElapsedMilliseconds}");
        var structured = new JsonObject { ["duration_ms"] = duration.Value, ["bytes"] = wav.Length };
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "audio",
                    ["data"] = Convert.ToBase64String(wav),
                    ["mimeType"] = "audio/wav",
                },
                new JsonObject { ["type"] = "text", ["text"] = structured.ToJsonString(Json) },
            },
            ["structuredContent"] = structured,
        };
    }

    private async Task<JsonObject> ListPresetsAsync(string caller, Stopwatch sw, CancellationToken ct)
    {
        var listing = await _engine.ListPresetsAsync(ct).ConfigureAwait(false);
        var presets = new JsonArray();
        foreach (var entry in listing.Presets)
        {
            presets.Add(new JsonObject { ["name"] = entry.Name, ["preset"] = entry.Preset?.DeepClone() });
        }

        _log.Info($"caller={caller} tool=list_presets count={listing.Presets.Count} ms={sw.ElapsedMilliseconds}");
        return Structured(new JsonObject { ["presets"] = presets, ["master_control"] = listing.MasterControl?.DeepClone() });
    }

    private JsonObject KeepAwake(int ttl, string caller, Stopwatch sw)
    {
        var expires = _keepAwake.Lease(caller, ttl);
        _log.Info($"caller={caller} tool=keep_awake ttl_seconds={ttl} ms={sw.ElapsedMilliseconds}");
        return Structured(new JsonObject
        {
            ["caller"] = caller,
            ["expires_at"] = expires.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        });
    }

    private static JsonObject Structured(JsonObject value)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = value.ToJsonString(Json) }),
            ["structuredContent"] = value,
        };
    }

    private static JsonObject ToolError(string message)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
            ["isError"] = true,
        };
    }

    private static McpResponse Result(JsonNode? id, JsonNode result)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };
        return new McpResponse(200, msg.ToJsonString(Json));
    }

    private static McpResponse Error(JsonNode? id, int code, string message, int status)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        return new McpResponse(status, msg.ToJsonString(Json));
    }
}
