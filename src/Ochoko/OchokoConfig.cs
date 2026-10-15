using System;
using System.IO;
using System.Text.Json;

namespace Ochoko;

/// <summary>C:\ProgramData\ochoko\config.json。ファイルも各キーも任意で、なければ既定値を使う。</summary>
public sealed class OchokoConfig
{
    public const string DefaultConfigDir = @"C:\ProgramData\ochoko";

    public string ConfigDir { get; private set; } = DefaultConfigDir;
    public const string DefaultDataDir = @"C:\apps\ochoko\data";

    public string DataDir { get; private set; } = DefaultDataDir;
    public int Port { get; private set; } = 8190;
    public string ProgId { get; private set; } = "AI.Talk.Editor.Api.TtsControl";

    /// <summary>null なら GetAvailableHostNames() の先頭を使う。</summary>
    public string? HostName { get; private set; }

    // BusyWait + OperationTimeout は、MCP クライアントの既定のリクエストタイムアウト(60 秒)より短くする
    public TimeSpan OperationTimeout { get; private set; } = TimeSpan.FromSeconds(45);
    public TimeSpan BusyWait { get; private set; } = TimeSpan.FromSeconds(5);
    public TimeSpan WarmupTimeout { get; private set; } = TimeSpan.FromSeconds(180);

    public string TokensPath => Path.Combine(ConfigDir, "tokens.json");
    public string LogDir => Path.Combine(DataDir, "logs");
    public string BaselinePath => Path.Combine(DataDir, "master-baseline.json");
    public string TmpDir => Path.Combine(DataDir, "tmp");
    public string Prefix => "http://+:" + Port + "/";

    public static OchokoConfig Load(string configDir)
    {
        var config = new OchokoConfig { ConfigDir = configDir };
        var path = Path.Combine(configDir, "config.json");
        if (!File.Exists(path))
        {
            return config;
        }

        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("dataDir", out var d)) config.DataDir = d.GetString()!;
            if (root.TryGetProperty("port", out var p)) config.Port = p.GetInt32();
            if (root.TryGetProperty("progId", out var pid)) config.ProgId = pid.GetString()!;
            if (root.TryGetProperty("hostName", out var h)) config.HostName = h.GetString();
            if (root.TryGetProperty("operationTimeoutSeconds", out var o)) config.OperationTimeout = TimeSpan.FromSeconds(o.GetInt32());
            if (root.TryGetProperty("busyWaitSeconds", out var b)) config.BusyWait = TimeSpan.FromSeconds(b.GetInt32());
            if (root.TryGetProperty("warmupTimeoutSeconds", out var w)) config.WarmupTimeout = TimeSpan.FromSeconds(w.GetInt32());
        }

        return config;
    }
}
