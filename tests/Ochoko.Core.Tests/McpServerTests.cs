using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ochoko.Core;
using Xunit;

namespace Ochoko.Core.Tests;

public class McpServerTests
{
    private readonly FakeEngine _engine = new FakeEngine();
    private readonly FakeClock _clock = new FakeClock();
    private readonly FakePower _power = new FakePower();
    private readonly McpServer _server;

    public McpServerTests()
    {
        var keep = new KeepAwakeManager(_clock, _power, NullLog.Instance);
        _server = new McpServer(_engine, keep, "test", NullLog.Instance);
    }

    private async Task<(int Status, JsonNode? Body)> Post(string body, string caller = "dev")
    {
        var r = await _server.HandleAsync(body, caller, CancellationToken.None);
        return (r.Status, r.Body == null ? null : JsonNode.Parse(r.Body));
    }

    private Task<(int Status, JsonNode? Body)> Call(string tool, string argsJson) =>
        Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + argsJson + "}}");

    [Theory]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2025-03-26", "2025-06-18")]
    [InlineData("2024-11-05", "2025-06-18")]
    public async Task Initialize_answers_supported_version(string requested, string expected)
    {
        var (status, body) = await Post("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + requested + "\"}}");
        Assert.Equal(200, status);
        Assert.Equal(7, (int)body!["id"]!);
        Assert.Equal(expected, (string)body["result"]!["protocolVersion"]!);
        Assert.Equal("ochoko", (string)body["result"]!["serverInfo"]!["name"]!);
        Assert.False((bool)body["result"]!["capabilities"]!["tools"]!["listChanged"]!);
    }

    [Theory]
    [InlineData("2025-03-26")]
    [InlineData("bogus")]
    public async Task Unsupported_protocol_version_header_is_400(string header)
    {
        var r = await _server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", "dev", CancellationToken.None, header);
        Assert.Equal(400, r.Status);
        var notification = await _server.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", "dev", CancellationToken.None, header);
        Assert.Equal(400, notification.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-06-18")]
    public async Task Missing_or_current_protocol_version_header_is_accepted(string? header)
    {
        var r = await _server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", "dev", CancellationToken.None, header);
        Assert.Equal(200, r.Status);
    }

    [Fact]
    public async Task Initialize_ignores_protocol_version_header()
    {
        var r = await _server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}", "dev", CancellationToken.None, "2024-11-05");
        Assert.Equal(200, r.Status);
    }

    [Fact]
    public async Task Notification_gets_202_without_body()
    {
        var (status, body) = await Post("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        Assert.Equal(202, status);
        Assert.Null(body);
    }

    [Fact]
    public async Task Ping_returns_empty_result()
    {
        var (status, body) = await Post("{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"method\":\"ping\"}");
        Assert.Equal(200, status);
        Assert.Equal("a", (string)body!["id"]!);
        Assert.Empty(body["result"]!.AsObject());
    }

    [Fact]
    public async Task Unknown_method_is_32601()
    {
        var (_, body) = await Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"resources/list\"}");
        Assert.Equal(-32601, (int)body!["error"]!["code"]!);
    }

    [Fact]
    public async Task Batch_is_32600()
    {
        var (status, body) = await Post("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}]");
        Assert.Equal(400, status);
        Assert.Equal(-32600, (int)body!["error"]!["code"]!);
    }

    [Fact]
    public async Task Malformed_json_is_32700()
    {
        var (status, body) = await Post("{");
        Assert.Equal(400, status);
        Assert.Equal(-32700, (int)body!["error"]!["code"]!);
    }

    [Fact]
    public async Task Tools_list_has_three_tools_with_output_schema()
    {
        var (_, body) = await Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
        var tools = body!["result"]!["tools"]!.AsArray();
        Assert.Equal(new[] { "synthesize", "list_presets", "keep_awake" }, tools.Select(t => (string)t!["name"]!).ToArray());
        Assert.All(tools, t => Assert.NotNull(t!["outputSchema"]));
    }

    [Fact]
    public async Task Synthesize_returns_audio_and_structured_content()
    {
        var (_, body) = await Call("synthesize",
            "{\"text\":\"こんにちは\",\"preset\":\"voice-a\",\"preset_overrides\":{\"Speed\":1.2,\"Styles\":[{\"Name\":\"J\",\"Value\":0.5}]},\"master_overrides\":{\"Pitch\":0.1}}");
        var result = body!["result"]!;
        Assert.Equal("audio", (string)result["content"]![0]!["type"]!);
        Assert.Equal("audio/wav", (string)result["content"]![0]!["mimeType"]!);
        Assert.Equal(_engine.Wav, Convert.FromBase64String((string)result["content"]![0]!["data"]!));
        Assert.Equal("text", (string)result["content"]![1]!["type"]!);
        Assert.Equal(result["structuredContent"]!.ToJsonString(), JsonNode.Parse((string)result["content"]![1]!["text"]!)!.ToJsonString());
        Assert.Equal(1000, (int)result["structuredContent"]!["duration_ms"]!);
        Assert.Equal(_engine.Wav.Length, (int)result["structuredContent"]!["bytes"]!);
        Assert.Equal(1.2, _engine.LastRequest!.PresetOverrides["Speed"]);
        Assert.Equal("J", _engine.LastRequest.Styles![0].Name);
        Assert.Equal(0.1, _engine.LastRequest.MasterOverrides["Pitch"]);
    }

    [Theory]
    [InlineData("{\"text\":\"\",\"preset\":\"p\"}")]
    [InlineData("{\"preset\":\"p\"}")]
    [InlineData("{\"text\":\"a\"}")]
    [InlineData("{\"text\":1,\"preset\":\"p\"}")]
    [InlineData("{\"text\":\"a\",\"preset\":\"p\",\"extra\":1}")]
    [InlineData("{\"text\":\"a\",\"preset\":\"ochoko:p\"}")]
    [InlineData("{\"text\":\"a\",\"preset\":\"p\",\"preset_overrides\":{\"SentencePause\":1}}")]
    [InlineData("{\"text\":\"a\",\"preset\":\"p\",\"preset_overrides\":{\"Speed\":\"fast\"}}")]
    [InlineData("{\"text\":\"a\",\"preset\":\"p\",\"preset_overrides\":{\"Styles\":[{\"Name\":\"J\"}]}}")]
    [InlineData("{\"text\":\"a\",\"preset\":\"p\",\"preset_overrides\":{\"Styles\":[{\"Name\":\"J\",\"Value\":1,\"X\":1}]}}")]
    [InlineData("{\"text\":\"a\",\"preset\":\"p\",\"master_overrides\":{\"Bogus\":1}}")]
    [InlineData("{\"text\":\"a\",\"preset\":\"p\",\"master_overrides\":[]}")]
    public async Task Synthesize_rejects_bad_arguments(string args)
    {
        var (_, body) = await Call("synthesize", args);
        Assert.Equal(-32602, (int)body!["error"]!["code"]!);
        Assert.Null(_engine.LastRequest);
    }

    [Fact]
    public async Task Synthesize_rejects_text_over_2000_chars()
    {
        var (_, ok) = await Call("synthesize", "{\"text\":\"" + new string('あ', 2000) + "\",\"preset\":\"p\"}");
        Assert.Null(ok!["error"]);
        var (_, bad) = await Call("synthesize", "{\"text\":\"" + new string('あ', 2001) + "\",\"preset\":\"p\"}");
        Assert.Equal(-32602, (int)bad!["error"]!["code"]!);
    }

    [Fact]
    public async Task Master_only_keys_are_accepted_in_master_overrides()
    {
        var (_, body) = await Call("synthesize", "{\"text\":\"a\",\"preset\":\"p\",\"master_overrides\":{\"SentencePause\":300}}");
        Assert.Null(body!["error"]);
        Assert.Equal(300, _engine.LastRequest!.MasterOverrides["SentencePause"]);
    }

    [Fact]
    public async Task Engine_errors_become_tool_errors()
    {
        _engine.Throw = new EngineBusyException();
        var (_, busy) = await Call("synthesize", "{\"text\":\"a\",\"preset\":\"p\"}");
        Assert.True((bool)busy!["result"]!["isError"]!);
        Assert.Equal("busy", (string)busy["result"]!["content"]![0]!["text"]!);

        _engine.Throw = new EngineException("timed out after 60 s");
        var (_, failed) = await Call("synthesize", "{\"text\":\"a\",\"preset\":\"p\"}");
        Assert.True((bool)failed!["result"]!["isError"]!);
    }

    [Fact]
    public async Task Unreadable_wav_is_a_tool_error()
    {
        _engine.Wav = new byte[] { 1, 2, 3 };
        var (_, body) = await Call("synthesize", "{\"text\":\"a\",\"preset\":\"p\"}");
        Assert.True((bool)body!["result"]!["isError"]!);
    }

    [Fact]
    public async Task List_presets_returns_raw_json()
    {
        var (_, body) = await Call("list_presets", "{}");
        var sc = body!["result"]!["structuredContent"]!;
        Assert.Equal("voice-a", (string)sc["presets"]![0]!["name"]!);
        Assert.Equal(1.0, (double)sc["presets"]![0]!["preset"]!["Volume"]!);
        Assert.Equal(1.0, (double)sc["master_control"]!["Speed"]!);
    }

    [Fact]
    public async Task Keep_awake_leases_per_caller_and_expires()
    {
        var (_, body) = await Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"keep_awake\",\"arguments\":{\"ttl_seconds\":60}}}", "prod");
        var sc = body!["result"]!["structuredContent"]!;
        Assert.Equal("prod", (string)sc["caller"]!);
        Assert.Equal("2026-01-01T00:01:00Z", (string)sc["expires_at"]!);
        Assert.True(_power.Held);
    }

    [Theory]
    [InlineData("{\"ttl_seconds\":0}")]
    [InlineData("{\"ttl_seconds\":901}")]
    [InlineData("{\"ttl_seconds\":1.5}")]
    [InlineData("{\"ttl_seconds\":\"5\"}")]
    [InlineData("{}")]
    public async Task Keep_awake_rejects_bad_ttl(string args)
    {
        var (_, body) = await Call("keep_awake", args);
        Assert.Equal(-32602, (int)body!["error"]!["code"]!);
        Assert.False(_power.Held);
    }

    [Fact]
    public async Task Unknown_tool_is_32602()
    {
        var (_, body) = await Call("nope", "{}");
        Assert.Equal(-32602, (int)body!["error"]!["code"]!);
    }
}
