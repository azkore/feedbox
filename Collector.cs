using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;

internal sealed class PageResult
{
    public List<Post> posts { get; set; }
    public string next { get; set; }
    public string problem { get; set; }
    public bool auth { get; set; }
    public bool stale { get; set; }
    public int retry { get; set; }
    internal int Added;
}

internal sealed class Collector
{
    private const int PageBudget = 8;
    internal enum WalkResult { More, Complete, GapAtCutoff }
    private readonly Store store;
    private readonly string script;
    private readonly string articleBody;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

    public Collector(Store store)
    {
        this.store = store;
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("collector.js"))
        using (var reader = new StreamReader(stream)) script = reader.ReadToEnd();
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("article-body.js"))
        using (var reader = new StreamReader(stream)) articleBody = reader.ReadToEnd();
    }

    public async Task<PageResult> Check(CoreWebView2 view, Source state, bool allowWhilePaused)
    {
        int added = 0;
        for (int i = 0; i < PageBudget; i++)
        {
            if (!store.HasSource(state.Name)) return new PageResult { Added = added };
            // A pause takes effect before the next request; the preceding page is already committed.
            if (!allowWhilePaused && store.IsPaused) return Finish(state, new PageResult(), added, "Check paused.");
            // Resume a partial walk at its committed continuation, or start at New.
            var first = state.Cursor == "";
            var path = first ? NewListingPath(state) : state.Cursor;
            // Manual, scheduled and continuation requests all consume the same persistent allowance.
            var limited = store.RedditLimit(DateTimeOffset.UtcNow, true);
            if (limited != null) return Finish(state, new PageResult { problem = limited }, added, limited);
            var result = await Fetch(view, path, state.Name, first);
            if (result.problem != null)
            {
                var detail = result.problem.TrimEnd('.') + (result.stale ? ". The next check will start from New." : ".");
                if (result.stale) store.ResetWalk(state.Name, detail);
                return Finish(state, result, added, detail);
            }

            var oldCursor = state.Cursor;
            var walk = ProcessPage(state, result, first);
            bool complete = walk == WalkResult.Complete;
            added += store.SavePage(state, result.posts, complete);
            if (!store.HasSource(state.Name)) { result.Added = added; return result; }
            if (complete) return Finish(state, result, added, state.Status, true);
            if (walk == WalkResult.GapAtCutoff)
            {
                const string gap = "Possible gap: reached the collection start date before finding the previous batch.";
                store.RecordGap(state, gap);
                return Finish(state, result, added, gap);
            }
            if (state.Cursor == "" || state.Cursor == oldCursor)
            {
                const string gap = "Possible gap: Reddit did not provide another page before the previous batch was reached.";
                store.RecordGap(state, gap);
                return Finish(state, result, added, gap);
            }
            // Bound how quickly a single source can request continuations.
            if (i + 1 < PageBudget) await Task.Delay(TimeSpan.FromSeconds(10));
        }
        return Finish(state, new PageResult(), added, "Page limit reached. The next check will continue here.");
    }

    private PageResult Finish(Source state, PageResult result, int added, string detail, bool complete = false)
    {
        // A stopped walk can report what it saved, but cannot say there were no new posts.
        result.Added = added;
        store.Note(state.Name, PostCount(added, complete) + (string.IsNullOrEmpty(detail) ? "" : " " + detail));
        return result;
    }

    internal static string PostCount(int added, bool complete)
    {
        return added == 0 ? (complete ? "No new posts found." : "No new posts added.")
            : "Added " + added.ToString(CultureInfo.CurrentCulture) + (added == 1 ? " new post." : " new posts.");
    }

    internal static WalkResult ProcessPage(Source state, PageResult page, bool first)
    {
        if (state.InitialRemaining > 0)
        {
            // Count each ordinary listing entry once, even if Reddit changes a continuation and we restart at New.
            var seen = new HashSet<string>(Split(state.InitialIds));
            var accepted = new List<Post>();
            foreach (var post in page.posts.Where(p => !p.pinned))
            {
                if (!seen.Contains(post.id))
                {
                    if (state.InitialRemaining == 0) break;
                    seen.Add(post.id);
                    state.InitialRemaining--;
                    if (state.RedditFloor == "" || string.CompareOrdinal(post.created, state.RedditFloor) < 0) state.RedditFloor = post.created;
                }
                accepted.Add(post);
            }
            page.posts = accepted;
            TrackPage(state, page);
            if (state.InitialRemaining == 0 || state.Cursor == "")
            {
                state.Status = state.InitialRemaining == 0 ? "" : "Reddit's listing ended before the requested count.";
                state.InitialRemaining = -1;
                state.InitialIds = "";
                return WalkResult.Complete;
            }
            state.InitialIds = string.Join(",", seen);
            state.Status = "Initial import: " + state.InitialRemaining.ToString(CultureInfo.InvariantCulture) + " articles remaining.";
            return WalkResult.More;
        }

        bool atCutoff = false;
        if (state.RedditFloor != "")
        {
            // Stop on a whole page of older ordinary posts; a pin or one out-of-order card does not end the walk.
            var ordinary = page.posts.Where(p => !p.pinned).ToList();
            atCutoff = ordinary.Count > 0 && ordinary.All(p => p.publicationKnown && string.CompareOrdinal(p.created, state.RedditFloor) < 0);
            page.posts = page.posts.Where(p => p.publicationKnown && string.CompareOrdinal(p.created, state.RedditFloor) >= 0).ToList();
        }

        bool initialByDate = state.InitialRemaining == 0;
        bool complete = Advance(state, page, first);
        if (initialByDate || (state.Anchors == "" && state.RedditFloor != ""))
        {
            if (!atCutoff && state.Cursor != "") return WalkResult.More;
            state.InitialRemaining = -1;
            state.Status = "";
            return WalkResult.Complete;
        }
        if (complete) return WalkResult.Complete;
        return atCutoff ? WalkResult.GapAtCutoff : WalkResult.More;
    }

    private static string[] TrackPage(Source state, PageResult result)
    {
        // Pinning moves posts outside the usual chronology, so pinned IDs are never checkpoints.
        var ids = result.posts.Where(p => !p.pinned).Select(p => p.id).Distinct().ToArray();
        var head = Split(state.Head).Concat(ids).Distinct().Take(12).ToArray();
        state.Head = string.Join(",", head);
        state.Cursor = result.next ?? "";
        return ids;
    }

    internal static bool Advance(Source state, PageResult result, bool first)
    {
        var ids = TrackPage(state, result);

        // On first use, save a bounded starting batch instead of importing the whole subreddit.
        if (state.RedditFloor == "" && state.Anchors == "" && state.Head != "" && (!first || state.Cursor == ""))
        {
            state.Status = "First check: only recent pages checked.";
            return true;
        }

        var anchors = Split(state.Anchors);
        var overlap = Split(state.Seen).Concat(ids.Intersect(anchors)).Distinct().ToArray();
        state.Seen = string.Join(",", overlap);
        // Require several known IDs and scan one extra page for reordered or recently approved posts.
        if (anchors.Length > 0 && overlap.Length >= Math.Min(3, anchors.Length))
        {
            if (state.ExtraPage || state.Cursor == "")
            {
                state.Status = "";
                return true;
            }
            state.ExtraPage = true;
        }
        state.Status = "Checking older pages for overlap.";
        return false;
    }

    private static string[] Split(string ids) { return ids.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries); }

    internal static string NewListingPath(Source source)
    {
        // The imported label can carry Reddit's spelling. A custom name must not become a URL path.
        var label = source.Label ?? "";
        var name = Regex.IsMatch(label, @"\Ar/[A-Za-z0-9_]{1,21}\z") && string.Equals(label, "r/" + source.Name, StringComparison.OrdinalIgnoreCase)
            ? label.Substring(2) : source.Name;
        return "/r/" + name + "/new/";
    }

    private async Task<PageResult> Fetch(CoreWebView2 view, string path, string source, bool first)
    {
        // Limit the WebView to the two known Reddit listing paths, including stored continuation values.
        if (!string.Equals(path, "/r/" + source + "/new/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/svc/shreddit/community-more-posts/new/?", StringComparison.Ordinal))
            return new PageResult { problem = "Reddit changed the continuation link.", stale = true };

        // Await the page's fetch promise through the local WebView API. There is no remote debugging listener.
        var expression = script.Trim() + "(" + json.Serialize(path) + "," + json.Serialize(source) + "," + (first ? "true" : "false") + ",(" + articleBody + "))";
        var request = json.Serialize(new { expression, awaitPromise = true, returnByValue = true });
        var call = view.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request);
        if (await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(25))) != call)
            return new PageResult { problem = "Reddit did not answer within 25 seconds." };

        var envelope = json.Deserialize<Dictionary<string, object>>(await call);
        if (envelope.ContainsKey("exceptionDetails") || !envelope.ContainsKey("result"))
            return new PageResult { problem = "Could not read the Reddit page in WebView2." };
        var value = (Dictionary<string, object>)envelope["result"];
        if (!value.ContainsKey("value")) return new PageResult { problem = "Reddit returned an unreadable page." };
        var result = json.ConvertToType<PageResult>(value["value"]);
        if (result.problem != null) return result;
        if (result.posts == null || result.posts.Count == 0)
            return new PageResult { problem = "Reddit returned no readable posts." };
        foreach (var post in result.posts)
        {
            // Validate links again at the native storage boundary before generating inbox HTML.
            if (post.id == null || !Regex.IsMatch(post.id, @"^t3_[a-z0-9]+$") || string.IsNullOrWhiteSpace(post.title)
                || post.path == null || !post.path.StartsWith("/r/" + source + "/comments/" + post.id.Substring(3) + "/", StringComparison.OrdinalIgnoreCase))
                return new PageResult { problem = "Reddit returned an unexpected post card." };
            post.originalUrl = "https://www.reddit.com" + post.path;
            DateTimeOffset created;
            post.publicationKnown = DateTimeOffset.TryParse(post.created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out created);
            if (!post.publicationKnown) created = DateTimeOffset.UtcNow;
            post.created = created.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }
        return result;
    }
}
