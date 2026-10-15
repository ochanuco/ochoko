using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ochoko.Core;

namespace Ochoko;

/// <summary>
/// A.I.VOICE Editor API(TtsControl)を COM の遅延バインドで呼ぶ。DLL は参照しない(公開リポジトリに置けないため)。
/// COM の呼び出しはすべて専用の STA スレッドで行い、同時に 1 件だけ処理する。
/// </summary>
public sealed class AiVoiceEngine : IVoiceEngine, IDisposable
{
    // HostStatus。値は公式ドキュメントに基づく。実機で確認すること
    private const int StatusNotRunning = 0;
    private const int StatusNotConnected = 1;
    private const int StatusBusy = 3;

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly OchokoConfig _config;
    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
    private readonly Thread _thread;
    private int _state = (int)EngineState.Starting;
    private int _running;

    // 以下は STA スレッドからだけ触る
    private dynamic? _tts;
    private JsonObject? _baselineMaster;
    private readonly Dictionary<string, JsonObject> _basePresets = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _writtenPresets = new Dictionary<string, string>(StringComparer.Ordinal);

    public AiVoiceEngine(OchokoConfig config, ILog log)
    {
        _config = config;
        _log = log;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ochoko-com" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public EngineState State => (EngineState)Volatile.Read(ref _state);

    private void SetState(EngineState state) => Volatile.Write(ref _state, (int)state);

    public Task<byte[]> SynthesizeAsync(SynthesisRequest request, CancellationToken ct) =>
        RunAsync(budget => Synthesize(request, budget), _config.BusyWait, _config.OperationTimeout, ct);

    public Task<PresetListing> ListPresetsAsync(CancellationToken ct) =>
        RunAsync(budget => ListPresets(budget), _config.BusyWait, _config.OperationTimeout, ct);

    /// <summary>起動直後と、つながっていないときの再接続。失敗は状態に反映するだけで投げない。</summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        try
        {
            await RunAsync(budget => { EnsureConnected(budget); return true; }, TimeSpan.Zero, _config.WarmupTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is EngineException || e is EngineBusyException)
        {
            _log.Warn("warm-up: " + e.Message);
        }
    }

    public void Dispose() => _queue.CompleteAdding();

    private void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            action();
        }
    }

    private async Task<T> RunAsync<T>(Func<TimeSpan, T> work, TimeSpan busyWait, TimeSpan timeout, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(busyWait, ct).ConfigureAwait(false))
        {
            throw new EngineBusyException();
        }

        try
        {
            // タイムアウトした操作が STA スレッドで続いている間は、新しい操作を積まない
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                throw new EngineException("the engine is still processing an earlier operation that timed out");
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    tcs.SetResult(work(timeout));
                }
                catch (EngineException e)
                {
                    tcs.SetException(e);
                }
                catch (Exception e)
                {
                    tcs.SetException(new EngineException("A.I.VOICE error: " + e.Message, e));
                }
                finally
                {
                    Volatile.Write(ref _running, 0);
                }
            });

            using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var delay = Task.Delay(timeout, delayCts.Token);
                var finished = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
                if (finished == tcs.Task)
                {
                    delayCts.Cancel();
                    return await tcs.Task.ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();
                SetState(EngineState.Unhealthy);
                _log.Error("operation timed out after " + (int)timeout.TotalSeconds + " s; engine marked unhealthy");
                throw new EngineException("timed out after " + (int)timeout.TotalSeconds + " s");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private dynamic EnsureConnected(TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        try
        {
            if (_tts == null)
            {
                var type = Type.GetTypeFromProgID(_config.ProgId)
                    ?? throw new EngineException("COM component '" + _config.ProgId + "' is not registered");
                dynamic created = Activator.CreateInstance(type)!;
                var hostName = _config.HostName ?? Strings((object?)created.GetAvailableHostNames()).FirstOrDefault()
                    ?? throw new EngineException("no A.I.VOICE host is available");
                created.Initialize(hostName);
                _tts = created;
            }

            dynamic tts = _tts!;
            var status = Status((object)tts);
            if (status == StatusNotRunning)
            {
                SetState(EngineState.Starting);
                _log.Info("starting the A.I.VOICE host");
                tts.StartHost();
                status = WaitStatus((object)tts, s => s != StatusNotRunning, deadline, "the A.I.VOICE host to start");
            }

            if (status == StatusNotConnected)
            {
                _log.Info("connecting to the A.I.VOICE host");
                tts.Connect();
                status = Status((object)tts);
            }

            if (status == StatusBusy)
            {
                // 再生や別の合成が終わるのを待つ。待ちきれなくても先に進めて、API 側の判断に任せる
                WaitStatus((object)tts, s => s != StatusBusy, DateTime.UtcNow + TimeSpan.FromSeconds(10), null);
            }

            if (_baselineMaster == null)
            {
                _baselineMaster = (JsonObject)JsonNode.Parse((string)tts.MasterControl)!;
                _log.Info("captured the baseline MasterControl");
            }

            SetState(EngineState.Ready);
            return tts;
        }
        catch (Exception e)
        {
            SetState(EngineState.Unavailable);
            _tts = null;
            if (e is EngineException)
            {
                throw;
            }

            throw new EngineException("cannot connect to A.I.VOICE: " + e.Message, e);
        }
    }

    private static int Status(object tts)
    {
        dynamic d = tts;
        return Convert.ToInt32((object)d.Status);
    }

    private static int WaitStatus(object tts, Func<int, bool> done, DateTime deadline, string? what)
    {
        while (true)
        {
            var status = Status((object)tts);
            if (done(status))
            {
                return status;
            }

            if (DateTime.UtcNow >= deadline)
            {
                if (what == null)
                {
                    return status;
                }

                throw new EngineException("timed out waiting for " + what);
            }

            Thread.Sleep(250);
        }
    }

    private static List<string> Strings(object? values) =>
        values is IEnumerable e ? e.Cast<object>().Select(o => o.ToString() ?? "").ToList() : new List<string>();

    private byte[] Synthesize(SynthesisRequest r, TimeSpan budget)
    {
        dynamic tts = EnsureConnected(budget);

        var names = Strings((object?)tts.VoicePresetNames);
        if (!names.Contains(r.Preset))
        {
            throw new EngineException("unknown preset: " + r.Preset);
        }

        var working = ParamKeys.WorkingPresetPrefix + r.Preset;
        var json = BuildWorkingPreset((object)tts, r, working);
        if (!names.Contains(working))
        {
            tts.AddVoicePreset(json);
        }
        else if (!_writtenPresets.TryGetValue(working, out var last) || last != json)
        {
            tts.SetVoicePreset(json);
        }

        _writtenPresets[working] = json;

        tts.CurrentVoicePresetName = working;
        tts.MasterControl = BuildMaster(r);
        tts.Text = r.Text;

        Directory.CreateDirectory(_config.TmpDir);
        var path = Path.Combine(_config.TmpDir, Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            tts.SaveAudioToFile(path);
            WaitStatus((object)tts, s => s != StatusBusy, DateTime.UtcNow + TimeSpan.FromSeconds(10), null);
            return ReadWhenReady(path);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException e)
            {
                _log.Warn("could not delete " + path + ": " + e.Message);
            }
        }
    }

    private JsonObject GetBasePreset(object ttsObject, string name)
    {
        dynamic tts = ttsObject;
        if (!_basePresets.TryGetValue(name, out var cached))
        {
            cached = (JsonObject)JsonNode.Parse((string)tts.GetVoicePreset(name))!;
            _basePresets[name] = cached;
        }

        return cached;
    }

    private string BuildWorkingPreset(object tts, SynthesisRequest r, string workingName)
    {
        var preset = (JsonObject)GetBasePreset(tts, r.Preset).DeepClone();
        preset["PresetName"] = workingName;
        foreach (var kv in r.PresetOverrides)
        {
            preset[kv.Key] = JsonValue.Create(kv.Value);
        }

        if (r.Styles != null)
        {
            var styles = new JsonArray();
            foreach (var s in r.Styles)
            {
                styles.Add(new JsonObject { [ParamKeys.StyleName] = s.Name, [ParamKeys.StyleValue] = s.Value });
            }

            preset[ParamKeys.Styles] = styles;
        }

        return preset.ToJsonString(JsonOptions);
    }

    private string BuildMaster(SynthesisRequest r)
    {
        var master = (JsonObject)_baselineMaster!.DeepClone();
        foreach (var kv in r.MasterOverrides)
        {
            master[kv.Key] = JsonValue.Create(kv.Value);
        }

        return master.ToJsonString(JsonOptions);
    }

    private PresetListing ListPresets(TimeSpan budget)
    {
        dynamic tts = EnsureConnected(budget);
        // ユーザーが GUI で編集した内容を拾うため、基底プリセットのキャッシュを作り直す
        _basePresets.Clear();
        var entries = new List<PresetEntry>();
        foreach (var name in Strings((object?)tts.VoicePresetNames))
        {
            if (name.StartsWith(ParamKeys.WorkingPresetPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var preset = GetBasePreset((object)tts, name);
            entries.Add(new PresetEntry(name, preset.DeepClone()));
        }

        return new PresetListing(entries, _baselineMaster?.DeepClone());
    }

    private static byte[] ReadWhenReady(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            try
            {
                if (File.Exists(path))
                {
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length > 44)
                    {
                        return bytes;
                    }
                }
            }
            catch (IOException)
            {
                // 書き出しの途中で掴まれているだけなので、待って読み直す
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new EngineException("A.I.VOICE did not produce an audio file");
            }

            Thread.Sleep(100);
        }
    }
}
