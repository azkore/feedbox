using System;
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

internal sealed class Inbox : IDisposable
{
    private readonly HttpListener listener = new HttpListener();
    private readonly Store store;
    private readonly Func<string, Task<string>> check;
    private readonly string host;
    private readonly string token = Guid.NewGuid().ToString("N");
    private readonly string script = Resource("inbox.js");
    private readonly string style = Resource("inbox.css");
    private readonly byte[] inter = BinaryResource("InterVariable.woff2");
    private readonly byte[] merriweather = BinaryResource("Merriweather-Regular.ttf");
    public readonly string Url;

    public Inbox(Store store, int port, Func<string, Task<string>> check)
    {
        this.store = store;
        this.check = check;
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
            ArticleFilter dates;
            if (!ArticleFilter.TryCreate(request.QueryString["from"], request.QueryString["to"], out dates))
            { Reply(context, 400, "Choose a valid date range. From must be on or before To."); return; }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/")
            {
                Reply(context, 200, Render(request, dates));
                return;
            }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/check-status")
            {
                Reply(context, 200, RenderCheckStatus(SourceName(request.QueryString["feed"]), GroupId(request.QueryString["group"]), store.IsPaused));
                return;
            }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/unread-counts")
            {
                var counts = store.UnreadCounts(dates);
                var json = new System.Web.Script.Serialization.JavaScriptSerializer();
                Reply(context, 200, json.Serialize(new { feeds = counts, total = counts.Values.Sum() }), "application/json; charset=utf-8");
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
            if (request.Headers["Origin"] != Url.TrimEnd('/') || request.ContentLength64 < 0 || request.ContentLength64 > 4096
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
            if (!ArticleFilter.TryCreate(form["from"], form["to"], out dates))
            { Reply(context, 400, "Choose a valid date range. From must be on or before To."); return; }
            var group = GroupId(form["group"]);
            string result = "";

            // Only an explicit read/unread form can change read state.
            switch (request.Url.AbsolutePath)
            {
                case "/source":
                    var name = (form["name"] ?? "").Trim();
                    if (name.StartsWith("r/", StringComparison.OrdinalIgnoreCase)) name = name.Substring(2);
                    if (!Regex.IsMatch(name, @"^[A-Za-z0-9_]{1,21}$")) { Reply(context, 400, "Enter a subreddit name."); return; }
                    store.AddSource(name, group);
                    break;
                case "/rss-source":
                    Uri url;
                    if (!RssCollector.TryFeedUrl((form["url"] ?? "").Trim(), out url))
                    { Reply(context, 400, "Enter a valid HTTP or HTTPS feed URL."); return; }
                    // Select the new subscription and show the outcome of its first check.
                    form["feed"] = store.AddRss(url, group);
                    await check(form["feed"]);
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
                case "/reorganize-feed":
                    var reorganized = SourceName(form["edited-feed"]);
                    var memberships = (form.GetValues("membership") ?? new string[0]).Select(GroupId).ToArray();
                    if (reorganized == "" || memberships.Any(groupId => groupId == 0) || !store.ReorganizeFeed(reorganized, memberships))
                    { Reply(context, 400, "Choose a feed and valid groups."); return; }
                    // Keep the current feed open if its previous folder was unchecked.
                    if (form["feed"] == reorganized && !memberships.Contains(group)) group = memberships.FirstOrDefault();
                    break;
                case "/collection":
                    if (form["action"] != "pause" && form["action"] != "resume") { Reply(context, 400, "Choose pause or resume."); return; }
                    store.SetPaused(form["action"] == "pause");
                    if (!store.IsPaused) check(null);
                    break;
                case "/read":
                    var id = form["id"] ?? "";
                    if (!ArticleId(id)) { Reply(context, 400, "Invalid post ID."); return; }
                    if (!store.SetRead(id, form["mark"] != "unread")) { Reply(context, 404, "This article is no longer in the inbox."); return; }
                    if (form["inline"] == "1") { context.Response.StatusCode = 204; return; }
                    break;
                case "/mark-feed-read":
                    // No group or All feeds selection can trigger a bulk read action.
                    var feed = SourceName(form["feed"]);
                    if (feed == "" || !store.HasSource(feed)) { Reply(context, 400, "Select a subscribed feed."); return; }
                    result = "&marked=" + store.MarkFeedRead(feed, dates).ToString(CultureInfo.InvariantCulture);
                    break;
                case "/check":
                    var batchOutcome = await check(null);
                    if (form["inline"] == "1") { Reply(context, 200, batchOutcome, "text/plain; charset=utf-8"); return; }
                    break;
                case "/check-feed":
                    // An invalid target must never fall back to checking the scheduled batch.
                    var target = SourceName(form["feed"]);
                    if (target == "" || !store.HasSource(target)) { Reply(context, 400, "Select a subscribed feed."); return; }
                    var outcome = await check(target);
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
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception) { Reply(context, 500, "The inbox could not complete the request."); }
        finally { context.Response.Close(); }
    }

    private string Render(HttpListenerRequest request, ArticleFilter dates)
    {
        bool unread = request.QueryString["all"] != "1";
        bool oldest = request.QueryString["oldest"] == "1";
        bool paused = store.IsPaused;
        var source = SourceName(request.QueryString["feed"]);
        var group = GroupId(request.QueryString["group"]);
        var before = request.QueryString["before"] ?? "";
        var beforeId = request.QueryString["id"] ?? "";
        DateTimeOffset cursor;
        if (!ArticleId(beforeId) || before.Length > 40
            || !DateTimeOffset.TryParse(before, CultureInfo.InvariantCulture, DateTimeStyles.None, out cursor)) before = "";
        var sources = store.Sidebar();
        // Start with the same date-scoped snapshot that the browser will use to refresh the badges.
        var counts = store.UnreadCounts(dates);
        Func<string, long> countFor = name => counts.ContainsKey(name) ? counts[name] : 0;
        var byName = sources.ToDictionary(s => s[0]);
        var groups = store.Groups();
        var selectedGroup = groups.Find(g => g.Id == group);
        if (selectedGroup == null) group = 0;
        string title;
        title = source == "" ? (selectedGroup == null ? "All feeds" : selectedGroup.Name)
            : byName.ContainsKey(source) ? byName[source][3] : source;

        // Keep feed navigation independent of the centered reading stream.
        var html = new StringBuilder("<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>Feed Inbox</title><link rel=stylesheet href='/inbox.css'><script src='/inbox.js' defer></script>");
        html.Append("<body data-unread='").Append(unread ? "1" : "0").Append("'><aside><strong class=brand>Feed Inbox</strong><nav aria-label='Feeds'>");
        html.Append(FeedLink("", "All feeds", source, unread, counts.Values.Sum(), oldest, 0, group, dates));
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
                html.Append(FeedLink(member[0], member[3], source, unread, countFor(member[0]), oldest, folder.Id, group, dates));
            }
            // Keep all member links in alphabetical order when the reader reveals the zero-unread feeds.
            html.Append("<button type=button class=more-feeds aria-expanded=false hidden></button></div></section>");
        }
        var ungrouped = sources.Where(s => !grouped.Contains(s[0])).ToList();
        if (ungrouped.Count > 0)
        {
            if (groups.Count > 0) html.Append("<div class=feed-section-label>Ungrouped</div>");
            html.Append("<div class=ungrouped-feeds id=ungrouped-feeds>");
            foreach (var s in ungrouped) html.Append(FeedLink(s[0], s[3], source, unread, countFor(s[0]), oldest, 0, group, dates));
            html.Append("<button type=button class=more-feeds aria-expanded=false hidden></button></div>");
        }
        if (groups.Count > 0) html.Append("<div hidden>").Append(Form("move-feed", "", unread, source, oldest, group, dates)).Append("</div>");
        html.Append("</nav><details class=add-source><summary>Add source or group</summary>");
        html.Append(Form("source", "<label>Subreddit <input name=name required maxlength=23 placeholder='r/rss'></label> <button>Add</button>", unread, source, oldest, group, dates));
        html.Append(Form("rss-source", "<label>RSS or Atom URL <input type=url name=url required maxlength=2000 placeholder='https://example.com/feed/'></label> <button>Add feed</button>", unread, source, oldest, group, dates));
        html.Append(Form("group", "<label>Group <input name=name required maxlength=80></label> <button>Add group</button>", unread, source, oldest, group, dates)).Append("</details>");
        // This is a global archive action regardless of the selected reading view.
        // Bind deletion to the read articles present when this page was rendered.
        var cutoff = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var readCount = store.ReadCount(cutoff);
        var fileSize = (store.DatabaseFileBytes / 1048576.0).ToString("N1", CultureInfo.CurrentCulture);
        html.Append("<details class=add-source><summary>Database</summary><p>Database file: ").Append(E(fileSize))
            .Append(" MiB<br>").Append(readCount).Append(" read articles</p>");
        html.Append(Form("delete-read", "<input type=hidden name=confirmed value=0><input type=hidden name=cutoff value='" + E(cutoff) + "'><button data-delete-count='" + readCount + "'" + (readCount == 0 ? " disabled" : "") + ">Delete read articles</button>", unread, source, oldest, group, dates));
        html.Append("</details><div class=sidebar-resize role=separator aria-label='Feed sidebar width' aria-orientation=vertical aria-valuemin=160 aria-valuemax=560 aria-valuenow=272 tabindex=0></div></aside>");

        html.Append("<main><header class=feed-header><div class=heading><h1>").Append(E(title)).Append("</h1><div class=toolbar>");
        html.Append("<a href='").Append(E(ViewUrl(source, true, oldest, group, dates))).Append("'").Append(unread ? " aria-current=page" : "").Append(">Unread</a><a href='").Append(E(ViewUrl(source, false, oldest, group, dates))).Append("'").Append(unread ? "" : " aria-current=page").Append(">View all articles</a>");
        html.Append("<a class=sort href='").Append(E(ViewUrl(source, unread, !oldest, group, dates))).Append("' title='Switch to ")
            .Append(oldest ? "newest first'>Oldest first" : "oldest first'>Newest first").Append("</a>");
        html.Append(DateControl(source, unread, oldest, group, dates));
        html.Append(Form("collection", paused ? IconButton("&#x25B6;&#xFE0E;", "Resume collection", " name=action value=resume")
            : IconButton("&#x23F8;&#xFE0E;", "Pause collection", " name=action value=pause"), unread, source, oldest, group, dates));
        if (byName.ContainsKey(source))
        {
            html.Append(Form("check-feed", IconButton("&#x21BB;", "Check this feed"), unread, source, oldest, group, dates));
            html.Append(Form("mark-feed-read", IconButton("&#x2713;", "Mark all as read", tooltip: dates.Active ? "Mark all as read in this feed and date range" : "Mark all as read in this feed"), unread, source, oldest, group, dates));
        }
        else if (!paused) html.Append(Form("check", IconButton("&#x21BB;", "Check now"), unread, source, oldest, group, dates));
        html.Append("<details class=check-status data-status-url='").Append(E("/check-status" + ViewUrl(source, true, false, group).Substring(1)))
            .Append("'><summary>Check status</summary>").Append(RenderCheckStatus(source, group, paused))
            .Append("</details></div></div><p id=check-feedback role=status></p><p id=key-status role=status></p></header>");
        long markedCount;
        if (byName.ContainsKey(source) && long.TryParse(request.QueryString["marked"], NumberStyles.None, CultureInfo.InvariantCulture, out markedCount))
            html.Append("<p role=status class=bulk-receipt>").Append(markedCount == 0 ? "No unread articles to mark." :
                "Marked " + markedCount.ToString("N0", CultureInfo.CurrentCulture) + (markedCount == 1 ? " article" : " articles") + " as read.").Append("</p>");
        long deletedCount;
        if (long.TryParse(request.QueryString["deleted"], NumberStyles.None, CultureInfo.InvariantCulture, out deletedCount))
        {
            html.Append("<p role=status>").Append(deletedCount == 0 ? "No read articles to delete." : "Deleted " + deletedCount + " read articles.");
            if (deletedCount > 0) html.Append(request.QueryString["compact"] == "0" ? " SQLite could not finish compacting the file." : " Database compacted.");
            html.Append("</p>");
        }
        html.Append("<div id=entries>");

        // Use the original URL supplied by the collector. Opening it does not mark the post read.
        var items = store.Items(unread, source, before, beforeId, oldest: oldest, group: group, dates: dates);
        if (items.Count == 0) html.Append(dates.Active ? (unread ? "<p>No unread posts in this date range.</p>" : "<p>No posts in this date range.</p>")
            : (unread ? "<p>No unread posts.</p>" : "<p>No posts collected yet.</p>"));
        string lastDay = null;
        for (int i = 0; i < Math.Min(items.Count, 100); i++)
        {
            var post = items[i];
            DateTimeOffset published;
            var valid = DateTimeOffset.TryParse(post[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out published);
            var date = valid ? published.ToLocalTime().ToString("g") : "";
            var day = valid ? published.LocalDateTime.ToString("D") : "Unknown date";
            if (day != lastDay) html.Append("<h2>").Append(E(day)).Append("</h2>");
            lastDay = day;
            html.Append("<article tabindex='-1' id='").Append(E(post[0])).Append("' data-post='").Append(E(post[0])).Append("' data-source='").Append(E(post[1])).Append("' data-read='").Append(post[5] == null ? "0" : "1").Append("'><div class=article-inner><h3>");
            Uri original;
            if (Uri.TryCreate(post[3], UriKind.Absolute, out original) && (original.Scheme == Uri.UriSchemeHttp || original.Scheme == Uri.UriSchemeHttps) && original.UserInfo == "")
                html.Append("<a class=original target=_blank rel=noopener href='").Append(E(original.AbsoluteUri)).Append("'>").Append(E(post[2])).Append("</a>");
            else html.Append("<strong>").Append(E(post[2])).Append("</strong>");
            html.Append("</h3><small>").Append(E(byName.ContainsKey(post[1]) ? byName[post[1]][3] : post[1]));
            if (post[6] != "") html.Append(IsRssKey(post[1]) ? " &middot; " : " &middot; u/").Append(E(post[6]));
            html.Append(" &middot; ").Append(E(date)).Append("</small><div class=article-actions>");
            html.Append(Form("read", "<input type=hidden name=id value='" + E(post[0]) + "'>"
                 + (post[5] == null ? "<button name=mark value=read>Mark read</button>" : "<button name=mark value=unread>Mark unread</button>"), unread, source, oldest, group, dates));
            html.Append("</div>");

            // Older captures and archive imports still have a plain-text reading view.
            if (!ArticleBody.TryAppend(html, post[9]) && post[7] != "") AppendContext(html, post[7]);
            Uri image;
            if (Uri.TryCreate(post[8], UriKind.Absolute, out image) && image.Scheme == Uri.UriSchemeHttps && (image.Host == "i.redd.it" || image.Host == "preview.redd.it" || image.Host == "external-preview.redd.it"))
                html.Append("<img class=preview loading=lazy referrerpolicy=no-referrer alt='Post preview' src='").Append(E(image.AbsoluteUri)).Append("'>");
            html.Append("</div></article>");
        }
        html.Append("</div>");
        if (items.Count > 100)
        {
            // The cursor belongs to the last rendered row and remains valid after it is marked read.
            var last = items[99];
            var url = ViewUrl(source, unread, oldest, group, dates) + "&before=" + Uri.EscapeDataString(last[4]) + "&id=" + Uri.EscapeDataString(last[0]);
            html.Append("<a id=more href='").Append(E(url)).Append("'>More posts</a>");
        }
        html.Append(FeedOptions(source, unread, oldest, group, dates, groups));
        return html.Append("</main></body></html>").ToString();
    }

    private string FeedOptions(string source, bool unread, bool oldest, int group, ArticleFilter dates,
        System.Collections.Generic.IEnumerable<FeedGroup> groups)
    {
        // One shared menu and dialog follow whichever feed button the reader opens.
        const string edited = "<input type=hidden name=edited-feed>";
        const string actions = "<div class=dialog-actions><button type=submit>Save</button><button type=button class=cancel-feed-edit>Cancel</button></div>";
        var choices = new StringBuilder("<fieldset><legend>Groups</legend>");
        foreach (var folder in groups)
            choices.Append("<label><input type=checkbox name=membership value='").Append(folder.Id).Append("'> ")
                .Append(E(folder.Name)).Append("</label>");
        choices.Append("</fieldset>");
        return "<div class=feed-menu hidden><button type=button data-edit=rename>Rename</button>"
            + "<button type=button data-edit=reorganize>Reorganize feed</button></div>"
            + "<dialog class=feed-editor aria-labelledby=feed-editor-title><h2 id=feed-editor-title></h2>"
            + Form("rename-feed", edited + "<label>Feed name <input name=label maxlength=120></label>" + actions, unread, source, oldest, group, dates)
            + Form("reorganize-feed", edited + choices + actions, unread, source, oldest, group, dates)
            + "<p class=feed-editor-error role=status></p></dialog>";
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
            html.Append("<li><b>").Append(E(row[0])).Append("</b> ");
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
            + ViewFields(source, unread, oldest, group)
            + (dates.From == "" ? "" : "<input type=hidden name=from value='" + E(dates.From) + "'>")
            + (dates.To == "" ? "" : "<input type=hidden name=to value='" + E(dates.To) + "'>") + fields + "</form>";
    }

    private static string ViewFields(string source, bool unread, bool oldest, int group)
    {
        return "<input type=hidden name=feed value='" + E(source) + "'>" + (group == 0 ? "" : "<input type=hidden name=group value='" + group + "'>")
            + (unread ? "" : "<input type=hidden name=all value=1>") + (oldest ? "<input type=hidden name=oldest value=1>" : "");
    }

    private static string DateControl(string source, bool unread, bool oldest, int group, ArticleFilter dates)
    {
        // Unset dates stay open-ended when new articles arrive or old ones are deleted.
        var label = dates.Active ? (dates.From == "" ? "Oldest" : dates.From) + " &#8211; " + (dates.To == "" ? "Newest" : dates.To) : "Dates";
        return "<details class=date-filter><summary title='Filter all feeds by publication date'>" + label + "</summary>"
            + "<form action='/' method=get>" + ViewFields(source, unread, oldest, group)
            + "<label>From <input type=date name=from value='" + E(dates.From) + "'></label>"
            + "<label>To <input type=date name=to value='" + E(dates.To) + "'></label>"
            + "<small>Leave blank for oldest or newest.</small><div class=date-actions><button>Apply</button>"
            + "<a class=date-clear href='" + E(ViewUrl(source, unread, oldest, group)) + "'>Clear</a></div></form></details>";
    }

    private static string IconButton(string glyph, string label, string attributes = "", string tooltip = null)
    {
        return "<button class=icon-button aria-label='" + E(label) + "' title='" + E(tooltip ?? label) + "'" + attributes
            + "><span aria-hidden=true>" + glyph + "</span></button>";
    }

    private static string FeedLink(string source, string label, string selected, bool unread, long count, bool oldest, int group, int selectedGroup, ArticleFilter dates)
    {
        var link = "<a class='feed" + (source == "" && group > 0 ? " group-link" : "") + (IsRssKey(source) ? " rss-feed" : "")
            + "' href='" + E(ViewUrl(source, unread, oldest, group, dates)) + "' data-feed='" + E(source) + "' data-group='" + group + "'"
            + (source == "" ? " draggable=false" : " draggable=true")
            + (source == selected && group == selectedGroup ? " aria-current=page" : "") + "><span class=feed-label>" + E(label)
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

    private static int GroupId(string value)
    {
        int group;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out group) ? group : 0;
    }

    private static string ViewUrl(string source, bool unread, bool oldest, int group, ArticleFilter dates = null)
    {
        dates = dates ?? ArticleFilter.All;
        return "/?feed=" + Uri.EscapeDataString(source) + (group > 0 ? "&group=" + group : "") + (unread ? "" : "&all=1") + (oldest ? "&oldest=1" : "")
            + (dates.From == "" ? "" : "&from=" + Uri.EscapeDataString(dates.From)) + (dates.To == "" ? "" : "&to=" + Uri.EscapeDataString(dates.To));
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

    private static void ReplyBytes(HttpListenerContext context, int code, byte[] bytes, string contentType)
    {
        var response = context.Response;
        response.StatusCode = code;
        response.ContentType = contentType;
        response.Headers["Cache-Control"] = "no-store";
        // Preserve the Origin on same-origin form submissions without sending a referrer to Reddit.
        response.Headers["Referrer-Policy"] = "same-origin";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; font-src 'self'; connect-src 'self'; img-src https://i.redd.it https://preview.redd.it https://external-preview.redd.it; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    public void Dispose() { listener.Close(); }
}
