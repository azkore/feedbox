using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

internal static class Tests
{
    private static void Expect(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private static Post Post(string id, string title, bool pinned = false)
    {
        return new Post { id = "t3_" + id, title = title, pinned = pinned, path = "/r/rss/comments/" + id + "/", originalUrl = "https://example.com/" + id, created = "2026-11-22T10:00:00Z" };
    }

    private static void Main()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            var initial = new[] { Post("a1", "<script>not HTML</script> ♪"), Post("b2", "Keep until read"), Post("c3", "Another post") };
            initial[0].body = "Listing context\n\n<img src=x onerror=alert(1)>";
            initial[0].image = "https://preview.redd.it/example.jpg";
            initial[1].content = @"{""nodes"":[{""tag"":""ul"",""children"":[{""tag"":""li"",""children"":[{""tag"":""a"",""href"":""https://example.com/article"",""children"":[{""tag"":""text"",""text"":""Original link""}]}]}]},{""tag"":""pre"",""children"":[{""tag"":""text"",""text"":""line 1\n  line 2""}]},{""tag"":""a"",""href"":""javascript:alert(1)"",""children"":[{""tag"":""text"",""text"":""Unsafe link""}]},{""tag"":""script"",""children"":[{""tag"":""text"",""text"":""<script>not an executable tag</script>""}]}]}";
            initial[2].body = "Plain fallback after invalid structured data";
            initial[2].content = "{invalid";
            using (var store = new Store(path))
            {
                // Commit a baseline, explicitly read one post, then rediscover the entire baseline.
                store.AddSource("rss");
                var state = store.Sources().Single();
                state.Head = "t3_a1,t3_b2,t3_c3";
                Expect(store.SavePage(state, initial, true) == 3, "the first batch reports exactly the rows inserted");
                store.SetRead("t3_b2", true);
                Expect(store.SavePage(state, initial, true) == 0, "rediscovered rows are not counted as new posts");
                Expect(store.Count(false) == 3 && store.Count(true) == 2, "deduplication preserves read state");
                store.SetPaused(true);
                // Exercise the rolling window with a synthetic clock instead of sending requests.
                var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                for (int i = 0; i < 60; i++) if (store.RedditLimit(clock.AddSeconds(10*i), true) != null) throw new Exception("Early budget refusal");
                Expect(store.RedditLimit(clock.AddMinutes(30), true) != null && store.RedditLimit(clock.AddHours(1), true) == null,
                    "budget rejects request 61 until the oldest request expires");
                Expect(store.RedditLimit(clock.AddHours(1).AddSeconds(9), true) != null, "spacing applies across checks");
                store.SetRedditCooldown(clock.AddDays(3));
            }
            using (var store = new Store(path))
            {
                Expect(store.Count(true) == 2 && store.Items(false).Any(p => p[2].EndsWith("♪")), "SQLite reopen preserves unread state and Unicode");
                Expect(store.IsPaused && store.Sources().Count == 1, "global pause persists across a reopen without removing a feed");
                var reopened = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
                Expect(store.RedditLimit(reopened, true) != null && store.RedditLimit(reopened.AddDays(1), false) == null,
                    "a multi-day cooldown survives restart and expires at its deadline");
                var state = store.Sources().Single();
                var fresh = new PageResult { posts = new System.Collections.Generic.List<Post> { Post("d4", "New"), Post("a1", "Pinned", true) }, next = "/next-page" };
                Expect(!Collector.Advance(state, fresh, true) && state.Seen == "", "a pinned ID is not evidence of catch-up");
                Expect(store.SavePage(state, fresh.posts, false) == 1, "a mixed page counts only the new row");
                state = store.Sources().Single();
                Expect(state.Cursor == "/next-page" && store.Count(false) == 4, "posts and continuation are saved together");
                var overlap = new PageResult { posts = initial.ToList(), next = "/overlap-page" };
                Expect(!Collector.Advance(state, overlap, false) && state.ExtraPage, "overlap schedules one extra page before advancing the checkpoint");
                Expect(Collector.Advance(state, overlap, false), "extra page completes catch-up");
                store.SavePage(state, overlap.posts, true);
                Expect(Collector.PostCount(0, true) == "No new posts found." && Collector.PostCount(0, false) == "No new posts added.",
                    "a failed or partial check does not claim there are no new posts");
                Expect(Collector.NewListingPath(new Source { Name = "4xgaming", Label = "r/4Xgaming" }) == "/r/4Xgaming/new/"
                    && Collector.NewListingPath(new Source { Name = "4xgaming", Label = "r/anotherfeed" }) == "/r/4xgaming/new/",
                    "the New URL retains original casing without using unrelated labels");

                // Exercise the same loopback routes a browser uses, using a fixture instead of Reddit.
                var port = 55000 + new Random().Next(5000);
                string checkedFeed = null;
                using (var server = new Inbox(store, port, feed => System.Threading.Tasks.Task.FromResult(checkedFeed = feed)))
                {
                    string html;
                    Expect(Request(server.Url, "GET", null, null, out html) == 200 && html.Contains("&lt;script&gt;not HTML&lt;/script&gt;"), "inbox escapes titles");
                    Expect(html.Contains("Listing context") && html.Contains("&lt;img src=x onerror=alert(1)&gt;")
                        && html.Contains("src='https://preview.redd.it/example.jpg'"), "inline context is inert text and a permitted preview");
                    Expect(html.Contains("href='https://example.com/a1'"), "the inbox renders the collector's original URL");
                    string all;
                    Expect(Request(server.Url + "?all=1", "GET", null, null, out all) == 200
                        && all.Contains("<ul><li><a target=_blank rel='noopener noreferrer' href='https://example.com/article'>Original link</a></li></ul>")
                        && all.Contains("<pre>line 1\n  line 2</pre>") && all.Contains("Plain fallback after invalid structured data")
                        && all.Contains("Unsafe link") && !all.Contains("javascript:alert") && !all.Contains("<script>not an executable tag"),
                        "structured bodies preserve lists, links and code; unsafe markup stays inert");
                    var token = Regex.Match(html, "name=token value='([0-9a-f]+)'").Groups[1].Value;
                    var form = "token=" + token + "&id=t3_a1";
                    string ignored;
                    Expect(Request(server.Url + "check-feed", "POST", server.Url.TrimEnd('/'), "token=" + token + "&feed=rss&inline=1", out ignored) == 200 && ignored == "rss", "inline check returns its callback result");
                    Expect(Request(server.Url + "check-feed", "POST", server.Url.TrimEnd('/'), "token=" + token + "&feed=rss", out ignored) == 303
                        && checkedFeed == "rss" && store.IsPaused, "a manual check targets one subscribed feed while paused");
                    Expect(Request(server.Url + "check-feed", "POST", server.Url.TrimEnd('/'), "token=" + token + "&feed=unknown", out ignored) == 400
                        && checkedFeed == "rss", "an unknown feed cannot dispatch a check");
                    Expect(Request(server.Url + "read", "POST", "https://example.com", form, out ignored) == 403 && store.Count(true) == 3,
                        "cross-origin requests cannot mark posts read");
                    Expect(Request(server.Url + "read", "POST", server.Url.TrimEnd('/'), form, out ignored) == 303 && store.Count(true) == 2,
                        "an explicit inbox action marks the post read");
                    Expect(Request(server.Url + "read", "POST", server.Url.TrimEnd('/'), "token=" + token + "&id=t3_c3&inline=1", out ignored) == 204
                        && store.Count(true) == 1, "keyboard mark-read writes one item without a page redirect");
                    Expect(Request(server.Url + "collection", "POST", server.Url.TrimEnd('/'), "token=" + token + "&action=resume", out ignored) == 303
                        && !store.IsPaused, "one inbox control resumes collection globally");
                    Expect(Request(server.Url + "collection", "POST", server.Url.TrimEnd('/'), "token=" + token + "&action=pause", out ignored) == 303
                        && store.IsPaused && store.Count(true) == 1, "pausing collection leaves reading state unchanged");
                    // The same toolbar route targets one subscription when the reader is in a feed.
                    var collection = server.Url + "collection";
                    var scope = "token=" + token + "&feed=rss&action=";
                    Expect(Request(collection, "POST", server.Url.TrimEnd('/'), scope + "pause", out ignored) == 303
                        && store.IsPaused && store.IsFeedPaused("rss") && store.Sources(dueOnly: true).Count == 0 && store.Sources().Count == 1,
                        "a paused Reddit feed remains readable and manually checkable but leaves the schedule");
                    Expect(Request(server.Url + "?feed=rss", "GET", null, null, out html) == 200 && html.Contains("Resume this feed")
                        && html.Contains("class='feed feed-paused'") && html.Contains("<h1 class=feed-paused>"), "individual pause is visible in the selected feed and sidebar");
                    Expect(Request(collection, "POST", server.Url.TrimEnd('/'), "token=" + token + "&feed=missing&action=resume", out ignored) == 404
                        && store.IsPaused, "a stale feed cannot resume global collection");
                    Expect(Request(collection, "POST", server.Url.TrimEnd('/'), scope + "resume", out ignored) == 303
                        && store.IsPaused && !store.IsFeedPaused("rss"), "resuming a single feed does not resume collection globally");
                }

                // Marking rows on a page must not shift unseen rows out of the next page.
                store.AddSource("pagingtest");
                var paging = store.Sources().Single(s => s.Name == "pagingtest");
                store.SavePage(paging, Enumerable.Range(0, 105).Select(i => Post("p" + i.ToString("D3"), "Paging test")), true);
                var firstPage = store.Items(true, "pagingtest", limit: 100);
                var last = firstPage.Last();
                foreach (var row in firstPage.Take(10)) store.SetRead(row[0], true);
                var nextPage = store.Items(true, "pagingtest", last[4], last[0]);
                Expect(firstPage.Count == 100 && nextPage.Count == 5 && nextPage.All(p => p[1] == "pagingtest"),
                    "marking rows cannot skip a later page of the same feed");
                Expect(store.Sidebar().Any(row => row[0] == "pagingtest" && row[2] == "95"),
                    "feeds retain their unread count while collection is paused");

                // Comparison imports do not change the checkpoint used by the collector.
                var anchors = store.Sources().Single(s => s.Name == "rss").Anchors;
                store.ImportHistoricalPosts("rss", new[] { Post("older0", "Comparison post") });
                Expect(store.Sources().Single(s => s.Name == "rss").Anchors == anchors, "historical import preserves the collection checkpoint");
                var oldest = store.Items(false, "rss", limit: 1, oldest: true).Single();
                var oldestNext = store.Items(false, "rss", oldest[4], oldest[0], oldest: true);
                Expect(oldestNext.Count == 4 && oldestNext.All(p => p[0] != oldest[0]), "oldest-first comparison pages stay in order");

                // An existing source may belong to two folders without copying its archive or cursor.
                var tech = new FeedGroup { Name = "Tech & tools" };
                tech.Members.Add("rss");
                var games = new FeedGroup { Name = "Games" };
                games.Members.AddRange(new[] { "rss", "newfeed", "newfeed" });
                var labels = new System.Collections.Generic.Dictionary<string, string> { { "rss", "r/RSS" }, { "newfeed", "r/newfeed" } };
                var totalBefore = store.Count(false);
                store.ImportGroups(new[] { tech, games }, labels);
                store.ImportGroups(new[] { tech, games }, labels);
                var folders = store.Groups();
                Expect(folders.Count == 2 && folders[0].Members.SequenceEqual(new[] { "rss" })
                    && folders[1].Members.SequenceEqual(new[] { "rss", "newfeed" }), "metadata import preserves multiple memberships and is idempotent");
                Expect(store.Count(false) == totalBefore && store.Count(false, "newfeed") == 0 && store.IsPaused
                    && store.Sources().Single(s => s.Name == "rss").Anchors == anchors, "metadata import leaves articles, pause and catch-up state intact");
                Expect(store.Items(false, group: folders[1].Id).All(row => row[1] == "rss")
                    && store.Items(false, group: folders[1].Id).Count == store.Count(false, "rss"), "group view excludes articles from unrelated feeds");
            }
            using (var reopened = new Store(path))
                Expect(reopened.Groups().Count == 2 && reopened.IsPaused, "the grouped database reopens without rerunning old migrations");
            TestCleanup();
            TestPrunedActivity();
            TestArchiveBridge();
            TestRedditCollectionStart();
            TestMarkFeedRead();
            TestDeleteFeed();
            TestCheckAllTypes();
            TestRss();
            TestEditRssSource();
            TestFeedIcons();
            TestPublisherPreview();
            TestAppConfig();
            TestReadingTools();
            TestMarkedArticles();
            TestInterruptedResponse();
            TestOpml();
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static void TestEditRssSource()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var store = new Store(path))
            using (var server = new Inbox(store, 55000 + new Random().Next(5000), name => System.Threading.Tasks.Task.FromResult("")))
            {
                var group = store.AddGroup("Fixture group");
                var old = new Uri("https://example.com/old.xml");
                var next = new Uri("https://example.com/new.xml");
                var conflicting = new Uri("https://example.com/existing.xml");
                var feed = store.AddRss(old, group);
                store.AddRss(conflicting, 0);
                store.RenameFeed(feed, "My feed");
                var originalArticle = Post("editold", "Already read");
                store.SaveRss(feed, "Publisher label", new[] { originalArticle });
                store.SetRead(originalArticle.id, true);
                var inFlight = store.RssToCheck(feed);

                // Exercise the same endpoints as the editor, including the optimistic version check.
                string html;
                var origin = server.Url.TrimEnd('/');
                Request(server.Url, "GET", null, null, out html);
                var token = Regex.Match(html, "name=token value='([0-9a-f]+)'").Groups[1].Value;
                var form = "token=" + token + "&edited-feed=" + feed + "&version=" + Store.RssKey(old.AbsoluteUri) + "&url=";
                var endpoint = server.Url + "edit-source";
                Expect(Request(server.Url + "feed-source?feed=" + feed, "GET", null, null, out html) == 200 && html.Contains(old.AbsoluteUri),
                    "the source editor can load the current subscription URL");
                Expect(Request(endpoint, "POST", origin, form + Uri.EscapeDataString(conflicting.AbsoluteUri), out html) == 409
                    && store.RssToCheck(feed).FeedUrl == old.AbsoluteUri, "editing cannot collide with an existing subscription");
                Expect(Request(endpoint, "POST", origin, form + Uri.EscapeDataString(next.AbsoluteUri), out html) == 303
                    && store.RssToCheck(feed).FeedUrl == next.AbsoluteUri && store.Groups().Single().Members.Contains(feed)
                    && store.RssSubscriptions().Single(row => row[0] == feed)[1] == "My feed" && store.Count(false, feed) == 1 && store.Count(true, feed) == 0,
                    "editing preserves the existing feed identity, articles, read state, label and memberships");
                Expect(Request(endpoint, "POST", origin, form + Uri.EscapeDataString(old.AbsoluteUri), out html) == 409
                    && store.RssToCheck(feed).FeedUrl == next.AbsoluteUri, "a stale editor cannot overwrite the new URL");

                var late = Post("editlate", "Late response");
                Expect(!store.NoteRss(inFlight, "Stale result") && store.SaveRss(feed, "Old title", new[] { late }, inFlight.FeedUrl) == -1
                    && store.Count(false, feed) == 1, "checks from the old URL cannot write after the edit");
                Expect(store.AddRss(next, 0) == feed && store.ImportRss(new[] { new OpmlFeed { Url = next, Label = "Imported", Group = "" } }, false).Item1 == 0
                    && store.AddRss(old, 0) != feed, "adding or importing resolves current URLs even after a source edit");

                // A rejected source change must not save the other settings; a valid save updates all four together.
                var otherGroup = store.AddGroup("Second group");
                var settings = "token=" + token + "&edited-feed=" + feed + "&label=Renamed&open=discussion&membership=" + otherGroup + "&version=";
                var settingsRoute = server.Url + "feed-settings";
                var newUrl = "https://example.com/final.xml";
                Expect(Request(settingsRoute, "POST", origin, settings + Store.RssKey(old.AbsoluteUri) + "&url=" + Uri.EscapeDataString(newUrl), out html) == 409
                    && store.Sidebar().Single(row => row[0] == feed)[3] == "My feed" && store.Groups().Single(g => g.Id == group).Members.Contains(feed),
                    "a stale URL leaves the name and memberships intact");
                Expect(Request(settingsRoute, "POST", origin, settings + Store.RssKey(next.AbsoluteUri) + "&url=" + Uri.EscapeDataString(newUrl), out html) == 303
                    && store.RssToCheck(feed).FeedUrl == newUrl && store.Sidebar().Single(row => row[0] == feed).Skip(3).Take(3).SequenceEqual(new[] { "Renamed", "", "1" })
                    && store.Groups().Single(g => g.Members.Contains(feed)).Id == otherGroup && store.Count(true, feed) == 0,
                    "saving feed settings updates the URL, name, group and open action without changing read state");
            }
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }

    private static void TestFeedIcons()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            var origin = new Uri("https://example.org/feeds/news.xml");
            Uri icon, site;
            string title;
            const string rss = "<rss><channel><title>Example</title><link>/news/</link><image><url>badge.png</url></image></channel></rss>";
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(rss)))
                RssCollector.Parse(input, origin, "fixture", out title, out icon, out site);
            Expect(icon.AbsoluteUri == "https://example.org/feeds/badge.png" && site.AbsoluteUri == "https://example.org/news/",
                "RSS icon metadata resolves against the feed response URL");
            const string atom = "<feed xmlns='http://www.w3.org/2005/Atom'><link rel='self' href='/feed'/><link href='/blog/'/><icon>/atom.png</icon><logo>/logo.png</logo></feed>";
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(atom)))
                RssCollector.Parse(input, origin, "fixture", out title, out icon, out site);
            Expect(icon.AbsolutePath == "/atom.png" && site.AbsolutePath == "/blog/", "Atom prefers its icon and uses the publisher link");

            // A local icon serves raster data for the subscription, while missing and unrelated keys fall back.
            using (var store = new Store(path))
            using (var inbox = new Inbox(store, 55000 + new Random().Next(5000), name => System.Threading.Tasks.Task.FromResult("")))
            using (var source = new System.Drawing.Bitmap(128, 64))
            using (var data = new MemoryStream())
            {
                var key = store.AddRss(origin, 0);
                source.Save(data, System.Drawing.Imaging.ImageFormat.Png);
                Directory.CreateDirectory(path + ".icons");
                using (var result = FeedIcons.Rasterize(data.ToArray()))
                {
                    Expect(result.Width == 64 && result.Height == 32 && FeedIcons.Rasterize(Encoding.UTF8.GetBytes("<svg/>")) == null,
                        "feed icons preserve proportions and reject markup");
                    result.Save(Path.Combine(path + ".icons", key + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                string html;
                Expect(Request(inbox.Url, "GET", null, null, out html) == 200 && html.Contains("/feed-icon?feed=" + key)
                    && Request(inbox.Url + "feed-icon?feed=" + key, "GET", null, null, out html) == 200
                    && Request(inbox.Url + "feed-icon?feed=unknown", "GET", null, null, out html) == 404,
                    "the sidebar uses saved icons on a local URL and tolerates missing assets");
            }
        }
        finally
        {
            if (Directory.Exists(path + ".icons")) Directory.Delete(path + ".icons", true);
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static void TestPublisherPreview()
    {
        var article = new Uri("https://example.com/news/story");
        var html = "<!-- <meta property='og:image' content='/ignored.jpg'> -->"
            + "<script>const text = `<meta property='og:image' content='/also-ignored.jpg'>`;</script>"
            + "<meta name=twitter:image content=/fallback.jpg>"
            + "<META content='../image.jpg?a=1&amp;b=2' PROPERTY='og:image' />";
        Expect(PublisherPreview.FindImage(html, article).AbsoluteUri == "https://example.com/image.jpg?a=1&b=2"
            && PublisherPreview.FindImage("<meta name=twitter:image content=/fallback.jpg>", article).AbsoluteUri == "https://example.com/fallback.jpg",
            "publisher previews resolve relative metadata, prefer Open Graph and ignore markup in scripts and comments");
        Expect(PublisherPreview.FindImage("<meta property=og:image content='file:///image.jpg'>", article) == null
            && PublisherPreview.Lookup("http://[::ffff:127.0.0.1]/").GetAwaiter().GetResult() == null
            && PublisherPreview.Lookup("http://169.254.169.254/").GetAwaiter().GetResult() == null,
            "previews do not fetch local, link-local or non-web URLs");
    }

    private static void TestInterruptedResponse()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            var port = 55000 + new Random().Next(5000);
            using (var store = new Store(file))
            using (var server = new Inbox(store, port, name => System.Threading.Tasks.Task.FromResult("")))
            {
                var request = Encoding.ASCII.GetBytes("GET /fonts/merriweather.ttf HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\n\r\n");
                // Reset the socket after the response starts, while the font still has bytes to send.
                for (int i = 0; i < 12; i++)
                {
                    using (var client = new TcpClient { ReceiveBufferSize = 1024, ReceiveTimeout = 5000, LingerState = new LingerOption(true, 0) })
                    {
                        client.Connect(IPAddress.Loopback, port);
                        var stream = client.GetStream();
                        stream.Write(request, 0, request.Length);
                        var beginning = new byte[16];
                        if (stream.Read(beginning, 0, beginning.Length) == 0) throw new IOException("The font response never started.");
                    }
                }
                string html;
                Expect(Request(server.Url, "GET", null, null, out html) == 200 && html.Contains("<title>feedbox</title>"),
                    "the inbox still serves requests after clients reset a response in progress");
            }
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(file + suffix)) File.Delete(file + suffix); }
    }

    private static void TestMarkedArticles()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            var saved = Post("marked1", "Saved article");
            var ordinary = Post("marked2", "Ordinary article");
            var read = Post("marked3", "Read article");
            using (var store = new Store(file))
            using (var server = new Inbox(store, 55000 + new Random().Next(5000), name => System.Threading.Tasks.Task.FromResult("")))
            {
                store.ImportHistoricalPosts("rss", new[] { saved, ordinary, read });
                store.SetRead(saved.id, true);
                store.SetRead(read.id, true);
                string html;
                var view = server.Url + "?feed=rss";
                Request(view, "GET", null, null, out html);
                var token = Regex.Match(html, "name=token value='([0-9a-f]+)'").Groups[1].Value;
                var origin = server.Url.TrimEnd('/');
                var route = server.Url + "mark-article";
                var action = "token=" + token + "&id=" + saved.id;
                Expect(Request(route, "POST", "https://example.com", action + "&action=mark", out html) == 403,
                    "a cross-origin request cannot mark an article");
                Expect(Request(route, "POST", origin, action + "&action=toggle", out html) == 200 && html == "{\"marked\":1,\"read\":0}"
                    && Request(route, "POST", origin, action + "&action=toggle", out html) == 200 && html == "{\"marked\":0,\"read\":1}"
                    && Request(route, "POST", origin, action + "&action=toggle", out html) == 200 && html == "{\"marked\":1,\"read\":0}",
                    "toggling alternates between marked unread and unmarked read");
                Func<string, string, bool> shows = (page, id) => page.Contains("data-post='" + id + "'");
                Expect(Request(view, "GET", null, null, out html) == 200 && shows(html, ordinary.id) && !shows(html, saved.id) && !shows(html, read.id),
                    "the default view includes only unread and unmarked articles");
                Expect(Request(view + "&apply-flags=1&state=8&state=4&state=1", "GET", null, null, out html) == 200
                    && shows(html, saved.id) && shows(html, ordinary.id) && !shows(html, read.id), "checking both mark states and Unread excludes all read articles");
                Expect(Request(view + "&flags=15", "GET", null, null, out html) == 200 && new[] { saved, ordinary, read }.All(post => shows(html, post.id)),
                    "checking all four flags shows every article");
                Expect(Request(view + "&flags=8", "GET", null, null, out html) == 200 && !shows(html, saved.id), "an unchecked read-status pair matches nothing");
                Expect(!store.SetRead(saved.id, true) && store.MarkFeedRead("rss") == 1 && store.Count(true, "rss") == 1,
                    "individual and bulk read actions leave a marked article unread");
            }
            using (var store = new Store(file))
            {
                store.ImportHistoricalPosts("rss", new[] { saved });
                var marked = store.Items(true, "rss", dates: ArticleFilter.All.WithFlags(ArticleFilter.Unread | ArticleFilter.Marked));
                Expect(marked.Count == 1 && marked[0][0] == saved.id, "marks survive restart and rediscovery");
                var consumed = store.UpdateMark(saved.id, true);
                Expect(consumed[0] == "0" && consumed[1] != null && store.UpdateMark(saved.id, true)[1] == consumed[1],
                    "opening a marked article consumes the mark only once and marks it read");
            }
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(file + suffix)) File.Delete(file + suffix); }
    }

    private static void TestReadingTools()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var store = new Store(file))
            using (var server = new Inbox(store, 55000 + new Random().Next(5000), name => System.Threading.Tasks.Task.FromResult("")))
            {
                var a = Post("undo1", "Новости 100%_");
                var b = Post("undo2", "Other post");
                var c = Post("undo3", "Previously read");
                var d = Post("undo4", "After the bulk action");
                b.body = "Text search marker.";
                store.ImportHistoricalPosts("rss", new[] { a, b, c });
                store.SetRead(c.id, true);
                var arrival = store.LatestArrival();
                const string receipt = "0123456789abcdef0123456789abcdef";
                Expect(store.MarkFeedRead("rss", batch: receipt) == 2 && store.CanUndoFeedRead("rss", receipt), "bulk marking records exactly the articles changed");
                store.ImportHistoricalPosts("rss", new[] { a, d });
                store.SetRead(b.id, true);
                Expect(store.UndoFeedRead("rss", receipt) == 1 && store.UndoFeedRead("rss", receipt) == 0
                    && store.Items(true, "rss").Select(r => r[0]).OrderBy(id => id).SequenceEqual(new[] { a.id, d.id }),
                    "undo does not alter previously or subsequently read articles or newly arrived ones");

                string html;
                ArticleFilter literal;
                Expect(ArticleFilter.TryCreate("", "", out literal, "новости 100%_") && store.Items(false, dates: literal).Single()[0] == a.id,
                    "SQLite matches Unicode case-insensitively before rendering the view");
                var searched = Request(server.Url + "?feed=rss&all=1&q=" + Uri.EscapeDataString("новости 100%_"), "GET", null, null, out html);
                if (searched != 200 || !html.Contains("data-post='" + a.id + "'"))
                    Console.WriteLine("Unicode search HTTP " + searched + "; query field: " + Regex.Match(html, "<input type=search[^>]+>").Value);
                Expect(searched == 200 && html.Contains("data-post='" + a.id + "'") && !html.Contains("data-post='" + b.id + "'"), "search folds Unicode and treats % and _ literally");
                Expect(Request(server.Url + "?feed=rss&all=1&q=TEXT+SEARCH+MARKER", "GET", null, null, out html) == 200
                    && html.Contains("data-post='" + b.id + "'") && !html.Contains("data-post='" + a.id + "'"), "search finds article body text");
                Expect(Request(server.Url + "?feed=rss&all=1&oldest=1&at=" + b.id, "GET", null, null, out html) == 200
                    && !html.Contains("data-post='" + a.id + "'") && html.Contains("data-post='" + b.id + "'"), "resume can start from the exact saved article");
                Expect(Request(server.Url + "?feed=rss&oldest=1&at=" + b.id, "GET", null, null, out html) == 200
                    && html.Contains("data-post='" + a.id + "'") && !html.Contains("data-post='" + b.id + "'"), "unread resume falls back to the first unread article if the bookmark is read");
                Expect(Request(server.Url + "new-articles?feed=rss&since=" + arrival, "GET", null, null, out html) == 200
                    && html.Contains("data-post='" + d.id + "'") && !html.Contains("data-post='" + a.id + "'"), "live arrivals include new saves rather than rediscovered posts");
            }
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(file + suffix)) File.Delete(file + suffix); }
    }

    private static void TestOpml()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var store = new Store(file))
            using (var server = new Inbox(store, 55000 + new Random().Next(5000), name => System.Threading.Tasks.Task.FromResult("")))
            {
                const string url = "https://example.com/radio.xml";
                const string opml = "<opml><body><outline text='Music'><outline text='Radio &amp; News' xmlUrl='https://example.com/radio.xml'/></outline>"
                    + "<outline text='Archive'><outline xmlUrl='https://example.com/radio.xml'/></outline><outline xmlUrl='https://www.reddit.com/r/rss/.rss'/></body></opml>";
                var payload = Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(opml)));
                string html;
                Request(server.Url, "GET", null, null, out html);
                var token = Regex.Match(html, "name=token value='([0-9a-f]+)'").Groups[1].Value;
                var origin = server.Url.TrimEnd('/');
                Expect(Request(server.Url + "import-opml", "POST", origin, "token=" + token + "&new-only=1&opml=" + payload, out html) == 303
                    && store.RssSubscriptions().Count == 1 && store.Groups().Count(g => g.Members.Count == 1) == 2 && store.Count(false) == 0,
                    "OPML adds metadata once and preserves both group memberships");
                var source = Store.RssKey(url);
                var old = Post("baseline1", "Before import"); old.publicationKnown = true;
                var unknown = Post("baseline2", "Undated at first check");
                var fresh = Post("baseline3", "Published after import"); fresh.publicationKnown = true;
                fresh.created = DateTimeOffset.UtcNow.AddMinutes(1).ToString("o");
                Expect(store.SaveRss(source, "Radio", new[] { old, unknown, fresh }) == 1 && store.Count(true, source) == 1,
                    "new-only import saves post-import dated entries but leaves old and initial undated articles out of SQLite posts");
                var later = Post("baseline4", "New undated article");
                Expect(store.SaveRss(source, "Radio", new[] { unknown, later }) == 1 && store.Count(false, source) == 2,
                    "the next poll skips the undated baseline and saves newly seen entries");
                Expect(Request(server.Url + "subscriptions.opml", "GET", null, null, out html) == 200, "OPML export is available in the inbox");
                int skipped;
                var roundTrip = Opml.Read(Encoding.UTF8.GetBytes(html), out skipped);
                Expect(skipped == 0 && roundTrip.Count == 2 && roundTrip.All(feed => feed.Label == "Radio & News")
                    && store.ImportRss(roundTrip, false).Item1 == 0 && store.Groups().Count == 2, "OPML export and reimport retain labels and multiple folders without duplicates");
            }
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(file + suffix)) File.Delete(file + suffix); }
    }

    private static void TestAppConfig()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var defaults = AppConfig.Load(folder, new string[0]);
            Expect(defaults.Database == Path.Combine(folder, "feedbox.sqlite") && defaults.Profile == Path.Combine(folder, "profile")
                && defaults.Port == 51883, "no-argument startup uses the executable folder");
            var legacyDatabase = Path.Combine(folder, "feed-inbox.sqlite");
            File.WriteAllText(legacyDatabase, "");
            File.WriteAllText(Path.Combine(folder, "feed-inbox.conf"), "port=52501\n");
            var upgrade = AppConfig.Load(folder, new string[0]);
            Expect(upgrade.Database == legacyDatabase && upgrade.Port == 52501, "an upgrade keeps an existing archive and config");
            var externalProfile = Path.Combine(Path.GetTempPath(), "External Profile");
            File.WriteAllText(Path.Combine(folder, "feedbox.conf"), "# Local settings\ndatabase=data files\\test.sqlite\nprofile=" + externalProfile + "\nport=52603\n");
            var custom = AppConfig.Load(folder, new string[0]);
            Expect(custom.Database == Path.Combine(folder, "data files", "test.sqlite") && custom.Profile == externalProfile
                && custom.Port == 52603, "the text config accepts absolute and executable-relative paths with spaces");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void TestDeleteFeed()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var store = new Store(path))
            using (var server = new Inbox(store, 55000 + new Random().Next(5000), name => System.Threading.Tasks.Task.FromResult("")))
            {
                var group = store.AddGroup("Fixture group");
                var otherGroup = store.AddGroup("Other group");
                store.AddSource("remove", group);
                store.AddSource("remove", otherGroup);
                store.AddSource("keep", group);
                var remove = store.Sources().Single(s => s.Name == "remove");
                var target = Post("deletefeed1", "The post to delete");
                var keep = Post("deletefeed2", "The post to preserve");
                store.SavePage(remove, new[] { target }, true);
                store.ImportHistoricalPosts("keep", new[] { keep });
                store.SetRead(keep.id, true);

                // Exercise the real POST route and check that the rest of the archive survives.
                string html;
                Request(server.Url + "?feed=remove&group=" + group, "GET", null, null, out html);
                var token = Regex.Match(html, "name=token value='([0-9a-f]+)'").Groups[1].Value;
                var form = "token=" + token + "&edited-feed=remove&feed=remove&group=" + group;
                Expect(Request(server.Url + "delete-feed", "POST", server.Url.TrimEnd('/'), form, out html) == 303
                    && store.Sources().Single().Name == "keep"
                    && store.Groups().Count == 2 && store.Groups().All(g => !g.Members.Contains("remove"))
                    && store.Items(false).Single()[0] == keep.id && store.Count(true) == 0,
                    "deleting a feed removes its posts and memberships while preserving other feeds and their read state");

                var rss = store.AddRss(new Uri("https://example.com/feed.xml"), group);
                store.DeleteFeed(rss);
                Expect(store.SavePage(remove, new[] { target }, true) == 0 && store.SaveRss(rss, "Late reply", new[] { target }) == 0
                    && store.Count(false) == 1, "a collection response cannot restore articles after its feed was deleted");
            }
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }

    private static void TestCheckAllTypes()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            int started = 0;
            bool rss = false, reddit = false;
            using (var store = new Store(path))
            using (var server = new Inbox(store, 55000 + new Random().Next(5000), name => System.Threading.Tasks.Task.FromResult(""),
                (r, d) => { rss = r; reddit = d; started++; return true; }, () => new FeedCheckStatus(started > 0, "Fixture progress")))
            {
                string html;
                store.AddSource("single");
                Request(server.Url, "GET", null, null, out html);
                Expect(html.Contains("action='/check-all'") && html.Contains("name=rss value=1 checked")
                    && html.Contains("name=reddit value=1>"), "All feeds offers RSS by default and Reddit as opt-in");
                var token = Regex.Match(html, "name=token value='([0-9a-f]+)'").Groups[1].Value;
                var form = "token=" + token + "&feed=&inline=1";
                var route = server.Url + "check-all";
                Expect(Request(route, "POST", server.Url.TrimEnd('/'), form, out html) == 400 && started == 0,
                    "an empty selection does not start a batch");
                Expect(Request(route, "POST", server.Url.TrimEnd('/'), form + "&rss=1", out html) == 202
                    && rss && !reddit && started == 1, "the default starts an RSS-only batch");
                Expect(Request(route, "POST", server.Url.TrimEnd('/'), form + "&reddit=1", out html) == 202
                    && !rss && reddit && started == 2, "switching the selection starts a Reddit-only batch");
                Expect(Request(server.Url + "check-all-status", "GET", null, null, out html) == 200
                    && html.Contains("\"Running\":true") && html.Contains("Fixture progress"), "the batch can report progress after the request returns");
                Request(server.Url + "?feed=single", "GET", null, null, out html);
                Expect(!html.Contains("action='/check-all'") && html.Contains("action='/check-feed'"), "single feed checks keep their existing control");
            }
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }

    private static void TestRss()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        var url = new Uri("https://example.com/feed/");
        string title;
        try
        {
            using (var store = new Store(path))
            using (var server = new Inbox(store, 55000 + new Random().Next(5000), name => System.Threading.Tasks.Task.FromResult("")))
            {
                var key = store.AddRss(url, 0);
                var xml = "<rss version='2.0'><channel><title>RSS fixture</title><item><guid>fixture-1</guid><title>Album review</title><link>https://example.com/review</link>"
                     + "<pubDate>Sun, 22 Nov 2026 16:05:55 +0000</pubDate><comments>/discussion</comments>"
                     + "<description><![CDATA[<p>A &amp; B</p><script>ignored</script>]]></description></item></channel></rss>";
                using (var body = new MemoryStream(Encoding.UTF8.GetBytes(xml)))
                {
                    var posts = RssCollector.Parse(body, url, key, out title);
                     Expect(posts.Count == 1 && title == "RSS fixture" && posts[0].body == "A & B" && posts[0].originalUrl == "https://example.com/review"
                         && posts[0].discussion == "https://example.com/discussion", "RSS retains searchable text and the discussion destination");
                    // RSS scheduling skips individual pauses; an explicit check can still find the feed.
                    Expect(store.SetFeedPaused(key, true) && store.RssToCheck() == null && store.RssToCheck(key) != null
                        && store.PausedFeeds().SetEquals(new[] { key }), "paused RSS remains manually checkable but leaves the schedule");
                    Expect(store.SetFeedPaused(key, false), "an RSS feed can be resumed independently");
                    Expect(store.Sources().Count == 0 && store.RssToCheck().Name == key, "RSS is due on its own schedule, outside the Reddit collector");
                    store.RecordAttempt(key);
                    Expect(store.RssToCheck() == null && store.RssToCheck(key).FeedUrl == url.AbsoluteUri, "RSS is deferred after an attempt but remains manually checkable");
                    store.SaveRss(key, title, posts);
                    string html;
                    var tokenPage = server.Url + "?feed=" + key;
                     Expect(Request(tokenPage, "GET", null, null, out html) == 200 && html.Contains("iframe class=rss-body") && html.Contains("RSS fixture")
                         && !html.Contains("data-discussion="), "RSS bodies use a separate frame; V opens the original by default");
                     var token = Regex.Match(html, "name=token value='([0-9a-f]+)'").Groups[1].Value;
                     Expect(Request(server.Url + "rss-body?id=" + posts[0].id, "GET", null, null, out html) == 200
                         && html.Contains("&lt;p&gt;A &amp;amp; B&lt;/p&gt;") && !html.Contains("<script>ignored"), "the frame starts with inert, escaped publisher HTML");
                     Expect(Request(server.Url + "feed-shortcut", "POST", server.Url.TrimEnd('/'), "token=" + token + "&edited-feed=" + key + "&open=discussion", out html) == 303
                         && Request(tokenPage, "GET", null, null, out html) == 200 && html.Contains("data-discussion='https://example.com/discussion'")
                         && html.Contains("href='https://example.com/review'"), "the feed setting changes V's target without changing the headline link");
                    Expect(Request(server.Url + "read", "POST", server.Url.TrimEnd('/'), "token=" + token + "&id=" + posts[0].id + "&inline=1", out html) == 204
                        && store.SaveRss(key, title, posts) == 0 && store.Count(true, key) == 0, "refreshing an RSS entry preserves its read state");

                    // Move one sidebar entry, preserving the separate membership and the saved article.
                    var music = store.AddGroup("music");
                    var other = store.AddGroup("other");
                    var destination = store.AddGroup("destination");
                    var move = server.Url + "move-feed";
                    var fields = "token=" + token + "&moved-feed=" + key + "&to-group=";
                    var origin = server.Url.TrimEnd('/');
                    Expect(Request(move, "POST", origin, fields + music + "&from-group=0", out html) == 303
                        && store.Groups().Single(g => g.Id == music).Members.Contains(key), "an ungrouped RSS feed can move into a folder");
                    store.AddRss(url, other);
                    Expect(Request(move, "POST", origin, fields + destination + "&from-group=" + music, out html) == 303
                        && store.Groups().Where(g => g.Members.Contains(key)).Select(g => g.Id).SequenceEqual(new[] { other, destination })
                        && store.Count(false, key) == 1 && store.Count(true, key) == 0, "moving one feed entry preserves its other folder and read state");
                    Expect(Request(move, "POST", origin, fields + destination + "&from-group=" + music, out html) == 409,
                        "stale feed moves are rejected");

                    var edit = "token=" + token + "&edited-feed=" + key;
                    Expect(Request(server.Url + "rename-feed", "POST", origin, edit + "&label=Album+reviews", out html) == 303
                        && store.SaveRss(key, "New publisher label", posts) == 0
                        && store.Sidebar().Single(s => s[0] == key)[3] == "Album reviews", "a local rename survives a publisher title refresh");
                    Expect(Request(server.Url + "reorganize-feed", "POST", origin, edit + "&membership=" + music + "&membership=" + destination, out html) == 303
                        && store.Groups().Where(g => g.Members.Contains(key)).Select(g => g.Id).SequenceEqual(new[] { music, destination })
                        && store.Count(true, key) == 0, "reorganizing updates the checked folders and preserves read state");
                    Expect(Request(server.Url + "reorganize-feed", "POST", origin, edit + "&membership=999999", out html) == 400
                        && store.Groups().Count(g => g.Members.Contains(key)) == 2, "invalid folder choices cannot partially reorganize a feed");
                }
                 var atom = "<feed xmlns='http://www.w3.org/2005/Atom'><title>Atom fixture</title><entry><id>fixture-1</id><title>Entry</title><link rel='self' href='atom.xml'/><link href='/article'/><link rel='replies' type='application/atom+xml' href='/replies.atom'/><link rel='replies' type='text/html' href='/replies'/><updated>2026-11-22T20:00:00Z</updated><summary type='text'>literal &lt;b&gt; text</summary></entry></feed>";
                 using (var body = new MemoryStream(Encoding.UTF8.GetBytes(atom)))
                 {
                     var item = RssCollector.Parse(body, url, Store.RssKey("other"), out title).Single();
                     Expect(item.originalUrl == "https://example.com/article" && item.discussion == "https://example.com/replies"
                          && item.content == null && item.body == "literal <b> text", "Atom uses HTML replies for discussions and preserves literal plain text");
                  }
                // Slashdot publishes both formats; changing formats should not change an article's identity.
                const string rdf = "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#' xmlns:dc='http://purl.org/dc/elements/1.1/' xmlns='";
                const string channel = "'><channel><title>RDF fixture</title><link>/</link></channel><image><url>/icon.png</url></image>";
                const string entry = "<item><title>RDF article</title><link>/article</link>";
                var rss1 = rdf + "http://purl.org/rss/1.0/" + channel + entry
                    + "<description><![CDATA[<b>Summary</b>]]></description><dc:creator>Editor</dc:creator><dc:date>2026-11-22T20:00:00Z</dc:date></item></rdf:RDF>";
                var rss09 = rdf + "http://my.netscape.com/rdf/simple/0.9/" + channel + entry + "</item></rdf:RDF>";
                Uri icon, site;
                Post dated, undated;
                using (var body = new MemoryStream(Encoding.UTF8.GetBytes(rss1)))
                    dated = RssCollector.Parse(body, url, key, out title, out icon, out site).Single();
                Expect(title == "RDF fixture" && site.AbsoluteUri == "https://example.com/" && icon.AbsoluteUri == "https://example.com/icon.png"
                    && dated.publicationKnown && dated.created == "2026-11-22T20:00:00.0000000+00:00" && dated.author == "Editor"
                    && dated.body == "Summary" && dated.content == "<b>Summary</b>", "RSS 1.0 reads sibling items and image, Dublin Core metadata, and article HTML");
                using (var body = new MemoryStream(Encoding.UTF8.GetBytes(rss09)))
                    undated = RssCollector.Parse(body, url, key, out title).Single();
                Expect(undated.id == dated.id && undated.originalUrl == "https://example.com/article" && !undated.publicationKnown,
                    "RSS 0.90 collects undated items with the same link-based identity as RSS 1.0");
                try
                {
                    using (var body = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE rss [<!ENTITY sample 'x'>]><rss><channel><title>&sample;</title></channel></rss>")))
                        RssCollector.Parse(body, url, key, out title);
                    throw new Exception("Unexpected RSS DTD acceptance");
                }
                catch (System.Xml.XmlException) { Console.WriteLine("PASS: RSS XML rejects DTDs"); }
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static void TestMarkFeedRead()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var store = new Store(path))
            using (var server = new Inbox(store, 55000 + new Random().Next(5000), feed => System.Threading.Tasks.Task.FromResult("")))
            {
                // Include a post beyond the first page and a separate feed that must remain unread.
                store.ImportHistoricalPosts("rss", Enumerable.Range(0, 105).Select(i => Post("bulk" + i, "Bulk read fixture")));
                store.ImportHistoricalPosts("otherfeed", new[] { Post("other1", "Leave unread") });
                string html;
                Expect(Request(server.Url, "GET", null, null, out html) == 200
                    && !html.Substring(0, html.IndexOf("</header>", StringComparison.Ordinal)).Contains("action='/mark-feed-read'"),
                    "All feeds has no bulk-read control in its toolbar");
                var token = Regex.Match(html, "name=token value='([0-9a-f]+)'").Groups[1].Value;
                var origin = server.Url.TrimEnd('/');
                var route = server.Url + "mark-feed-read";
                Expect(Request(route, "POST", origin, "token=" + token + "&feed=&group=1", out html) == 400
                    && store.Count(true) == 106, "a group request cannot mark a feed read");
                Expect(Request(route, "POST", origin, "token=" + token + "&feed=rss&through=" + store.LatestArrival(), out html) == 303
                    && store.Count(true, "rss") == 0 && store.Count(true, "otherfeed") == 1 && store.Count(false) == 106,
                    "bulk read reaches every post in one feed without changing other feeds or deleting posts");

                // Use local midnights around the October clock change and test both boundaries.
                var start = new DateTimeOffset(new DateTime(2026, 10, 25));
                var end = new DateTimeOffset(new DateTime(2026, 10, 26));
                var prior = Post("beforedate", "Before the selected date");
                var first = Post("startdate", "At the start of the selected date");
                var last = Post("enddate", "At the end of the selected date");
                var after = Post("afterdate", "After the selected date");
                prior.created = start.UtcDateTime.AddTicks(-1).ToString("o");
                first.created = start.UtcDateTime.ToString("o");
                last.created = end.UtcDateTime.AddTicks(-1).ToString("o");
                after.created = end.UtcDateTime.ToString("o");
                store.ImportHistoricalPosts("rss", new[] { prior, first, last, after });
                const string dates = "from=2026-10-25&to=2026-10-25";
                Expect(Request(server.Url + "?feed=rss&" + dates, "GET", null, null, out html) == 200
                    && html.Contains("At the start of the selected date") && html.Contains("At the end of the selected date")
                    && !html.Contains("Before the selected date") && !html.Contains("After the selected date"),
                    "date filtering covers the full local day and excludes adjacent days");
                Expect(Request(server.Url + "unread-counts?" + dates, "GET", null, null, out html) == 200
                    && html.Contains("\"rss\":2") && html.Contains("\"total\":2"), "periodic unread counts use the same publication dates");
                Expect(Request(route, "POST", origin, "token=" + token + "&feed=rss&from=2026-10-26&to=2026-10-25", out html) == 400
                    && store.Count(true, "rss") == 4, "reversed dates cannot mark articles read");
                Expect(Request(route, "POST", origin, "token=" + token + "&feed=otherfeed&target-feed=rss&through=" + store.LatestArrival() + "&" + dates, out html) == 303
                    && store.Items(true, "rss").Select(row => row[0]).OrderBy(id => id).SequenceEqual(new[] { after.id, prior.id }),
                    "a sidebar bulk read targets its feed and leaves articles outside the selected dates unread");
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static void TestRedditCollectionStart()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        var date = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        Func<string, int, Post> post = (id, hour) =>
        {
            var article = Post(id, id);
            article.created = date.AddHours(hour).ToString("o");
            article.publicationKnown = true;
            return article;
        };
        Func<string, Post[], PageResult> page = (next, articles) => new PageResult { next = next, posts = articles.ToList() };
        try
        {
            using (var store = new Store(path))
            {
                store.AddSource("default");
                Expect(store.Sources().Single(s => s.Name == "default").InitialRemaining == 100, "new Reddit feeds default to 100 articles");
                store.AddSource("count", initialCount: 3);
                var state = store.Sources().Single(s => s.Name == "count");
                var pin = post("pin", -400);
                pin.pinned = true;
                var first = page("/next", new[] { pin, post("one", 4), post("two", 3) });
                Expect(Collector.ProcessPage(state, first, true) == Collector.WalkResult.More && store.SavePage(state, first.posts, false) == 2,
                    "initial count ignores pins and saves progress with the page");
                store.SetRead("t3_one", true);
                store.ResetWalk("count", "Continuation expired.");
                store.AddSource("count", initialCount: 200);
            }
            using (var store = new Store(path))
            {
                // A restarted walk can see the first page again without consuming the remaining slot twice.
                var state = store.Sources().Single(s => s.Name == "count");
                var replay = page("/next", new[] { post("one", 4), post("two", 3), post("three", 2), post("four", 1) });
                Expect(state.InitialRemaining == 1 && Collector.ProcessPage(state, replay, true) == Collector.WalkResult.Complete
                    && store.SavePage(state, replay.posts, true) == 1 && store.Count(false, "count") == 3 && store.Count(true, "count") == 2,
                    "the selected count survives a restart, duplicate pages, and re-adding a feed");
                state = store.Sources().Single(s => s.Name == "count");
                var tooOld = page("/still-older", new[] { post("four", 1), post("five", -3) });
                Expect(state.RedditFloor == post("three", 2).created && Collector.ProcessPage(state, tooOld, false) == Collector.WalkResult.GapAtCutoff
                    && tooOld.posts.Count == 0, "catch-up cannot save or keep walking past the count boundary when anchors are missing");

                var byDate = new Source { Anchors = "", Head = "", Cursor = "", Seen = "", RedditFloor = date.ToString("o"), InitialRemaining = 0 };
                var mixed = page("/next", new[] { post("new", 1), post("old", -1), post("midnight", 0) });
                Expect(Collector.ProcessPage(byDate, mixed, true) == Collector.WalkResult.More
                    && mixed.posts.Select(p => p.id).SequenceEqual(new[] { "t3_new", "t3_midnight" }), "the starting date is inclusive and a single older post does not stop the walk");
                var older = page("/still-older", new[] { post("older", -2), post("oldest", -3) });
                Expect(Collector.ProcessPage(byDate, older, false) == Collector.WalkResult.Complete && older.posts.Count == 0
                    && byDate.InitialRemaining == -1, "date-based collection stops when the listing reaches older history");
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static void TestArchiveBridge()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        var cutoff = DateTimeOffset.Parse("2026-11-23T01:00:00Z");
        try
        {
            using (var store = new Store(path))
            {
                store.SetPaused(true);
                var settled = Post("settled", "Settled post");
                settled.created = cutoff.AddMinutes(-1).ToString("o");
                var recent = Post("recent", "Unsettled post");
                recent.created = cutoff.AddMinutes(1).ToString("o");
                store.ImportHistoricalPosts("rss", new[] { settled, recent });
                store.AddSource("empty");
                store.AddSource("pending");
                var pending = store.Sources().Single(s => s.Name == "pending");
                pending.Cursor = "/next";
                store.SavePage(pending, new Post[0], false);
                store.SeedRedditBridge(cutoff);
                var rss = store.Sources().Single(s => s.Name == "rss");
                Expect(rss.Anchors == settled.id && store.Sources().Single(s => s.Name == "pending").Cursor == "/next"
                    && store.Sidebar().Single(r => r[0] == "empty")[4].Contains("no earlier saved posts"),
                    "archive handoff excludes the unsettled tail, preserves a pending walk and reports an empty archive");

                // Simulate an exhausted listing after the newer page was saved.
                rss.Head = recent.id;
                store.RecordGap(rss, "Possible gap.");
                store.Note("rss", "No new posts added. Page limit reached. The next check will continue here.");
                using (var server = new Inbox(store, 55000 + new Random().Next(5000), name => System.Threading.Tasks.Task.FromResult("")))
                {
                    string html;
                    var url = server.Url + "check-status?feed=rss";
                    Expect(Request(url, "GET", null, null, out html) == 200 && html.Contains("class=check-warning"),
                        "a fresh gap stays red through a partial check");

                    // A clean overlap check clears the active warning while retaining the historical note.
                    rss = store.Sources().Single(s => s.Name == "rss");
                    Expect(rss.Anchors == recent.id, "an exhausted listing adopts its saved head");
                    rss.Head = recent.id;
                    rss.Status = "";
                    store.SavePage(rss, new Post[0], true);
                    store.Note("rss", "No new posts found.");
                    Expect(Request(url, "GET", null, null, out html) == 200 && !html.Contains("class=check-warning")
                        && html.Contains("No new posts found.<br><span class=coverage-history>Possible earlier gap:"),
                        "a clean check leaves the earlier gap as a muted note below the result");
                    Expect(Request(server.Url + "unread-counts", "GET", null, null, out html) == 200
                        && !Regex.Match(html, @"""warnings"":\[[^\]]*\]").Value.Contains("\"rss\""),
                        "sidebar refresh does not flag a historical gap");

                    store.Note("rss", "Reddit returned HTTP 403.");
                    Expect(Request(url, "GET", null, null, out html) == 200 && html.Contains("class=check-warning") && html.Contains("class=coverage-history"),
                        "a new error is red without reactivating the historical gap");
                }
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static void TestCleanup()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        var read = Post("purge1", "Read article");
        var unread = Post("purge2", "Unread article");
        try
        {
            using (var store = new Store(path))
            {
                store.ImportHistoricalPosts("rss", new[] { read, unread });
                store.SetRead(read.id, true);
                var mark = store.Items(false).Single(row => row[0] == read.id)[5];
                var beforeMark = DateTimeOffset.Parse(mark).AddTicks(-1).ToString("o");
                Expect(store.DeleteReadAndCompact(beforeMark).Item1 == 0 && store.Count(false) == 2,
                    "a confirmation cutoff never deletes a post marked read later");
                var result = store.DeleteReadAndCompact(mark);
                Expect(result.Item1 == 1 && result.Item2 && store.Count(false) == 1 && store.Count(true) == 1,
                    "cleanup deletes only read posts and successfully compacts the fixture database");
                Expect(!store.SetRead(read.id, false), "a stale read action cannot restore a removed post");
            }
            using (var store = new Store(path))
            {
                store.ImportHistoricalPosts("rss", new[] { read, unread });
                Expect(store.Items(false).Single()[0] == unread.id, "deleted IDs stay out of the inbox after reopen and re-import");
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static void TestPrunedActivity()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sqlite");
        var now = DateTimeOffset.UtcNow;
        try
        {
            using (var store = new Store(path))
            {
                // Two posts share a publication time and the feed belongs to two groups.
                var dayOne = Post("activity1", "First recent post");
                var dayTwo = Post("activity2", "Second recent post");
                var week = Post("activity3", "Older this week");
                var old = Post("activity4", "Outside both windows");
                var unread = Post("activity5", "Still in the archive");
                dayOne.created = dayTwo.created = unread.created = now.AddHours(-1).ToString("o");
                week.created = now.AddDays(-3).ToString("o");
                old.created = now.AddDays(-8).ToString("o");
                store.ImportHistoricalPosts("rss", new[] { dayOne, dayTwo, week, old });
                store.ImportHistoricalPosts("other", new[] { unread });
                var group = store.AddGroup("Activity");
                var otherGroup = store.AddGroup("Also subscribed");
                store.ReorganizeFeed("rss", new[] { group, otherGroup });
                foreach (var post in new[] { dayOne, dayTwo, week, old }) store.SetRead(post.id, true);

                var before = store.SidebarArticleCounts("rss", 0, now);
                Expect(before.SequenceEqual(new long[] { 0, 4, 4, 2, 3 }), "only articles published inside each window count toward activity");
                Expect(store.DeleteReadAndCompact(DateTimeOffset.UtcNow.ToString("o")).Item1 == 4, "activity fixture prunes its four read articles");
                Expect(store.SidebarArticleCounts("rss", 0, now).SequenceEqual(new long[] { 0, 0, 0, 2, 3 })
                    && store.SidebarArticleCounts("", group, now).SequenceEqual(new long[] { 0, 0, 0, 2, 3 })
                    && store.SidebarArticleCounts("", 0, now).SequenceEqual(new long[] { 0, 0, 1, 3, 4 }),
                    "pruning preserves rolling activity at feed, group and All feeds scope without inflating archive totals");
                Expect(store.SidebarArticleCounts("rss", 0, now.AddDays(1)).Skip(3).SequenceEqual(new long[] { 0, 3 })
                    && store.SidebarArticleCounts("rss", 0, now.AddDays(5)).Skip(3).SequenceEqual(new long[] { 0, 2 }),
                    "activity ages out of the daily and weekly windows at different times");

                // Query the original window again to distinguish physical expiration from filtering it out.
                store.ExpirePrunedActivity(now.AddDays(5));
                Expect(store.SidebarArticleCounts("rss", 0, now)[4] == 2, "expiration removes the timestamp older than seven days");
                store.ExpirePrunedActivity(now.AddDays(8));
                Expect(store.SidebarArticleCounts("rss", 0, now).Skip(3).SequenceEqual(new long[] { 0, 0 }),
                    "no extra publication times remain after the week has passed");
                store.ImportHistoricalPosts("rss", new[] { dayOne });
                Expect(store.Count(false, "rss") == 0, "activity expiration leaves deletion deduplication intact");
            }
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }

    private static int Request(string url, string method, string origin, string form, out string text)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = method;
        request.Timeout = 5000;
        request.AllowAutoRedirect = false;
        if (form != null)
        {
            request.Headers["Origin"] = origin;
            request.ContentType = "application/x-www-form-urlencoded";
            var bytes = Encoding.UTF8.GetBytes(form);
            request.ContentLength = bytes.Length;
            using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        }
        HttpWebResponse response;
        try { response = (HttpWebResponse)request.GetResponse(); }
        catch (WebException ex) { response = (HttpWebResponse)ex.Response; if (response == null) throw; }
        using (response)
        using (var reader = new StreamReader(response.GetResponseStream()))
        { text = reader.ReadToEnd(); return (int)response.StatusCode; }
    }
}
