using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ochoko.Core;

namespace Ochoko.Core.Tests;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan span) => UtcNow += span;
}

public sealed class FakePower : IPowerRequest
{
    public bool Held { get; private set; }
    public int Acquires { get; private set; }
    public void Acquire() { Held = true; Acquires++; }
    public void Release() { Held = false; }
}

public sealed class FakeEngine : IVoiceEngine
{
    public EngineState State { get; set; } = EngineState.Ready;
    public SynthesisRequest? LastRequest { get; private set; }
    public Exception? Throw { get; set; }
    public byte[] Wav { get; set; } = WavBuilder.Build(24000, 48000);

    public Task<byte[]> SynthesizeAsync(SynthesisRequest request, CancellationToken ct)
    {
        LastRequest = request;
        if (Throw != null) throw Throw;
        return Task.FromResult(Wav);
    }

    public Task<PresetListing> ListPresetsAsync(CancellationToken ct) =>
        Task.FromResult(new PresetListing(
            new[] { new PresetEntry("voice-a", JsonNode.Parse("{\"PresetName\":\"voice-a\",\"Volume\":1.0}")) },
            JsonNode.Parse("{\"Volume\":1.0,\"Speed\":1.0}")));
}

public static class WavBuilder
{
    /// <summary>16bit モノラルの無音 WAV。byteRate = sampleRate * 2。</summary>
    public static byte[] Build(int sampleRate, int dataBytes)
    {
        var ms = new System.IO.MemoryStream();
        var w = new System.IO.BinaryWriter(ms);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        w.Write(new byte[dataBytes]);
        return ms.ToArray();
    }
}
