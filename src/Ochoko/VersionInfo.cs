using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Ochoko;

public static class VersionInfo
{
    /// <summary>exe の隣の release.json(リリースのワークフローが書く)の version。なければアセンブリの版。</summary>
    public static string Get()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "release.json");
            if (File.Exists(path))
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(v.GetString()))
                    {
                        return v.GetString()!;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException || e is JsonException)
        {
        }

        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return info?.InformationalVersion ?? "unknown";
    }
}
