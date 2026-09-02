using Deskhand.Core.Services;
using Xunit;

namespace Deskhand.Rdp.Tests;

/// <summary>The per-app cache behind deskhand_crawl_ux — save a crawled map, recall it, list, delete.</summary>
public class UxCacheStoreTests
{
    [Fact]
    public void Save_load_list_delete_round_trip()
    {
        string key = "testapp|Class|Title " + System.Guid.NewGuid().ToString("N")[..6];
        UxCacheStore.Save(key, new { hello = "world", n = 42 });

        var loaded = UxCacheStore.Load(key);
        Assert.NotNull(loaded);
        Assert.Equal("world", loaded!.Value.GetProperty("hello").GetString());
        Assert.Equal(42, loaded.Value.GetProperty("n").GetInt32());

        // The sanitized key appears in the listing.
        var sanitized = new string(key.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        Assert.Contains(sanitized, UxCacheStore.List());

        Assert.True(UxCacheStore.Delete(key));
        Assert.Null(UxCacheStore.Load(key));
    }

    [Fact]
    public void Load_unknown_returns_null()
    {
        Assert.Null(UxCacheStore.Load("no such app " + System.Guid.NewGuid()));
    }
}
