using System;
using System.Collections.Generic;
using System.Linq;

namespace Ochoko.Core;

public interface IPowerRequest
{
    void Acquire();
    void Release();
}

/// <summary>呼び出し元ごとの keep-awake リース。ひとつでも有効な間だけ電源要求を保持する。</summary>
public sealed class KeepAwakeManager
{
    private readonly IClock _clock;
    private readonly IPowerRequest _power;
    private readonly ILog _log;
    private readonly object _gate = new object();
    private readonly Dictionary<string, DateTimeOffset> _leases = new Dictionary<string, DateTimeOffset>();
    private bool _held;

    public KeepAwakeManager(IClock clock, IPowerRequest power, ILog log)
    {
        _clock = clock;
        _power = power;
        _log = log;
    }

    /// <summary>同じ呼び出し元のリースは置き換える。失効時刻を返す。</summary>
    public DateTimeOffset Lease(string caller, int ttlSeconds)
    {
        lock (_gate)
        {
            var expires = _clock.UtcNow.AddSeconds(ttlSeconds);
            _leases[caller] = expires;
            Apply();
            return expires;
        }
    }

    /// <summary>失効したリースを捨て、電源要求を実態に合わせる。定期的に呼ぶ。</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            Apply();
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                Apply();
                return _leases.Count;
            }
        }
    }

    private void Apply()
    {
        var now = _clock.UtcNow;
        foreach (var caller in _leases.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
        {
            _leases.Remove(caller);
        }

        var wanted = _leases.Count > 0;
        if (wanted == _held)
        {
            return;
        }

        try
        {
            if (wanted)
            {
                _power.Acquire();
            }
            else
            {
                _power.Release();
            }

            _held = wanted;
            _log.Info("keep-awake " + (wanted ? "held" : "released"));
        }
        catch (Exception e)
        {
            _log.Error("keep-awake power request failed: " + e.Message);
        }
    }
}
