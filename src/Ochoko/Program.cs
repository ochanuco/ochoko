using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Ochoko.Core;

namespace Ochoko;

public static class Program
{
    private static ILog? s_log;

    public static int Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal("unhandled exception: " + e.ExceptionObject);
        try
        {
            return Run();
        }
        catch (Exception e)
        {
            ReportFatal("fatal: " + e);
            return 2;
        }
    }

    // WinExe にはコンソールがないので、ロガーができる前の失敗は既定のデータ領域か一時フォルダのログに残す
    private static void ReportFatal(string message)
    {
        var log = s_log;
        if (log == null)
        {
            foreach (var dir in new[] { Path.Combine(OchokoConfig.DefaultDataDir, "logs"), Path.Combine(Path.GetTempPath(), "ochoko") })
            {
                try
                {
                    log = new FileLog(dir);
                    break;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
            }
        }

        log?.Error(message);
    }

    private static int Run()
    {
        var config = OchokoConfig.Load(OchokoConfig.DefaultConfigDir);
        Directory.CreateDirectory(config.DataDir);
        var log = new FileLog(config.LogDir);
        s_log = log;
        var version = VersionInfo.Get();

        BearerAuthenticator auth;
        try
        {
            auth = new BearerAuthenticator(TokenFile.Parse(File.ReadAllText(config.TokensPath)));
        }
        catch (Exception e) when (e is IOException || e is FormatException || e is UnauthorizedAccessException)
        {
            log.Error("cannot load " + config.TokensPath + ": " + e.Message);
            return 2;
        }

        if (auth.Count == 0)
        {
            log.Error(config.TokensPath + " has no tokens");
            return 2;
        }

        using (var power = new PowerRequest())
        using (var engine = new AiVoiceEngine(config, log))
        {
            var keepAwake = new KeepAwakeManager(new SystemClock(), power, log);
            var mcp = new McpServer(engine, keepAwake, version, log);
            var host = new HttpHost(config.Prefix, mcp, auth, engine, version, log);
            try
            {
                host.Start();
            }
            catch (HttpListenerException e)
            {
                log.Error("cannot listen on " + config.Prefix + ": " + e.Message + " (register.ps1 grants the URL ACL)");
                return 1;
            }

            log.Info("ochoko " + version + " listening on " + config.Prefix + " callers=" + auth.Count);

            using (var cts = new CancellationTokenSource())
            using (new Timer(_ => keepAwake.Refresh(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
            using (new Timer(_ => Reconnect(engine, cts.Token), null, TimeSpan.Zero, TimeSpan.FromSeconds(30)))
            {
                try
                {
                    host.RunAsync(cts.Token).GetAwaiter().GetResult();
                }
                catch (InvalidOperationException e)
                {
                    log.Error(e.Message);
                    return 1;
                }
            }
        }

        return 0;
    }

    // A.I.VOICE の起動待ちや再接続を、呼び出しが来る前に済ませておく
    private static void Reconnect(AiVoiceEngine engine, CancellationToken ct)
    {
        if (engine.State == EngineState.Ready)
        {
            return;
        }

        engine.WarmUpAsync(ct).GetAwaiter().GetResult();
    }
}
