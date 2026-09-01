using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Deskhand.Core.Services;

/// <summary>
/// Outbound event push: a set of subscriber URLs that Deskhand POSTs events to (UI focus/window-open, and any
/// other events wired in). Best-effort fire-and-forget with a short timeout — a slow or dead subscriber never
/// blocks the server. Registration is in-memory (per process). Note UI events can carry window titles, so treat
/// subscriber URLs as trusted sinks.
/// </summary>
public sealed class WebhookService
{
    private readonly ConcurrentDictionary<string, byte> _urls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public IReadOnlyList<string> List() => _urls.Keys.OrderBy(u => u).ToList();

    public bool Add(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")
        && _urls.TryAdd(url.Trim(), 0);

    public bool Remove(string? url) => url is not null && _urls.TryRemove(url.Trim(), out _);
    public void Clear() => _urls.Clear();

    /// <summary>POST a payload to every subscriber. Never throws; failures are swallowed per-URL.</summary>
    public async Task Deliver(object payload)
    {
        var urls = _urls.Keys.ToArray();
        if (urls.Length == 0) return;
        string json = JsonSerializer.Serialize(payload);
        foreach (var u in urls)
        {
            try { using var c = new StringContent(json, Encoding.UTF8, "application/json"); await _http.PostAsync(u, c); }
            catch { /* best effort — a bad sink must not affect the server */ }
        }
    }
}
