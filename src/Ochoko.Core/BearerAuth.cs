using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ochoko.Core;

public sealed class BearerAuthenticator
{
    private const string Scheme = "Bearer ";
    private readonly List<KeyValuePair<string, byte[]>> _entries = new List<KeyValuePair<string, byte[]>>();

    public BearerAuthenticator(IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var kv in tokens)
        {
            _entries.Add(new KeyValuePair<string, byte[]>(kv.Key, Hash(kv.Value)));
        }
    }

    public int Count => _entries.Count;

    /// <summary>トークンに対応する呼び出し元の名前。認証できなければ null。</summary>
    public string? Authenticate(string? authorizationHeader)
    {
        if (authorizationHeader == null || !authorizationHeader.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorizationHeader.Substring(Scheme.Length).Trim();
        if (token.Length == 0)
        {
            return null;
        }

        var presented = Hash(token);
        string? caller = null;
        // 一致した時点で打ち切らず、全件を同じ手間で比べる
        foreach (var entry in _entries)
        {
            if (FixedTimeEquals(presented, entry.Value))
            {
                caller = entry.Key;
            }
        }

        return caller;
    }

    // 長さの違いが比較時間に出ないよう、ハッシュ同士を比べる
    private static byte[] Hash(string value)
    {
        using (var sha = SHA256.Create())
        {
            return sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        }
    }

    internal static bool FixedTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }
}

public static class TokenFile
{
    /// <summary>{"&lt;caller&gt;": "&lt;token&gt;"} を読む。形が違えば FormatException。</summary>
    public static Dictionary<string, string> Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new FormatException("tokens.json is not valid JSON: " + e.Message);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("tokens.json must be a JSON object of caller -> token");
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var token = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new FormatException("tokens.json: token of '" + prop.Name + "' must be a non-empty string");
                }

                if (!seen.Add(token!))
                {
                    throw new FormatException("tokens.json: '" + prop.Name + "' shares a token with another caller");
                }

                result[prop.Name] = token!;
            }

            return result;
        }
    }
}
