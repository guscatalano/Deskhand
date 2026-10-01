using System.Text;
using System.Text.Json;

namespace Deskhand.Core;

/// <summary>
/// Optional declarative configuration. Deskhand's knobs are all <c>DESKHAND_*</c> environment variables read
/// directly throughout the codebase (it does not use the ASP.NET Core configuration system). This loads a JSON
/// file at startup and promotes its values into the process environment <b>before</b> anything reads them — but
/// only where the variable isn't already set, so a real environment variable always wins. That makes a
/// file-based, fully declarative install possible ("drop deskhand.json, run the exe") without changing how any
/// component reads its settings.
///
/// <para><b>Precedence:</b> real environment variable &gt; config file &gt; built-in default.</para>
///
/// <para><b>Search order</b> (first file found wins): the path in <c>DESKHAND_CONFIG</c>; <c>deskhand.json</c> in
/// the current directory; <c>deskhand.json</c> beside the executable; <c>%PROGRAMDATA%\Deskhand\deskhand.json</c>.</para>
///
/// <para><b>Keys</b> may be full env names (<c>"DESKHAND_PORT"</c>), the suffix (<c>"PORT"</c>), or camelCase /
/// kebab (<c>"maxUploadMb"</c>, <c>"enable-shell"</c>) — all normalized to <c>DESKHAND_UPPER_SNAKE</c>.
/// <b>Values</b> may be strings, numbers, or booleans (<c>true</c>→<c>"1"</c>, <c>false</c>→<c>"0"</c>); a nested
/// <c>"deskhand"</c> object is also honored. A missing or empty file is a no-op; a file that is present but
/// malformed is fatal (<see cref="ApplyOrExit"/>) — Deskhand refuses to start rather than silently ignore your
/// settings.</para>
/// </summary>
public static class EnvConfigFile
{
    public readonly record struct Result(string? Path, int Applied, int Skipped, string? Error);

    /// <summary>Load the config file (if any) into the environment. Call this once, as the very first thing a
    /// host does, before any <c>Environment.GetEnvironmentVariable</c> read.</summary>
    public static Result Apply()
    {
        string? path = Locate();
        if (path is null) return new Result(null, 0, 0, null);
        try
        {
            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return new Result(path, 0, 0, null);  // empty placeholder: no-op
            using var doc = JsonDocument.Parse(text,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Result(path, 0, 0, "root is not a JSON object");

            int applied = 0, skipped = 0;
            void Process(JsonElement obj)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    // A nested "deskhand": { ... } wrapper is honored (so a shared config file can namespace us).
                    if (prop.Value.ValueKind == JsonValueKind.Object && prop.NameEquals("deskhand")) { Process(prop.Value); continue; }
                    string? val = ToValue(prop.Value);
                    if (val is null) continue;  // null / object / array -> skip
                    string name = Normalize(prop.Name);
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) { skipped++; continue; }  // env wins
                    Environment.SetEnvironmentVariable(name, val);
                    applied++;
                }
            }
            Process(doc.RootElement);
            return new Result(path, applied, skipped, null);
        }
        catch (Exception ex) { return new Result(path, 0, 0, ex.Message); }
    }

    /// <summary>Apply the config file and write a one-line notice to <paramref name="log"/> (use
    /// <c>Console.Error</c> for the MCP stdio host, whose stdout carries the protocol). A config file that is
    /// present but malformed is <b>fatal</b>: rather than silently fall back to defaults — which would quietly
    /// ignore settings the operator intended, including security-relevant ones like a token or bind address —
    /// the process exits with <paramref name="exitCode"/>. A missing or empty file is a no-op.</summary>
    public static Result ApplyOrExit(System.IO.TextWriter log, int exitCode = 3)
    {
        var r = Apply();
        if (r.Error is not null)
        {
            log.WriteLine($"FATAL: config file '{r.Path}' is present but could not be applied: {r.Error}");
            log.WriteLine("  Refusing to start with a broken config so your settings aren't silently ignored. Fix the file or remove it.");
            Environment.Exit(exitCode);
        }
        else if (r.Path is not null)
            log.WriteLine($"Config: applied {r.Applied} setting(s) from {r.Path} (environment overrides; {r.Skipped} already set in environment).");
        return r;
    }

    private static string? Locate()
    {
        var explicitPath = Environment.GetEnvironmentVariable("DESKHAND_CONFIG")?.Trim();
        if (!string.IsNullOrEmpty(explicitPath)) return File.Exists(explicitPath) ? explicitPath : null;
        foreach (var cand in Candidates())
            if (File.Exists(cand)) return cand;
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(Directory.GetCurrentDirectory(), "deskhand.json");
        yield return Path.Combine(AppContext.BaseDirectory, "deskhand.json");
        var programData = Environment.GetEnvironmentVariable("PROGRAMDATA");
        if (!string.IsNullOrEmpty(programData)) yield return Path.Combine(programData, "Deskhand", "deskhand.json");
    }

    private static string? ToValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        _ => null,
    };

    /// <summary>"DESKHAND_PORT" -&gt; itself; "port" -&gt; "DESKHAND_PORT"; "maxUploadMb" -&gt; "DESKHAND_MAX_UPLOAD_MB".</summary>
    internal static string Normalize(string key)
    {
        string k = key.Trim();
        if (k.StartsWith("DESKHAND_", StringComparison.OrdinalIgnoreCase)) return k.ToUpperInvariant();
        var sb = new StringBuilder("DESKHAND_");
        char prev = '\0';
        foreach (char c in k)
        {
            if (c is '-' or ' ' or '.' or '_') { if (sb[^1] != '_') sb.Append('_'); prev = c; continue; }
            if (char.IsUpper(c) && (char.IsLower(prev) || char.IsDigit(prev)) && sb[^1] != '_') sb.Append('_');
            sb.Append(char.ToUpperInvariant(c));
            prev = c;
        }
        return sb.ToString();
    }
}
