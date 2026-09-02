using System.Diagnostics;

namespace Deskhand.Core.Services;

public record CrawlNodeDto(
    string? Ref, string Name, string Type, int? X, int? Y,
    IReadOnlyList<string> Actions, bool Expanded, IReadOnlyList<CrawlNodeDto> Children);

public record UxCrawlDto(
    string AppKey, string? Window, int Nodes, int Depth, bool Cached, string CrawledAt,
    CrawlNodeDto? Root, string Note, string? Error = null);

/// <summary>
/// Actively explores a window's UX to build a deep, cacheable map — so an agent can learn "every command this
/// app has" once and recall it. <b>Safe by design:</b> it only performs non-destructive, structure-revealing
/// actions — <c>ExpandCollapse.Expand</c> on collapsed expandables (menus/trees/groups/combos) and, optionally,
/// selecting tabs — then re-reads the revealed children and recurses to a depth. It <b>never invokes</b>
/// buttons or menu commands (that would run them), and it skips anything whose label matches a dangerous verb
/// (delete/quit/format/…). Expanded nodes are collapsed again afterward to restore state. The result is cached
/// per app (exe · window-class · title) via <see cref="UxCacheStore"/>.
/// </summary>
public static class UxCrawler
{
    private static readonly string[] Dangerous =
        { "delete", "remove", "quit", "exit", "close", "shut down", "shutdown", "restart", "log off", "logoff",
          "sign out", "format", "uninstall", "erase", "discard", "reset", "wipe", "purge", "empty", "clear all" };

    public static UxCrawlDto Crawl(IAutomationBackend b, string? rootRef, int depth = 3, int maxNodes = 1500,
        bool selectTabs = false, bool useCache = false)
    {
        depth = Math.Clamp(depth, 1, 8);
        maxNodes = Math.Clamp(maxNodes, 10, 20_000);

        ElementInfoDto root;
        try { root = rootRef is not null ? b.GetElement(rootRef) : b.GetForegroundWindow(); }
        catch (Exception ex) { return new UxCrawlDto("", null, 0, depth, false, Now(), null, "", "Could not resolve the window: " + ex.Message); }

        string appKey = AppKey(root);
        if (useCache)
        {
            var cached = UxCacheStore.Load(appKey);
            if (cached is { } c)
            {
                try
                {
                    var dto = System.Text.Json.JsonSerializer.Deserialize<UxCrawlDto>(c.GetRawText());
                    if (dto is not null) return dto with { Cached = true, Note = "Returned the cached map (pass useCache=false to re-crawl)." };
                }
                catch { /* stale/incompatible cache — re-crawl below */ }
            }
        }

        var budget = new int[] { maxNodes };
        var sw = Stopwatch.StartNew();
        CrawlNodeDto node;
        try { node = Walk(b, root.Ref, depth, selectTabs, budget, sw); }
        catch (Exception ex) { return new UxCrawlDto(appKey, root.Name, 0, depth, false, Now(), null, "", "Crawl failed: " + ex.Message); }

        int nodes = maxNodes - budget[0];
        var map = new UxCrawlDto(appKey, Clean(root.Name), nodes, depth, false, Now(), node,
            $"Crawled {nodes} nodes to depth {depth} (safe mode: expanded structure, no commands invoked). Cached under appKey — recall with useCache=true.");
        UxCacheStore.Save(appKey, map);
        return map;
    }

    private static CrawlNodeDto Walk(IAutomationBackend b, string reference, int depth, bool selectTabs, int[] budget, Stopwatch sw)
    {
        ElementInfoDto el;
        try { el = b.GetElement(reference); }
        catch { return new CrawlNodeDto(reference, "", "?", null, null, Array.Empty<string>(), false, Array.Empty<CrawlNodeDto>()); }

        var actions = ActionsFor(el);
        var (cx, cy) = Center(el.BoundingRect);
        bool expanded = false;

        if (budget[0] <= 0 || depth <= 0 || sw.Elapsed > TimeSpan.FromSeconds(60))
            return new CrawlNodeDto(el.Ref, Clean(el.Name) ?? "", el.ControlType, cx, cy, actions, false, Array.Empty<CrawlNodeDto>());

        // Reveal children non-destructively: expand a collapsed expandable (unless its label is dangerous).
        if (el.Patterns.Contains("ExpandCollapse") && !IsDangerous(el.Name))
        {
            try { b.ExpandCollapse(el.Ref, true); expanded = true; Thread.Sleep(120); } catch { }
        }
        else if (selectTabs && el.ControlType == "TabItem" && !IsDangerous(el.Name))
        {
            try { b.Select(el.Ref); Thread.Sleep(120); } catch { }
        }

        var children = new List<CrawlNodeDto>();
        try
        {
            var found = b.Find(el.Ref, new FindQuery(Scope: "children", Max: 300));
            foreach (var ch in found)
            {
                if (budget[0] <= 0) break;
                budget[0]--;
                children.Add(Walk(b, ch.Ref, depth - 1, selectTabs, budget, sw));
            }
        }
        catch { }

        if (expanded) { try { b.ExpandCollapse(el.Ref, false); } catch { } }   // restore collapsed state

        return new CrawlNodeDto(el.Ref, Clean(el.Name) ?? "", el.ControlType, cx, cy, actions, expanded, children);
    }

    private static List<string> ActionsFor(ElementInfoDto e)
    {
        var a = new List<string>();
        foreach (var p in e.Patterns)
            switch (p)
            {
                case "Invoke": a.Add("invoke"); break;
                case "Toggle": a.Add("toggle"); break;
                case "ExpandCollapse": a.Add("expand"); break;
                case "Value": case "RangeValue": a.Add("setValue"); break;
                case "SelectionItem": a.Add("select"); break;
            }
        return a.Distinct().ToList();
    }

    private static bool IsDangerous(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.ToLowerInvariant();
        return Dangerous.Any(d => n.Contains(d));
    }

    private static (int? x, int? y) Center(RectDto? r) =>
        r is { Width: > 0, Height: > 0 } ? (r.X + r.Width / 2, r.Y + r.Height / 2) : (null, null);

    private static string AppKey(ElementInfoDto root)
    {
        string exe = "app";
        try { if (root.ProcessId is int pid) { using var p = Process.GetProcessById(pid); exe = p.ProcessName; } } catch { }
        return $"{exe}|{root.ClassName}|{Clean(root.Name)}";
    }

    private static string Now() => DateTime.Now.ToString("s");
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : (s.Trim().Length > 100 ? s.Trim()[..100] + "…" : s.Trim());
}
