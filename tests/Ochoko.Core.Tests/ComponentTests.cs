using System;
using System.Collections.Generic;
using Ochoko.Core;
using Xunit;

namespace Ochoko.Core.Tests;

public class KeepAwakeTests
{
    private readonly FakeClock _clock = new FakeClock();
    private readonly FakePower _power = new FakePower();
    private KeepAwakeManager Make() => new KeepAwakeManager(_clock, _power, NullLog.Instance);

    [Fact]
    public void Holds_until_the_last_lease_expires()
    {
        var m = Make();
        m.Lease("dev", 30);
        m.Lease("prod", 60);
        Assert.True(_power.Held);

        _clock.Advance(TimeSpan.FromSeconds(30));
        m.Refresh();
        Assert.True(_power.Held);
        Assert.Equal(1, m.ActiveCount);

        _clock.Advance(TimeSpan.FromSeconds(30));
        m.Refresh();
        Assert.False(_power.Held);
        Assert.Equal(0, m.ActiveCount);
    }

    [Fact]
    public void Same_caller_replaces_its_lease()
    {
        var m = Make();
        m.Lease("dev", 10);
        var expires = m.Lease("dev", 100);
        Assert.Equal(_clock.UtcNow.AddSeconds(100), expires);

        _clock.Advance(TimeSpan.FromSeconds(50));
        m.Refresh();
        Assert.True(_power.Held);
        Assert.Equal(1, _power.Acquires);

        m.Lease("dev", 5);
        _clock.Advance(TimeSpan.FromSeconds(5));
        m.Refresh();
        Assert.False(_power.Held);
    }
}

public class AuthTests
{
    private static BearerAuthenticator Make() =>
        new BearerAuthenticator(new Dictionary<string, string> { ["dev"] = "tok-dev", ["prod"] = "tok-prod" });

    [Theory]
    [InlineData("Bearer tok-dev", "dev")]
    [InlineData("bearer tok-prod", "prod")]
    [InlineData("Bearer tok-dev ", "dev")]
    [InlineData("Bearer tok-dev2", null)]
    [InlineData("Bearer ", null)]
    [InlineData("Basic tok-dev", null)]
    [InlineData("tok-dev", null)]
    [InlineData(null, null)]
    public void Maps_token_to_caller(string? header, string? expected)
    {
        Assert.Equal(expected, Make().Authenticate(header));
    }

    [Fact]
    public void Token_file_parses_and_validates()
    {
        var tokens = TokenFile.Parse("{\"dev\":\"a\",\"prod\":\"b\"}");
        Assert.Equal("b", tokens["prod"]);
        Assert.Throws<FormatException>(() => TokenFile.Parse("[]"));
        Assert.Throws<FormatException>(() => TokenFile.Parse("{\"dev\":\"\"}"));
        Assert.Throws<FormatException>(() => TokenFile.Parse("{\"dev\":1}"));
        Assert.Throws<FormatException>(() => TokenFile.Parse("{\"dev\":\"a\",\"prod\":\"a\"}"));
        Assert.Throws<FormatException>(() => TokenFile.Parse("nope"));
    }
}

public class WavInfoTests
{
    [Fact]
    public void Computes_duration_from_header()
    {
        Assert.Equal(1000, WavInfo.TryGetDurationMs(WavBuilder.Build(24000, 48000)));
        Assert.Equal(500, WavInfo.TryGetDurationMs(WavBuilder.Build(44100, 44100)));
    }

    [Fact]
    public void Caps_a_data_size_larger_than_the_file()
    {
        var wav = WavBuilder.Build(24000, 48000);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(wav, 40);
        Assert.Equal(1000, WavInfo.TryGetDurationMs(wav));
    }

    [Fact]
    public void Returns_null_for_a_huge_non_data_chunk()
    {
        var wav = new byte[64];
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        System.Text.Encoding.ASCII.GetBytes("junk").CopyTo(wav, 12);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(wav, 16);
        Assert.Null(WavInfo.TryGetDurationMs(wav));
    }

    [Fact]
    public void Returns_null_for_non_wav()
    {
        Assert.Null(WavInfo.TryGetDurationMs(new byte[] { 1, 2, 3 }));
        Assert.Null(WavInfo.TryGetDurationMs(new byte[100]));
    }
}
