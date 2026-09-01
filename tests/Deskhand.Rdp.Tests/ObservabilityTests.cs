using Deskhand.Core.Services;
using Xunit;

namespace Deskhand.Rdp.Tests;

/// <summary>Metrics rendering, webhook registration, and fetch input validation (no network).</summary>
public class ObservabilityTests
{
    [Fact]
    public void Metrics_render_is_prometheus_text()
    {
        var text = MetricsService.Render(armed: true, captureEnabled: true, version: "9.9.9");
        Assert.Contains("deskhand_up{version=\"9.9.9\"} 1", text);
        Assert.Contains("deskhand_armed 1", text);
        Assert.Contains("# TYPE deskhand_uptime_seconds gauge", text);
    }

    [Fact]
    public void Webhooks_add_validate_and_remove()
    {
        var wh = new WebhookService();
        Assert.True(wh.Add("https://example.com/hook"));
        Assert.False(wh.Add("https://example.com/hook"));   // duplicate
        Assert.False(wh.Add("not-a-url"));                  // invalid
        Assert.False(wh.Add("ftp://example.com"));          // wrong scheme
        Assert.Single(wh.List());
        Assert.True(wh.Remove("https://example.com/hook"));
        Assert.Empty(wh.List());
    }

    [Fact]
    public async Task Fetch_rejects_a_non_http_url_fast()
    {
        var r = await FetchService.DownloadAsync("not-a-url", null, null);
        Assert.False(r.Ok);
        Assert.Contains("http/https", r.Error!);
    }
}
