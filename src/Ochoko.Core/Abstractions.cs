using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Ochoko.Core;

public enum EngineState
{
    Ready,
    Starting,
    Unavailable,
    Unhealthy,
}

public sealed class StyleValue
{
    public StyleValue(string name, double value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public double Value { get; }
}

public sealed class SynthesisRequest
{
    public SynthesisRequest(
        string text,
        string preset,
        IReadOnlyDictionary<string, double> presetOverrides,
        IReadOnlyList<StyleValue>? styles,
        IReadOnlyDictionary<string, double> masterOverrides)
    {
        Text = text;
        Preset = preset;
        PresetOverrides = presetOverrides;
        Styles = styles;
        MasterOverrides = masterOverrides;
    }

    public string Text { get; }
    public string Preset { get; }
    public IReadOnlyDictionary<string, double> PresetOverrides { get; }

    /// <summary>null なら基底プリセットの Styles をそのまま使う。</summary>
    public IReadOnlyList<StyleValue>? Styles { get; }

    public IReadOnlyDictionary<string, double> MasterOverrides { get; }
}

public sealed class PresetEntry
{
    public PresetEntry(string name, JsonNode? preset)
    {
        Name = name;
        Preset = preset;
    }

    public string Name { get; }
    public JsonNode? Preset { get; }
}

public sealed class PresetListing
{
    public PresetListing(IReadOnlyList<PresetEntry> presets, JsonNode? masterControl)
    {
        Presets = presets;
        MasterControl = masterControl;
    }

    public IReadOnlyList<PresetEntry> Presets { get; }
    public JsonNode? MasterControl { get; }
}

public interface IVoiceEngine
{
    EngineState State { get; }

    /// <summary>WAV のバイト列を返す。同時に 1 件しか処理しない。</summary>
    Task<byte[]> SynthesizeAsync(SynthesisRequest request, CancellationToken ct);

    Task<PresetListing> ListPresetsAsync(CancellationToken ct);
}

/// <summary>排他の待ちがタイムアウトした。</summary>
public sealed class EngineBusyException : Exception
{
    public EngineBusyException() : base("busy") { }
}

/// <summary>呼び出し元に tool error として返すエンジンの失敗。</summary>
public sealed class EngineException : Exception
{
    public EngineException(string message) : base(message) { }
    public EngineException(string message, Exception inner) : base(message, inner) { }
}

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new NullLog();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
