using System;
using System.IO;
using System.Text;
using Ochoko.Core;

namespace Ochoko;

/// <summary>日付ごとのログファイル。読み上げる本文は書かず、長さだけを記録する(呼び出し側の責務)。</summary>
public sealed class FileLog : ILog
{
    private const int KeepDays = 14;
    private readonly string _dir;
    private readonly object _gate = new object();

    public FileLog(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        Prune();
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + message + Environment.NewLine;
        try
        {
            lock (_gate)
            {
                File.AppendAllText(Path.Combine(_dir, "ochoko-" + now.ToString("yyyyMMdd") + ".log"), line, new UTF8Encoding(false));
            }
        }
        catch (IOException)
        {
            // ログに書けないことで本処理を止めない
        }
    }

    private void Prune()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_dir, "ochoko-*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-KeepDays))
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
        }
    }
}
