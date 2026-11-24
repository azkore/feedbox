using System;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

internal sealed class FeedCheckStatus
{
    public bool Running { get; private set; }
    public string Message { get; private set; }

    public FeedCheckStatus(bool running, string message)
    {
        Running = running;
        Message = message;
    }
}

internal sealed class Inbox : IDisposable
{
    private readonly HttpListener listener = new HttpListener();
    private readonly Store store;
    private readonly Func<string, Task<string>> check;
    private readonly Func<bool, bool, bool> startBatch;
    private readonly Func<FeedCheckStatus> batchStatus;
    private readonly string host;
    private readonly string token = Guid.NewGuid().ToString("N");
    private readonly string script = Resource("inbox.js");
    private readonly string style = Resource("inbox.css");
    private readonly byte[] inter = BinaryResource("InterVariable.woff2");
    private readonly byte[] merriweather = BinaryResource("Merriweather-Regular.ttf");
    public readonly string Url;

    public Inbox(Store store, int port, Func<string, Task<string>> check,
        Func<bool, bool, bool> startBatch = null, Func<FeedCheckStatus> batchStatus = null)
    {
        this.store = store;
        this.check = check;
        this.startBatch = startBatch;
        this.batchStatus = batchStatus ?? (() => new FeedCheckStatus(false, ""));
        host = "127.0.0.1:" + port;
        Url = "http://" + host + "/";
        // Ask HTTP.sys for only the loopback prefix. No LAN listener or URL reservation is needed.
        listener.Prefixes.Add(Url);
        listener.Start();
        Task.Run((Func<Task>)Listen);
    }

    private async Task Listen()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            // Keep the HTTP loop responsive if a client leaves a request half-written.
            var request = context;
            ThreadPool.QueueUserWorkItem(state => Serve(request));
        }
    }

    private async void Serve(HttpListenerContext context)
    {
        var request = context.Request;
        try
        {
            // Block alternate Host headers and mutations originating from another web page.
            if (request.Headers["Host"] != host) { Reply(context, 403, "Wrong host."); return; }
            // Browsers encode these parameters in UTF-8; HttpListener.QueryString uses the Windows code page here.
            var rawUrl = request.RawUrl ?? "";
            var queryStart = rawUrl.IndexOf('?');
            var query = HttpUtility.ParseQueryString(queryStart < 0 ? "" : rawUrl.Substring(queryStart + 1), Encoding.UTF8);
            ArticleFilter dates;
            if (!ArticleFilter.TryCreate(query["from"], query["to"], out dates, query["q"]))
            { Reply(context, 400, "Choose a valid date range and a search of at most 120 characters."); return; }
            dates = dates.WithFlags(ViewFlags(query));
            if (request.HttpMethod == "GET" && (request.Url.AbsolutePath == "/" || request.Url.AbsolutePath == "/new-articles"))
            {
                Reply(context, 200, Render(request.Url.AbsolutePath, query, dates));
                return;
            }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/check-status")
            {
                Reply(context, 200, RenderCheckStatus(SourceName(query["feed"]), GroupId(query["group"]), store.IsPaused));
                return;
            }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/check-all-status")
            {
                Reply(context, 200, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(batchStatus()), "application/json; charset=utf-8");
                return;
            }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/unread-counts")
            {
                var counts = store.UnreadCounts(dates);
                var json = new System.Web.Script.Serialization.JavaScriptSerializer();
                var archive = query["feed"] == null ? null : ArchiveCountSummary(SourceName(query["feed"]), GroupId(query["group"]));
                Reply(context, 200, json.Serialize(new { feeds = counts, total = counts.Values.Sum(), arrival = store.LatestArrival(), archive,
                     icons = store.Icons.Available(), pausedFeeds = store.PausedFeeds().ToArray(),
                     warnings = store.FeedCheckHealth().Where(row => HasCheckWarning(row[1], row[2])).Select(row => row[0]).ToArray() }),
                    "application/json; charset=utf-8");
                return;
            }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/feed-icon")
            {
                var sourceKey = SourceName(query["feed"]);
                var icon = sourceKey != "" && store.HasSource(sourceKey) ? store.Icons.Read(sourceKey) : null;
                if (icon == null) { Reply(context, 404, "No feed icon."); return; }
                ReplyBytes(context, 200, icon, "image/png", cacheSeconds: 86400);
                return;
            }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/sidebar-counts")
            {
                // This request is made only when opening a sidebar tooltip.
                var counts = ArchiveCountSummary(SourceName(query["feed"]), GroupId(query["group"]), DateTimeOffset.UtcNow);
                var json = new System.Web.Script.Serialization.JavaScriptSerializer();
                Reply(context, 200, json.Serialize(counts), "application/json; charset=utf-8");
                return;
            }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/rss-body")
            {
                var id = query["id"] ?? "";
                var body = IsRssKey(id) ? store.RssBody(id) : null;
                if (body == null) { Reply(context, 404, "Article body not found."); return; }
                ReplyBytes(context, 200, Encoding.UTF8.GetBytes(RssFrame(body)), "text/html; charset=utf-8", rssFrame: true);
                return;
            }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/feed-source")
            {
                var key = SourceName(query["feed"]);
                var rssSource = IsRssKey(key) ? store.RssToCheck(key) : null;
                if (rssSource == null) { Reply(context, 404, "RSS or Atom feed not found."); return; }
                // Load the URL on demand and use its version to detect edits made in another tab.
                var json = new System.Web.Script.Serialization.JavaScriptSerializer();
                Reply(context, 200, json.Serialize(new { url = rssSource.FeedUrl, version = Store.RssKey(rssSource.FeedUrl) }), "application/json; charset=utf-8");
                return;
            }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/subscriptions.opml")
            {
                context.Response.Headers["Content-Disposition"] = "attachment; filename=feedbox.opml";
                ReplyBytes(context, 200, Opml.Export(store.RssSubscriptions(), store.Groups()), "text/x-opml; charset=utf-8");
                return;
            }
            // Serve only named embedded assets, never a file path supplied by the client.
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/inbox.js")
            { Reply(context, 200, script, "text/javascript; charset=utf-8"); return; }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/inbox.css")
            { Reply(context, 200, style, "text/css; charset=utf-8"); return; }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/fonts/inter.woff2")
            { ReplyBytes(context, 200, inter, "font/woff2"); return; }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/fonts/merriweather.ttf")
            { ReplyBytes(context, 200, merriweather, "font/ttf"); return; }
            if (request.HttpMethod != "POST") { Reply(context, 405, "Method not allowed."); return; }
            var maxBody = request.Url.AbsolutePath == "/import-opml" ? Opml.MaxBytes * 3 : 4096;
            if (request.ContentLength64 > maxBody) { Reply(context, 413, "The upload is too large."); return; }
            if (request.Headers["Origin"] != Url.TrimEnd('/') || request.ContentLength64 < 0
                || request.ContentType == null || !request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            { Reply(context, 403, "Use the local inbox to make changes."); return; }

            var bytes = new byte[(int)request.ContentLength64];
            for (int i = 0; i < bytes.Length;)
            {
                var read = request.InputStream.Read(bytes, i, bytes.Length - i);
                if (read == 0) { Reply(context, 400, "Incomplete request."); return; }
                i += read;
            }
            var form = HttpUtility.ParseQueryString(Encoding.UTF8.GetString(bytes));
            if (form["token"] != token) { Reply(context, 403, "Reload the inbox and try again."); return; }
            if (!ArticleFilter.TryCreate(form["from"], form["to"], out dates, form["q"]))
            { Reply(context, 400, "Choose a valid date range and a search of at most 120 characters."); return; }
            dates = dates.WithFlags(ViewFlags(form));
            var group = GroupId(form["group"]);
            // Sidebar actions name their target separately from the view to return to.
            var targetFeed = form["target-feed"];
            var actionFeed = SourceName(targetFeed ?? form["feed"]);
            string result = "";

            // Only an explicit read/unread form can change read state.
            switch (request.Url.AbsolutePath)
            {
                case "/publisher-preview":
                    // Only look up the original URL of a stored RSS article, never an address supplied by the request.
                    var previewId = form["id"] ?? "";
                    var previewUrl = IsRssKey(previewId) ? await PublisherPreview.Lookup(store.RssArticleUrl(previewId)) : null;
                    if (previewUrl == null) { context.Response.StatusCode = 204; return; }
                    Reply(context, 200, previewUrl.AbsoluteUri, "text/plain; charset=utf-8");
                    return;
                case "/source":
                    var name = (form["name"] ?? "").Trim();
                    if (name.StartsWith("r/", StringComparison.OrdinalIgnoreCase)) name = name.Substring(2);
                    if (!Regex.IsMatch(name, @"^[A-Za-z0-9_]{1,21}$")) { Reply(context, 400, "Enter a subreddit name."); return; }
                    int initialCount = 100;
                    string since = "";
                    // Use separate field names from the reading filters carried by this form.
                    if (form["reddit-start"] == "date")
                    {
                        ArticleFilter start;
                        if (!ArticleFilter.TryCreate(form["reddit-since"], "", out start) || start.StartUtc == "")
                        { Reply(context, 400, "Enter a valid collection start date."); return; }
                        since = start.StartUtc;
                    }
                    else if (form["reddit-start"] == "count")
                    {
                        if (!int.TryParse(form["reddit-count"], NumberStyles.None, CultureInfo.InvariantCulture, out initialCount) || initialCount < 1 || initialCount > 1000)
                        { Reply(context, 400, "Enter a starting count between 1 and 1,000."); return; }
                    }
                    else if (form["reddit-start"] != null) { Reply(context, 400, "Choose a count or start date."); return; }
                    store.AddSource(name, group, initialCount, since);
                    break;
                case "/rss-source":
                    Uri url;
                    if (!RssCollector.TryFeedUrl((form["url"] ?? "").Trim(), out url))
                    { Reply(context, 400, "Enter a valid HTTP or HTTPS feed URL."); return; }
                    // Select the new subscription and show the outcome of its first check.
                    form["feed"] = store.AddRss(url, group);
                    await check(form["feed"]);
                    break;
                case "/import-opml":
                    try
                    {
                        int skipped;
                        var entries = Opml.Read(Convert.FromBase64String(form["opml"] ?? ""), out skipped);
                        var imported = store.ImportRss(entries, form["new-only"] == "1");
                        result = "&imported=" + imported.Item1.ToString(CultureInfo.InvariantCulture)
                            + "&existing=" + imported.Item2.ToString(CultureInfo.InvariantCulture)
                            + "&skipped=" + skipped.ToString(CultureInfo.InvariantCulture);
                    }
                    catch (InvalidDataException ex) { Reply(context, 400, ex.Message); return; }
                    catch (System.Xml.XmlException) { Reply(context, 400, "The OPML file could not be read."); return; }
                    catch (FormatException) { Reply(context, 400, "The OPML file could not be read."); return; }
                    break;
                case "/group":
                    var groupName = (form["name"] ?? "").Trim();
                    if (groupName.Length < 1 || groupName.Length > 80 || groupName.Any(char.IsControl))
                    { Reply(context, 400, "Enter a group name, up to 80 characters."); return; }
                    group = store.AddGroup(groupName);
                    form["feed"] = "";
                    break;
                case "/move-feed":
                    var movedFeed = SourceName(form["moved-feed"]);
                    int fromGroup;
                    var toGroup = GroupId(form["to-group"]);
                    if (movedFeed == "" || toGroup == 0 || !int.TryParse(form["from-group"], NumberStyles.None, CultureInfo.InvariantCulture, out fromGroup))
                    { Reply(context, 400, "Choose a feed and destination group."); return; }
                    if (!store.MoveFeed(movedFeed, fromGroup, toGroup))
                    { Reply(context, 409, "The feed or group changed. Refresh the inbox and try again."); return; }
                    // A selected feed remains selected at its new location.
                    if (form["feed"] == movedFeed && group == fromGroup) group = toGroup;
                    break;
                case "/rename-feed":
                    var renamed = SourceName(form["edited-feed"]);
                    var label = (form["label"] ?? "").Trim();
                    if (renamed == "" || label.Length > 120 || label.Any(char.IsControl) || !store.RenameFeed(renamed, label))
                    { Reply(context, 400, "Choose a feed and enter a name of up to 120 characters."); return; }
                    break;
                case "/edit-source":
                    var editedSource = SourceName(form["edited-feed"]);
                    Uri replacement;
                    if (!IsRssKey(editedSource) || !RssCollector.TryFeedUrl((form["url"] ?? "").Trim(), out replacement))
                    { Reply(context, 400, "Enter a valid HTTP or HTTPS feed URL."); return; }
                    var edit = store.EditRssSource(editedSource, form["version"], replacement);
                    if (edit == Store.RssSourceEdit.Missing) { Reply(context, 404, "RSS or Atom feed not found."); return; }
                    if (edit == Store.RssSourceEdit.Stale) { Reply(context, 409, "The source URL changed. Reopen Edit source to try again."); return; }
                    if (edit == Store.RssSourceEdit.Duplicate) { Reply(context, 409, "Another subscription already uses this URL."); return; }
                    break;
                case "/reorganize-feed":
                    var reorganized = SourceName(form["edited-feed"]);
                    var memberships = (form.GetValues("membership") ?? new string[0]).Select(GroupId).ToArray();
                    if (reorganized == "" || memberships.Any(groupId => groupId == 0) || !store.ReorganizeFeed(reorganized, memberships))
                    { Reply(context, 400, "Choose a feed and valid groups."); return; }
                    // Keep the current feed open if its previous folder was unchecked.
                    if (form["feed"] == reorganized && !memberships.Contains(group)) group = memberships.FirstOrDefault();
                    break;
                case "/feed-shortcut":
                    var mode = form["open"];
                    if ((mode != "original" && mode != "discussion") || !store.SetOpenDiscussion(SourceName(form["edited-feed"]), mode == "discussion"))
                    { Reply(context, 400, "Choose a shortcut for an RSS or Atom feed."); return; }
                    break;
                case "/feed-settings":
                    var settingsFeed = SourceName(form["edited-feed"]);
                    var settingsLabel = (form["label"] ?? "").Trim();
                    var settingsGroups = (form.GetValues("membership") ?? new string[0]).Select(GroupId).ToArray();
                    if (settingsFeed == "" || settingsLabel.Length > 120 || settingsLabel.Any(char.IsControl) || settingsGroups.Any(groupId => groupId == 0))
                    { Reply(context, 400, "Choose a feed, valid groups, and a name of up to 120 characters."); return; }
                    // Only RSS and Atom subscriptions have an editable URL and open action.
                    Uri settingsUrl = null;
                    var openMode = form["open"];
                    if (IsRssKey(settingsFeed) && (!RssCollector.TryFeedUrl((form["url"] ?? "").Trim(), out settingsUrl)
                        || (openMode != "original" && openMode != "discussion")))
                    { Reply(context, 400, "Enter a valid HTTP or HTTPS feed URL and choose an open action."); return; }
                    var saved = store.SaveFeedSettings(settingsFeed, settingsLabel, settingsGroups, settingsUrl, form["version"], openMode == "discussion");
                    if (saved == Store.RssSourceEdit.Missing) { Reply(context, 404, "The feed is no longer subscribed."); return; }
                    if (saved == Store.RssSourceEdit.Stale) { Reply(context, 409, "The source URL changed. Reopen Settings to try again."); return; }
                    if (saved == Store.RssSourceEdit.Duplicate) { Reply(context, 409, "Another subscription already uses this URL."); return; }
                    if (saved == Store.RssSourceEdit.InvalidGroups) { Reply(context, 400, "One of the groups is no longer available."); return; }
                    // Keep the feed open if its former group was unchecked.
                    if (group != 0 && form["feed"] == settingsFeed && !settingsGroups.Contains(group)) group = settingsGroups.FirstOrDefault();
                    break;
                case "/delete-feed":
                    var deletedFeed = SourceName(form["edited-feed"]);
                    if (deletedFeed == "" || !store.DeleteFeed(deletedFeed))
                    { Reply(context, 404, "The feed is no longer in the inbox."); return; }
                    // If the deleted feed was open, show its group or All feeds instead.
                    if (SourceName(form["feed"]) == deletedFeed) form["feed"] = "";
                    break;
                case "/collection":
                    if (form["action"] != "pause" && form["action"] != "resume") { Reply(context, 400, "Choose pause or resume."); return; }
                    var pause = form["action"] == "pause";
                    // An explicit target can never fall back to the global setting if it is stale.
                    if (targetFeed != null || !string.IsNullOrEmpty(form["feed"]))
                    {
                        if (actionFeed == "" || !store.SetFeedPaused(actionFeed, pause)) { Reply(context, 404, "This feed is no longer subscribed."); return; }
                    }
                    else
                    {
                        store.SetPaused(pause);
                        if (!pause) check(null);
                    }
                    break;
                case "/read":
                    var id = form["id"] ?? "";
                    if (!ArticleId(id)) { Reply(context, 400, "Invalid post ID."); return; }
                    if (!store.SetRead(id, form["mark"] != "unread"))
                    {
                        if (store.PublishedAt(id) == "") Reply(context, 404, "This article is no longer in the inbox.");
                        else Reply(context, 409, "This article is marked. Use Numpad + or open it with V to mark it read.");
                        return;
                    }
                    if (form["inline"] == "1") { context.Response.StatusCode = 204; return; }
                    break;
                case "/mark-article":
                    var article = form["id"] ?? "";
                    var action = form["action"];
                    if (!ArticleId(article) || (action != "toggle" && action != "opened" && action != "mark")) { Reply(context, 400, "Invalid article or action."); return; }
                    var state = store.UpdateMark(article, action == "opened", toggle: action == "toggle");
                    if (state == null) { Reply(context, 404, "This article is no longer in the inbox."); return; }
                    Reply(context, 200, "{\"marked\":" + state[0] + ",\"read\":" + (state[1] == null ? "0" : "1") + "}", "application/json; charset=utf-8");
                    return;
                case "/mark-feed-read":
                    // A bulk read always targets one subscription, even from an aggregate view.
                    if (actionFeed == "" || !store.HasSource(actionFeed)) { Reply(context, 400, "Select a subscribed feed."); return; }
                    long through;
                    if (!long.TryParse(form["through"], NumberStyles.None, CultureInfo.InvariantCulture, out through))
                    { Reply(context, 400, "Reload the feed before marking it read."); return; }
                    var batch = Guid.NewGuid().ToString("N");
                    long marked = store.MarkFeedRead(actionFeed, dates, batch, through);
                    result = "&marked=" + marked.ToString(CultureInfo.InvariantCulture) + (marked > 0 ? "&batch=" + batch : "");
                    if (targetFeed != null) result += "&receipt-feed=" + Uri.EscapeDataString(actionFeed);
                    break;
                case "/undo-feed-read":
                    var undoBatch = form["batch"] ?? "";
                    if (actionFeed == "" || !IsBatch(undoBatch)) { Reply(context, 400, "That read receipt is not available."); return; }
                    result = "&restored=" + store.UndoFeedRead(actionFeed, undoBatch).ToString(CultureInfo.InvariantCulture);
                    break;
                case "/check":
                    var batchOutcome = await check(null);
                    if (form["inline"] == "1") { Reply(context, 200, batchOutcome, "text/plain; charset=utf-8"); return; }
                    break;
                case "/check-all":
                    if (group != 0 || !string.IsNullOrEmpty(form["feed"])) { Reply(context, 400, "Open All feeds to run this check."); return; }
                    bool rssSelected = form["rss"] == "1", redditSelected = form["reddit"] == "1";
                    if (!rssSelected && !redditSelected) { Reply(context, 400, "Select RSS or Reddit first."); return; }
                    if (startBatch == null || !startBatch(rssSelected, redditSelected))
                    { Reply(context, 409, "A check of all feeds is already running or collection is paused."); return; }
                    // Large batches can outlast an HTTP request. Report progress from a separate endpoint.
                    if (form["inline"] == "1") { Reply(context, 202, "Check queued."); return; }
                    break;
                case "/check-feed":
                    // An invalid target must never fall back to checking the scheduled batch.
                    if (actionFeed == "" || !store.HasSource(actionFeed)) { Reply(context, 400, "Select a subscribed feed."); return; }
                    var outcome = await check(actionFeed);
                    if (form["inline"] == "1") { Reply(context, 200, outcome, "text/plain; charset=utf-8"); return; }
                    break;
                case "/delete-read":
                    if (form["confirmed"] != "1") { Reply(context, 400, "Confirm deletion in the inbox first."); return; }
                    DateTimeOffset cutoff;
                    if (!DateTimeOffset.TryParseExact(form["cutoff"], "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out cutoff) || cutoff > DateTimeOffset.UtcNow)
                    { Reply(context, 400, "Reload the inbox before deleting read articles."); return; }
                    var deleted = store.DeleteReadAndCompact(cutoff.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    result = "&deleted=" + deleted.Item1.ToString(CultureInfo.InvariantCulture) + (deleted.Item2 ? "" : "&compact=0");
                    break;
                default: Reply(context, 404, "Not found."); return;
            }
            context.Response.StatusCode = 303;
            context.Response.RedirectLocation = ViewUrl(SourceName(form["feed"]), form["all"] != "1", form["oldest"] == "1", group, dates) + result;
        }
        // HTTP.sys reports a client disconnect as HttpListenerException, including after sending headers.
        catch (HttpListenerException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception) { TryFinishResponse(() => Reply(context, 500, "The inbox could not complete the request.")); }
        finally { TryFinishResponse(() => context.Response.Close()); }
    }

    private static void TryFinishResponse(Action finish)
    {
        // A failed error reply or close must not escape the async request handler. Once headers are sent,
        // HttpListener can reject changes to the response with InvalidOperationException.
        try { finish(); }
        catch (HttpListenerException) { }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    private string Render(string path, NameValueCollection query, ArticleFilter dates)
    {
        bool unread = (dates.Flags & ArticleFilter.Read) == 0;
        bool oldest = query["oldest"] == "1";
        bool paused = store.IsPaused;
        var source = SourceName(query["feed"]);
        var group = GroupId(query["group"]);
        var before = query["before"] ?? "";
        var beforeId = query["id"] ?? "";
        DateTimeOffset cursor;
        if (!ArticleId(beforeId) || before.Length > 40
            || !DateTimeOffset.TryParse(before, CultureInfo.InvariantCulture, DateTimeStyles.None, out cursor)) before = "";
        var sources = store.Sidebar();
        var arrival = store.LatestArrival();
        if (path == "/new-articles")
        {
            long since;
            if (!long.TryParse(query["since"], NumberStyles.None, CultureInfo.InvariantCulture, out since)) since = arrival;
            since = Math.Min(since, arrival);
            var fresh = store.Items(unread, source, limit: 100, oldest: oldest, group: group, dates: dates,
                since: since, through: arrival, edge: before, edgeId: beforeId);
            var delta = new StringBuilder();
            var nextArrival = fresh.Count == 100 ? fresh.Last()[10] : arrival.ToString(CultureInfo.InvariantCulture);
            delta.Append("<div id=entries data-arrival='").Append(nextArrival).Append("'>");
            AppendArticles(delta, fresh, sources.ToDictionary(row => row[0]), unread, source, oldest, group, dates, false);
            return delta.Append("</div>").ToString();
        }
        // Start with the same date-scoped snapshot that the browser will use to refresh the badges.
        var counts = store.UnreadCounts(dates);
        Func<string, long> countFor = name => counts.ContainsKey(name) ? counts[name] : 0;
        var byName = sources.ToDictionary(s => s[0]);
        var pausedFeeds = store.PausedFeeds();
        var icons = store.Icons.Available();
        var groups = store.Groups();
        var selectedGroup = groups.Find(g => g.Id == group);
        if (selectedGroup == null) group = 0;
        string title;
        title = source == "" ? (selectedGroup == null ? "All feeds" : selectedGroup.Name)
            : byName.ContainsKey(source) ? byName[source][3] : source;

        // Keep feed navigation independent of the centered reading stream.
        var html = new StringBuilder("<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>feedbox</title><link rel=stylesheet href='/inbox.css'><script src='/inbox.js' defer></script>");
        html.Append("<body data-unread='").Append(unread ? "1" : "0").Append("' data-flags='").Append(dates.Flags).Append("'><aside>");
        AppendSidebarTools(html, unread, source, oldest, group, dates);
        html.Append("<nav aria-label='Feeds'><div class=feed-pins>");
        html.Append(FeedLink("", "All feeds", source, unread, counts.Values.Sum(), oldest, 0, group, dates));
        html.Append("<div class=pinned-group hidden></div><div class=pinned-feed hidden></div></div>");
        var grouped = new System.Collections.Generic.HashSet<string>();
        // Use the saved folder order and sort feeds by their displayed names.
        foreach (var folder in groups)
        {
            var members = folder.Members.Where(byName.ContainsKey).Select(name => byName[name])
                .OrderBy(row => row[3], StringComparer.OrdinalIgnoreCase).ThenBy(row => row[0], StringComparer.Ordinal).ToList();
            var unreadCount = members.Sum(row => countFor(row[0]));
            var memberList = "group-members-" + folder.Id.ToString(CultureInfo.InvariantCulture);
            html.Append("<section class=feed-group><div class=group-heading><button class=group-toggle type=button aria-expanded=true aria-controls='")
                .Append(memberList).Append("' aria-label='Toggle ").Append(E(folder.Name)).Append("'></button>")
                .Append(FeedLink("", folder.Name, source, unread, unreadCount, oldest, folder.Id, group, dates))
                .Append("</div><div class=group-members id='").Append(memberList).Append("'>");
            foreach (var member in members)
            {
                grouped.Add(member[0]);
                html.Append(FeedLink(member[0], member[3], source, unread, countFor(member[0]), oldest, folder.Id, group, dates, member[5] == "1",
                     icons.Contains(member[0]), HasCheckWarning(member[1], member[4]), pausedFeeds.Contains(member[0])));
            }
            // Keep all member links in alphabetical order when the reader reveals the zero-unread feeds.
            html.Append("<button type=button class=more-feeds aria-expanded=false hidden></button></div></section>");
        }
        var ungrouped = sources.Where(s => !grouped.Contains(s[0])).ToList();
        if (ungrouped.Count > 0)
        {
            if (groups.Count > 0) html.Append("<div class=feed-section-label>Ungrouped</div>");
            html.Append("<div class=ungrouped-feeds id=ungrouped-feeds>");
            foreach (var s in ungrouped) html.Append(FeedLink(s[0], s[3], source, unread, countFor(s[0]), oldest, 0, group, dates, s[5] == "1",
                 icons.Contains(s[0]), HasCheckWarning(s[1], s[4]), pausedFeeds.Contains(s[0])));
            html.Append("<button type=button class=more-feeds aria-expanded=false hidden></button></div>");
        }
        if (groups.Count > 0) html.Append("<div hidden>").Append(Form("move-feed", "", unread, source, oldest, group, dates)).Append("</div>");
        html.Append("</nav><div id=sidebar-count-tip role=tooltip hidden></div><div class=sidebar-resize role=separator aria-label='Feed sidebar width' aria-orientation=vertical aria-valuemin=160 aria-valuemax=560 aria-valuenow=272 tabindex=0></div></aside>");

        html.Append("<main><header class=feed-header><div class=heading><div class=heading-title><h1")
            .Append(pausedFeeds.Contains(source) ? " class=feed-paused" : "").Append('>').Append(E(title)).Append("</h1> ");
        AppendHeadingCounts(html, source, group);
        html.Append("</div><div class=toolbar>");
        html.Append(StateControl(source, oldest, group, dates));
        html.Append("<a class=sort href='").Append(E(ViewUrl(source, unread, !oldest, group, dates))).Append("' title='Switch to ")
            .Append(oldest ? "newest first'>Oldest first" : "oldest first'>Newest first").Append("</a>");
        html.Append(DateControl(source, unread, oldest, group, dates));
        html.Append(SearchControl(source, unread, oldest, group, dates));
        // Aggregate views use the global switch; a subscribed feed uses only its own switch.
        if (source == "" || byName.ContainsKey(source))
        {
            var contextPaused = source == "" ? paused : pausedFeeds.Contains(source);
            var scope = source == "" ? " collection" : " this feed";
            html.Append(Form("collection", contextPaused ? IconButton("&#x25B6;&#xFE0E;", "Resume" + scope, " name=action value=resume")
                 : IconButton("&#x23F8;&#xFE0E;", "Pause" + scope, " name=action value=pause"), unread, source, oldest, group, dates));
        }
        if (byName.ContainsKey(source))
        {
            html.Append(Form("check-feed", IconButton("&#x21BB;", "Check this feed"), unread, source, oldest, group, dates));
            if ((dates.Flags & ArticleFilter.Inbox) == ArticleFilter.Inbox) html.Append(Form("mark-feed-read", "<input type=hidden name=through value='" + arrival.ToString(CultureInfo.InvariantCulture) + "'>"
                + IconButton("&#x2713;", "Mark all as read", tooltip: dates.Active || dates.Search != "" ? "Mark all matching articles as read in this feed" : "Mark all as read in this feed"), unread, source, oldest, group, dates));
        }
        else if (!paused && group == 0 && source == "" && startBatch != null)
        {
            html.Append(Form("check-all", "<span class=check-all-controls>" + IconButton("&#x21BB;", "Check selected feed types")
                + "<details class=check-types><summary aria-label='Choose feed types' title='Choose feed types'>&#x25be;</summary>"
                + "<div><label><input type=checkbox name=rss value=1 checked> RSS</label>"
                + "<label><input type=checkbox name=reddit value=1> Reddit</label></div></details></span>", unread, source, oldest, group, dates));
        }
        else if (!paused) html.Append(Form("check", IconButton("&#x21BB;", "Check now"), unread, source, oldest, group, dates));
        bool allFeeds = source == "" && group == 0;
        var allStatus = allFeeds ? batchStatus() : new FeedCheckStatus(false, "");
        html.Append("<details class=check-status data-status-url='").Append(E("/check-status" + ViewUrl(source, true, false, group).Substring(1)))
            .Append("'><summary>Check status</summary>").Append(RenderCheckStatus(source, group, paused))
            .Append("</details></div></div><p id=check-feedback role=status").Append(allFeeds ? " data-check-batch" : "")
            .Append(" data-running=").Append(allStatus.Running ? "1" : "0").Append('>')
            .Append(E(allStatus.Message)).Append("</p><p id=key-status role=status></p></header>");
        long markedCount;
        var batch = query["batch"] ?? "";
        var receiptFeed = SourceName(query["receipt-feed"] ?? source);
        if (byName.ContainsKey(receiptFeed) && long.TryParse(query["marked"], NumberStyles.None, CultureInfo.InvariantCulture, out markedCount))
        {
            // Keep the receipt and Undo attached to the feed the reader acted on.
            var feedNote = receiptFeed == source ? "" : " in " + E(byName[receiptFeed][3]);
            html.Append("<div role=status class=bulk-receipt>").Append(markedCount == 0 ? "No unread articles to mark." :
                "Marked " + markedCount.ToString("N0", CultureInfo.CurrentCulture) + (markedCount == 1 ? " article" : " articles") + " as read" + feedNote + ".");
            if (IsBatch(batch) && store.CanUndoFeedRead(receiptFeed, batch))
                html.Append(" ").Append(Form("undo-feed-read", "<input type=hidden name=batch value='" + E(batch) + "'>"
                    + "<input type=hidden name=target-feed value='" + E(receiptFeed) + "'><button>Undo</button>", unread, source, oldest, group, dates));
            html.Append("</div>");
        }
        long restored;
        if (long.TryParse(query["restored"], NumberStyles.None, CultureInfo.InvariantCulture, out restored))
            html.Append("<p role=status class=bulk-receipt>").Append(restored == 0 ? "No articles to restore." :
                "Restored " + restored.ToString("N0", CultureInfo.CurrentCulture) + (restored == 1 ? " unread article." : " unread articles.")).Append("</p>");
        int importedCount, existingCount, skippedCount;
        if (int.TryParse(query["imported"], NumberStyles.None, CultureInfo.InvariantCulture, out importedCount)
            && int.TryParse(query["existing"], NumberStyles.None, CultureInfo.InvariantCulture, out existingCount)
            && int.TryParse(query["skipped"], NumberStyles.None, CultureInfo.InvariantCulture, out skippedCount))
            html.Append("<p role=status class=bulk-receipt>OPML: ").Append(importedCount).Append(" added, ").Append(existingCount)
                .Append(" already subscribed, ").Append(skippedCount).Append(" unsupported or invalid.</p>");
        long deletedCount;
        if (long.TryParse(query["deleted"], NumberStyles.None, CultureInfo.InvariantCulture, out deletedCount))
        {
            html.Append("<p role=status>").Append(deletedCount == 0 ? "No read articles to delete." : "Deleted " + deletedCount + " read articles.");
            if (deletedCount > 0) html.Append(query["compact"] == "0" ? " SQLite could not finish compacting the file." : " Database compacted.");
            html.Append("</p>");
        }
        // Keep the drag targets with the cards so the controls above them keep their own width.
        html.Append("<div class=reader-cards>");
        foreach (var edge in new[] { "left", "right" })
            html.Append("<div class=reader-resize data-edge='").Append(edge).Append("' role=separator aria-orientation=vertical aria-label='")
                .Append(edge == "left" ? "Left" : "Right").Append(" card edge' aria-controls=entries aria-valuemin=8 aria-valuenow=22 tabindex=0 title='Drag to resize cards'></div>");
        html.Append("<div id=entries data-arrival='").Append(arrival.ToString(CultureInfo.InvariantCulture)).Append("'>");

        // Use the original URL supplied by the collector. Opening it does not mark the post read.
        var anchor = query["at"] ?? "";
        var anchorTime = ArticleId(anchor) && before == "" ? store.PublishedAt(anchor) : "";
        var items = anchorTime == "" ? null : store.Items(unread, source, anchorTime, anchor, oldest: oldest, group: group, dates: dates, inclusive: true);
        // An article already read, removed, or outside the view cannot serve as the resume position.
        if (items == null || items.Count == 0 || items[0][0] != anchor)
            items = store.Items(unread, source, before, beforeId, oldest: oldest, group: group, dates: dates);
        if (items.Count == 0) html.Append(dates.Search != "" ? "<p>No articles match this search in the current view.</p>" : dates.Active ? (unread ? "<p>No unread posts in this date range.</p>" : "<p>No posts in this date range.</p>")
            : (unread ? "<p>No unread posts.</p>" : "<p>No posts collected yet.</p>"));
        AppendArticles(html, items, byName, unread, source, oldest, group, dates, true);
        html.Append("</div>");
        if (items.Count > 100)
        {
            // The cursor belongs to the last rendered row and remains valid after it is marked read.
            var last = items[99];
            var url = ViewUrl(source, unread, oldest, group, dates) + "&before=" + Uri.EscapeDataString(last[4]) + "&id=" + Uri.EscapeDataString(last[0]);
            html.Append("<a id=more href='").Append(E(url)).Append("'>More posts</a>");
        }
        html.Append("</div>").Append(FeedOptions(source, unread, oldest, group, dates, groups));
        return html.Append("</main></body></html>").ToString();
    }

    private object ArchiveCountSummary(string source, int group, DateTimeOffset? recentAsOf = null)
    {
        var counts = store.SidebarArticleCounts(source, group, recentAsOf);
        if (recentAsOf.HasValue)
            return new { marked = counts[0], read = counts[1], unread = counts[2] - counts[1], total = counts[2], last24h = counts[3] };
        return new { marked = counts[0], read = counts[1], unread = counts[2] - counts[1], total = counts[2] };
    }

    private void AppendHeadingCounts(StringBuilder html, string source, int group)
    {
        // The heading uses the same unfiltered scope as the sidebar tooltip.
        var counts = store.SidebarArticleCounts(source, group);
        html.Append("<span class=heading-counts><span data-count-part=marked").Append(counts[0] == 0 ? " hidden" : "")
            .Append("><span data-count=marked>").Append(counts[0]).Append("</span>/</span><span data-count-part=read")
            .Append(counts[1] == 0 ? " hidden" : "").Append("><span data-count=read>").Append(counts[1])
            .Append("</span>/<span data-count=unread>").Append(counts[2] - counts[1])
            .Append("</span>/</span><span data-count=total>").Append(counts[2]).Append("</span></span>");
    }

    private void AppendSidebarTools(StringBuilder html, bool unread, string source, bool oldest, int group, ArticleFilter dates)
    {
        // Keep management controls in a disclosure so opening them doesn't resize the feed list.
        html.Append("<div class=sidebar-header><strong class=brand>feed(in)box</strong><details class=sidebar-tools>")
            .Append("<summary aria-label='Sidebar options' title='Sidebar options'>&#x22EF;</summary><div class=sidebar-tools-panel>")
            .Append("<label class=show-all-label hidden><input class=show-all-feeds type=checkbox> Show all</label>")
            .Append("<label class=feed-sort-label hidden>Sort feeds<select class=feed-sort><option value=name>Grouped: by name</option><option value=group-count>Grouped: by count</option><option value=all-count>All feeds: by count</option></select></label>");
        html.Append("<details class=add-source><summary>Add source or group</summary>");
        html.Append(Form("source", "<label>Subreddit <input name=name required maxlength=23 placeholder='r/rss'></label>"
            + "<fieldset class=reddit-start><legend>Start collection</legend>"
            + "<label><input type=radio name=reddit-start value=count checked> Latest articles</label>"
            + "<label class=reddit-start-count>Number of articles <input type=number name=reddit-count value=100></label>"
            + "<label><input type=radio name=reddit-start value=date> From a date</label>"
            + "<label class=reddit-start-date>Start date (inclusive) <input type=date name=reddit-since></label>"
            + "</fieldset><button>Add</button>", unread, source, oldest, group, dates));
        html.Append(Form("rss-source", "<label>RSS or Atom URL <input type=url name=url required maxlength=2000 placeholder='https://example.com/feed/'></label> <button>Add feed</button>", unread, source, oldest, group, dates));
        html.Append(Form("import-opml", "<label>Import RSS/Atom subscriptions <input type=file name=opml-file accept='.opml,.xml,text/xml' required></label>"
            + "<label><input type=checkbox name=new-only value=1> Only collect new articles after import</label> <button>Import OPML</button>", unread, source, oldest, group, dates));
        html.Append("<p><a href='/subscriptions.opml' download>Export RSS/Atom OPML</a></p>");
        html.Append(Form("group", "<label>Group <input name=name required maxlength=80></label> <button>Add group</button>", unread, source, oldest, group, dates)).Append("</details>");

        // This global action deletes only the read articles present when the panel was rendered.
        var cutoff = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var readCount = store.ReadCount(cutoff);
        var fileSize = (store.DatabaseFileBytes / 1048576.0).ToString("N1", CultureInfo.CurrentCulture);
        html.Append("<details class=add-source><summary>Database</summary><p>Database file: ").Append(E(fileSize))
            .Append(" MiB<br>").Append(readCount).Append(" read articles</p>");
        html.Append(Form("delete-read", "<input type=hidden name=confirmed value=0><input type=hidden name=cutoff value='" + E(cutoff) + "'><button data-delete-count='" + readCount + "'" + (readCount == 0 ? " disabled" : "") + ">Delete read articles</button>", unread, source, oldest, group, dates));
        html.Append("</details></div></details></div>");
    }

    private void AppendArticles(StringBuilder html, System.Collections.Generic.IList<string[]> items,
        System.Collections.Generic.IDictionary<string, string[]> byName, bool unread, string source, bool oldest, int group, ArticleFilter dates, bool headings)
    {
        string lastDay = null;
        for (int i = 0; i < Math.Min(items.Count, 100); i++)
        {
            var post = items[i];
            DateTimeOffset published;
            var valid = DateTimeOffset.TryParse(post[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out published);
            var date = valid ? published.ToLocalTime().ToString("g") : "";
            var day = valid ? published.LocalDateTime.ToString("D") : "Unknown date";
            if (headings && day != lastDay) html.Append("<h2>").Append(E(day)).Append("</h2>");
            lastDay = day;
            html.Append("<article tabindex='-1' id='").Append(E(post[0])).Append("' data-post='").Append(E(post[0])).Append("' data-source='").Append(E(post[1]));
            Uri discussion;
            if (byName.ContainsKey(post[1]) && byName[post[1]][5] == "1" && TryWebUrl(post[11], out discussion))
                html.Append("' data-discussion='").Append(E(discussion.AbsoluteUri));
            html
                .Append("' data-published='").Append(E(post[4])).Append("' data-day='").Append(E(day))
                .Append("' data-read='").Append(post[5] == null ? "0" : "1").Append("' data-marked='").Append(post[12]).Append('\'');
            if (IsRssKey(post[1]) && post[8] == "") html.Append(" data-publisher-preview");
            html.Append("><div class=article-inner>");
            // Both state controls live in the margin. Marked articles stay unread until unmarked or opened.
            var marked = post[12] == "1";
            var read = post[5] != null;
            var readTitle = marked ? "Marked articles stay unread" : read ? "Mark unread" : "Mark read";
            html.Append("<div class=article-state>").Append(Form("read", "<input type=hidden name=id value='" + E(post[0]) + "'>"
                + "<button type=submit class=article-read name=mark value=" + (read ? "unread" : "read")
                + " aria-label='Read' aria-pressed=" + (read ? "true" : "false") + " title='" + readTitle + "'"
                + (marked ? " disabled" : "") + "><span aria-hidden=true>&#x2713;</span></button>", unread, source, oldest, group, dates));
            html.Append("<button type=button class=article-bookmark aria-label='Marked' aria-pressed=")
                .Append(marked ? "true title='Unmark article'" : "false title='Mark article'")
                .Append("><span aria-hidden=true>&#x1F516;&#xFE0E;</span></button></div><h3>");
            Uri original;
            if (TryWebUrl(post[3], out original))
                html.Append("<a class=original target=_blank rel=noopener href='").Append(E(original.AbsoluteUri)).Append("'>").Append(E(post[2])).Append("</a>");
            else html.Append("<strong>").Append(E(post[2])).Append("</strong>");
            html.Append("</h3><small>").Append(E(byName.ContainsKey(post[1]) ? byName[post[1]][3] : post[1]));
            if (post[6] != "") html.Append(IsRssKey(post[1]) ? " &middot; " : " &middot; u/").Append(E(post[6]));
            html.Append(" &middot; ").Append(E(date)).Append("</small>");

            // Publisher HTML lives in a separate document, without access to the inbox's styles or app controls.
            if (IsRssKey(post[1]) && post[9] == "1")
                html.Append("<iframe class=rss-body title='Article body' loading=lazy sandbox='allow-same-origin allow-popups allow-popups-to-escape-sandbox' src='/rss-body?id=")
                    .Append(E(post[0])).Append("'></iframe>");
            else if (!ArticleBody.TryAppend(html, post[9]) && post[7] != "") AppendContext(html, post[7]);
            Uri image;
            if (Uri.TryCreate(post[8], UriKind.Absolute, out image) && image.Scheme == Uri.UriSchemeHttps && (image.Host == "i.redd.it" || image.Host == "preview.redd.it" || image.Host == "external-preview.redd.it"))
                html.Append("<img class=preview loading=lazy referrerpolicy=no-referrer alt='Post preview' src='").Append(E(image.AbsoluteUri)).Append("'>");
            html.Append("</div></article>");
        }
    }

    private static bool TryWebUrl(string text, out Uri url)
    {
        return Uri.TryCreate(text, UriKind.Absolute, out url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && url.UserInfo == "";
    }

    private string RssFrame(string[] body)
    {
        Uri original;
        var relativeTo = TryWebUrl(body[1], out original) || TryWebUrl(body[2], out original)
            ? "<base href='" + E(original.AbsoluteUri) + "'>" : "";
        // The parent parses this escaped payload in an inert template before displaying it.
        // Keep the frame readable before the publisher's own styles are applied.
        return "<!doctype html><meta charset=utf-8>" + relativeTo + "<style>@font-face{font-family:Merriweather;src:url('" + Url + "fonts/merriweather.ttf')}"
            + "</style><style>html,body{margin:0}body{color:#333;font:16px/1.69 Merriweather,Georgia,serif}a{color:#286d45}"
            + "@media(prefers-color-scheme:dark){body{color:#edf1ee}a{color:#91d5ad}}"
            + "img,video{max-width:100%;height:auto}#rss-content{display:flow-root;overflow-wrap:anywhere}#rss-content>:first-child{margin-top:0}"
            + "#rss-content>:last-child{margin-bottom:0}</style><div id=rss-content></div><textarea id=rss-payload hidden>\n"
            + E(body[0]) + "</textarea>";
    }

    private string FeedOptions(string source, bool unread, bool oldest, int group, ArticleFilter dates,
        System.Collections.Generic.IEnumerable<FeedGroup> groups)
    {
        // One shared menu and dialog follow whichever feed button the reader opens.
        const string edited = "<input type=hidden name=edited-feed>";
        const string target = "<input type=hidden name=target-feed>";
        const string actions = "<div class=dialog-actions><button type=submit>Save</button><button type=button class=cancel-feed-edit>Cancel</button></div>";
        var choices = new StringBuilder("<fieldset><legend>Groups</legend>");
        foreach (var folder in groups)
            choices.Append("<label><input type=checkbox name=membership value='").Append(folder.Id).Append("'> ")
                .Append(E(folder.Name)).Append("</label>");
        choices.Append("</fieldset>");
        return "<div class=feed-menu hidden>"
            + Form("collection", target + "<button type=submit name=action value=pause>Pause</button>", unread, source, oldest, group, dates)
            + Form("check-feed", target + "<button type=submit>Refresh</button>", unread, source, oldest, group, dates)
            + Form("mark-feed-read", target + "<button type=submit" + ((dates.Flags & ArticleFilter.Inbox) == ArticleFilter.Inbox ? "" : " disabled")
                + ">Mark as read</button>", unread, source, oldest, group, dates)
            + "<button type=button data-edit=delete>Delete</button><button type=button data-edit=settings>Settings</button></div>"
            + "<dialog class=feed-editor aria-labelledby=feed-editor-title><h2 id=feed-editor-title></h2>"
            + Form("feed-settings", edited + "<label>Feed name <input name=label maxlength=120></label>" + choices
                + "<fieldset class=feed-rss-settings><legend>RSS or Atom</legend><input type=hidden name=version>"
                + "<label>Source URL <input type=url name=url required maxlength=2000></label>"
                + "<label>&ldquo;Open article&rdquo; action <select name=open><option value=original>Original article</option>"
                + "<option value=discussion>Discussion (if available)</option></select></label></fieldset>" + actions, unread, source, oldest, group, dates)
            + Form("delete-feed", edited + "<p>This removes the subscription from every group and deletes all its saved articles from feedbox.</p>"
                + "<div class=dialog-actions><button type=button class=cancel-feed-edit>Cancel</button><button type=submit>Delete feed</button></div>", unread, source, oldest, group, dates)
            + "<p class=feed-editor-error role=status></p></dialog>";
    }

    private static bool HasCheckWarning(string status, string coverage)
    {
        // A successful latest check does not resolve a gap in earlier coverage.
        if (!string.IsNullOrWhiteSpace(coverage)) return true;
        // A Reddit check can save some posts before encountering a problem. Classify what follows the count.
        var detail = Regex.Replace(status ?? "", @"\A(?:No new posts (?:found|added)\.|Added \d+ new posts?\.)\s*", "").Trim();
        return detail != "" && detail != "Check paused." && detail != "Checking older pages for overlap."
            && detail != "Page limit reached. The next check will continue here."
            && detail != "Please wait ten seconds between listing requests."
            && detail != "Source URL changed. Waiting for the next check."
            && detail != "Starting batch saved. Older history was not imported."
            && !detail.StartsWith("Hourly listing request limit reached. Next slot: ", StringComparison.Ordinal)
            && !Regex.IsMatch(detail, @"\AInitial import: \d+ articles remaining\.\z");
    }

    private string RenderCheckStatus(string source, int group, bool paused)
    {
        var html = new StringBuilder("<ul>");
        if (paused) html.Append("<li><b>Collection is paused.</b></li>");
        var rows = store.CheckStatuses(source, group);
        if (rows.Count == 0) html.Append("<li>No feeds in this view.</li>");
        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            // The stored attempt still counts if Reddit returned an error; deferred checks do not change it.
            html.Append(row[4] == "1" ? "<li><b class=feed-paused>" : HasCheckWarning(row[1], row[2]) ? "<li><b class=check-warning>" : "<li><b>").Append(E(row[0])).Append("</b> ");
            if (row[4] == "1") html.Append("<span class=check-age>Feed paused. </span>");
            DateTimeOffset attempted;
            if (DateTimeOffset.TryParseExact(row[3], "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out attempted))
            {
                html.Append("<time class=check-age datetime='").Append(E(row[3])).Append("' title='")
                    .Append(E("Last check started " + attempted.ToLocalTime().ToString("f"))).Append("'>")
                    .Append(CheckAge(now - attempted)).Append("</time>");
            }
            else html.Append("<span class=check-age>Never checked</span>");
            // The old first-check message has no post count and says nothing about the archive import.
            var status = row[1] == "Starting batch saved. Older history was not imported." ? "" :
                string.IsNullOrWhiteSpace(row[1]) ? "No check result recorded." : row[1];
            var detail = (status + " " + row[2]).Trim();
            if (detail != "") html.Append("<br>").Append(E(detail));
            html.Append("</li>");
        }
        return html.Append("</ul>").ToString();
    }

    private static string CheckAge(TimeSpan age)
    {
        if (age.TotalMinutes < 1) return "Just now";
        int count;
        string unit;
        if (age.TotalDays >= 1) { count = age.Days; unit = "day"; }
        else if (age.TotalHours >= 1) { count = age.Hours; unit = "hour"; }
        else { count = age.Minutes; unit = "minute"; }
        return count.ToString(CultureInfo.InvariantCulture) + " " + unit + (count == 1 ? "" : "s") + " ago";
    }

    private static void AppendContext(StringBuilder html, string body)
    {
        // Collapse blank lines added by the listing HTML while retaining paragraph and line breaks.
        html.Append("<div class=context>");
        foreach (var block in Regex.Split(body.Replace("\r\n", "\n").Replace('\r', '\n'), @"\n(?:[^\S\n]*\n)+"))
        {
            var paragraph = block.Trim();
            if (paragraph.Length > 0) html.Append("<p>").Append(E(paragraph)).Append("</p>");
        }
        html.Append("</div>");
    }

    private string Form(string action, string fields, bool unread, string source, bool oldest, int group, ArticleFilter dates)
    {
        return "<form class=inline method=post action='/" + action + "'><input type=hidden name=token value='" + token + "'>"
             + ViewFields(source, unread, oldest, group, dates.Flags)
            + DateFields(dates) + SearchField(dates) + fields + "</form>";
    }

    private static string ViewFields(string source, bool unread, bool oldest, int group, int flags = -1)
    {
        return "<input type=hidden name=feed value='" + E(source) + "'>" + (group == 0 ? "" : "<input type=hidden name=group value='" + group + "'>")
             + (flags >= 0 ? "<input type=hidden name=flags value='" + flags + "'>" : unread ? "" : "<input type=hidden name=all value=1>")
             + (oldest ? "<input type=hidden name=oldest value=1>" : "");
    }

    private static int ViewFlags(NameValueCollection values)
    {
        int flags;
        // Accept the old all=1 links as well as the checkbox form and canonical flags links.
        if (int.TryParse(values["flags"], NumberStyles.None, CultureInfo.InvariantCulture, out flags) && flags >= 0 && flags <= ArticleFilter.Everything)
            return flags;
        if (values["apply-flags"] != "1") return values["all"] == "1" ? ArticleFilter.Everything : ArticleFilter.Inbox;
        flags = 0;
        foreach (var value in values.GetValues("state") ?? new string[0])
        {
            int choice;
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out choice) && (choice == 1 || choice == 2 || choice == 4 || choice == 8)) flags |= choice;
        }
        return flags;
    }

    private static string StateControl(string source, bool oldest, int group, ArticleFilter dates)
    {
        Func<int, string, string> checkbox = (flag, label) => "<label><input type=checkbox name=state value='" + flag + "'"
            + ((dates.Flags & flag) != 0 ? " checked" : "") + "> " + label + "</label>";
        return "<details class=state-filter><summary>Articles</summary><form action='/' method=get>"
            + "<input type=hidden name=apply-flags value=1>" + ViewFields(source, true, oldest, group) + DateFields(dates) + SearchField(dates)
            + "<div class=state-columns><fieldset aria-label='Mark status'>" + checkbox(ArticleFilter.Marked, "Marked") + checkbox(ArticleFilter.Unmarked, "Unmarked")
            + "</fieldset><fieldset aria-label='Read status'>" + checkbox(ArticleFilter.Read, "Read") + checkbox(ArticleFilter.Unread, "Unread")
            + "</fieldset></div><button>Apply</button></form></details>";
    }

    private static string DateFields(ArticleFilter dates)
    {
        return (dates.From == "" ? "" : "<input type=hidden name=from value='" + E(dates.From) + "'>")
            + (dates.To == "" ? "" : "<input type=hidden name=to value='" + E(dates.To) + "'>");
    }

    private static string SearchField(ArticleFilter dates)
    {
        return dates.Search == "" ? "" : "<input type=hidden name=q value='" + E(dates.Search) + "'>";
    }

    private static string SearchControl(string source, bool unread, bool oldest, int group, ArticleFilter dates)
    {
        ArticleFilter withoutSearch;
        ArticleFilter.TryCreate(dates.From, dates.To, out withoutSearch);
        return "<form class=archive-search action='/' method=get>" + ViewFields(source, unread, oldest, group, dates.Flags) + DateFields(dates)
            + "<input type=search maxlength=120 name=q aria-label='Search titles and text' placeholder='Search' value='" + E(dates.Search) + "'>"
            + (dates.Search == "" ? "" : " <a class=clear-search href='" + E(ViewUrl(source, unread, oldest, group, withoutSearch.WithFlags(dates.Flags))) + "'>Clear search</a>") + "</form>";
    }

    private static string DateControl(string source, bool unread, bool oldest, int group, ArticleFilter dates)
    {
        // Unset dates stay open-ended when new articles arrive or old ones are deleted.
        var label = dates.Active ? (dates.From == "" ? "Oldest" : dates.From) + " &#8211; " + (dates.To == "" ? "Newest" : dates.To) : "Dates";
        ArticleFilter withoutDates;
        ArticleFilter.TryCreate("", "", out withoutDates, dates.Search);
        return "<details class=date-filter><summary title='Filter all feeds by publication date'>" + label + "</summary>"
            + "<form action='/' method=get>" + ViewFields(source, unread, oldest, group, dates.Flags) + SearchField(dates)
            + "<label>From <input type=date name=from value='" + E(dates.From) + "'></label>"
            + "<label>To <input type=date name=to value='" + E(dates.To) + "'></label>"
            + "<small>Leave blank for oldest or newest.</small><div class=date-actions><button>Apply</button>"
            + "<a class=date-clear href='" + E(ViewUrl(source, unread, oldest, group, withoutDates.WithFlags(dates.Flags))) + "'>Clear</a></div></form></details>";
    }

    private static string IconButton(string glyph, string label, string attributes = "", string tooltip = null)
    {
        return "<button class=icon-button aria-label='" + E(label) + "' title='" + E(tooltip ?? label) + "'" + attributes
            + "><span aria-hidden=true>" + glyph + "</span></button>";
    }

    private static string FeedLink(string source, string label, string selected, bool unread, long count, bool oldest, int group, int selectedGroup,
        ArticleFilter dates, bool openDiscussion = false, bool hasIcon = false, bool warning = false, bool paused = false)
    {
        var icon = hasIcon ? "<img class=feed-site-icon alt='' loading=lazy width=16 height=16 src='/feed-icon?feed=" + E(source) + "'>" : "";
        var link = "<a class='feed" + (source == "" && group > 0 ? " group-link" : "") + (IsRssKey(source) ? " rss-feed" : "")
             + (warning ? " has-check-warning" : "") + (paused ? " feed-paused" : "")
            + "' href='" + E(ViewUrl(source, unread, oldest, group, dates)) + "' data-feed='" + E(source) + "' data-group='" + group + "'"
            + (source == "" ? " draggable=false" : " draggable=true")
            + (openDiscussion ? " data-open-discussion=1" : "")
             + (paused ? " aria-description='Feed paused'" : warning ? " aria-description='Check status has a warning'" : "")
            + (source == "" && group > 0 && group == selectedGroup ? " data-active-group" : "")
            + (source == selected && group == selectedGroup ? " aria-current=page" : "") + "><span class=feed-label>" + icon + E(label)
            + "</span><span class=count>" + count.ToString(CultureInfo.InvariantCulture) + "</span></a>";
        if (source == "") return link;
        return "<div class=feed-row>" + link + "<button type=button class=feed-options aria-expanded=false aria-label='Options for "
            + E(label) + "' title='Feed options'>&#x22EF;</button></div>";
    }

    private static string SourceName(string source)
    {
        return IsRssKey(source) || Regex.IsMatch(source ?? "", @"\A[a-z0-9_]{1,21}\z") ? source : "";
    }

    private static bool IsRssKey(string id) { return Regex.IsMatch(id ?? "", @"\Arss_[a-f0-9]{64}\z"); }
    private static bool ArticleId(string id) { return IsRssKey(id) || Regex.IsMatch(id ?? "", @"\At3_[a-z0-9]+\z"); }
    private static bool IsBatch(string id) { return Regex.IsMatch(id, @"\A[0-9a-f]{32}\z"); }

    private static int GroupId(string value)
    {
        int group;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out group) ? group : 0;
    }

    private static string ViewUrl(string source, bool unread, bool oldest, int group, ArticleFilter dates = null)
    {
        dates = dates ?? ArticleFilter.All;
        var flagQuery = dates.Flags < 0 ? (unread ? "" : "&all=1") : dates.Flags == ArticleFilter.Inbox ? "" : "&flags=" + dates.Flags;
        return "/?feed=" + Uri.EscapeDataString(source) + (group > 0 ? "&group=" + group : "") + flagQuery + (oldest ? "&oldest=1" : "")
            + (dates.From == "" ? "" : "&from=" + Uri.EscapeDataString(dates.From)) + (dates.To == "" ? "" : "&to=" + Uri.EscapeDataString(dates.To))
            + (dates.Search == "" ? "" : "&q=" + Uri.EscapeDataString(dates.Search));
    }

    private static string Resource(string name)
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
    }

    private static string E(string s) { return WebUtility.HtmlEncode(s); }

    private static byte[] BinaryResource(string name)
    {
        using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (var output = new MemoryStream())
        {
            input.CopyTo(output);
            return output.ToArray();
        }
    }

    private static void Reply(HttpListenerContext context, int code, string body, string contentType = null)
    {
        ReplyBytes(context, code, Encoding.UTF8.GetBytes(body), contentType ?? (code == 200 ? "text/html; charset=utf-8" : "text/plain; charset=utf-8"));
    }

    private static void ReplyBytes(HttpListenerContext context, int code, byte[] bytes, string contentType, bool rssFrame = false, int cacheSeconds = 0)
    {
        var response = context.Response;
        response.StatusCode = code;
        response.ContentType = contentType;
        // Let the sidebar reuse its saved icons across views. Reader data stays uncached.
        response.Headers["Cache-Control"] = cacheSeconds > 0 ? "private, max-age=" + cacheSeconds.ToString(CultureInfo.InvariantCulture) : "no-store";
        // Preserve the Origin on same-origin form submissions without sending a referrer to Reddit.
        response.Headers["Referrer-Policy"] = rssFrame ? "no-referrer" : "same-origin";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Content-Security-Policy"] = rssFrame
            ? "default-src 'none'; style-src 'unsafe-inline' http: https:; img-src http: https: data:; font-src http: https: data:; media-src http: https: data:; base-uri http: https:; form-action 'none'; frame-ancestors 'self'; sandbox allow-same-origin allow-popups allow-popups-to-escape-sandbox"
            : "default-src 'none'; script-src 'self'; style-src 'self'; font-src 'self'; connect-src 'self'; img-src 'self' http: https:; frame-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    public void Dispose() { listener.Close(); }
}
