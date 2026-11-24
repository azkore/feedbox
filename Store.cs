using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal sealed class Post
{
    public bool publicationKnown;
    public string id { get; set; }
    public string title { get; set; }
    public string path { get; set; }
    public string originalUrl { get; set; }
    public string created { get; set; }
    public bool pinned { get; set; }
    public string author { get; set; }
    public string body { get; set; }
    public string image { get; set; }
    public string content { get; set; }
    public string discussion { get; set; }
}

internal sealed class Source
{
    public string FeedUrl;
    public string Name, Label, Anchors, Cursor, Head, Seen, Status;
    public bool ExtraPage;
    public string RedditFloor = "", InitialIds = "";
    // -1 means no initial import, 0 means date-based, and a positive value counts articles still owed.
    public int InitialRemaining = -1;
}

internal sealed class FeedGroup
{
    public int Id;
    public string Name;
    public readonly List<string> Members = new List<string>();
}

internal sealed class ArticleFilter
{
    public const int Unread = 1, Read = 2, Unmarked = 4, Marked = 8, Inbox = Unread | Unmarked, Everything = 15;
    public static readonly ArticleFilter All = new ArticleFilter("", "", "", "", "");
    public readonly string From, To, StartUtc, EndUtc, Search;
    public readonly int Flags;
    public bool Active { get { return From != "" || To != ""; } }

    private ArticleFilter(string from, string to, string startUtc, string endUtc, string search, int flags = -1)
    {
        From = from;
        To = to;
        StartUtc = startUtc;
        EndUtc = endUtc;
        Search = search;
        Flags = flags;
    }

    public ArticleFilter WithFlags(int flags) { return new ArticleFilter(From, To, StartUtc, EndUtc, Search, flags); }

    public static bool TryCreate(string from, string to, out ArticleFilter dates, string search = null)
    {
        const string format = "yyyy-MM-dd";
        dates = All;
        from = from ?? "";
        to = to ?? "";
        search = (search ?? "").Trim();
        if (search.Length > 120) return false;
        DateTime first = DateTime.MinValue, last = DateTime.MaxValue.Date;
        if ((from != "" && !DateTime.TryParseExact(from, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out first))
            || (to != "" && !DateTime.TryParseExact(to, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out last)) || first > last) return false;
        if (from == "" && to == "" && search == "") return true;

        // Convert each local midnight separately so the range still matches the date headings on DST days.
        // Use a UTC second prefix: saved articles have varying fractional precision but share this prefix.
        const string utcPrefix = "yyyy-MM-dd'T'HH:mm:ss";
        dates = new ArticleFilter(from, to,
            from == "" ? "" : first.ToUniversalTime().ToString(utcPrefix, CultureInfo.InvariantCulture),
            to == "" || last == DateTime.MaxValue.Date ? "" : last.AddDays(1).ToUniversalTime().ToString(utcPrefix, CultureInfo.InvariantCulture), search);
        return true;
    }
}

internal sealed class Store : IDisposable
{
    private const string PublicationFilter = " AND (?='' OR published>=?) AND (?='' OR published<?)";
    private const string SearchFilter = " AND (?='' OR inbox_contains(title,?) OR inbox_contains(body,?))";
    private static string StateFilter(int flags)
    {
        // Each pair contributes an independent requirement. An unchecked pair matches no articles.
        if ((flags & 3) == 0 || (flags & 12) == 0) return " AND 0";
        return ((flags & 3) == ArticleFilter.Unread ? " AND read_at IS NULL" : (flags & 3) == ArticleFilter.Read ? " AND read_at IS NOT NULL" : "")
            + ((flags & 12) == ArticleFilter.Unmarked ? " AND marked=0" : (flags & 12) == ArticleFilter.Marked ? " AND marked=1" : "");
    }
    private static readonly SqlFunction ContainsFunction = ContainsText;
    private readonly object gate = new object();
    private readonly string databasePath;
    private IntPtr db;

    public Store(string path)
    {
        databasePath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        Check(sqlite3_open_v2(Bytes(path), out db, 6, IntPtr.Zero));
        Check(sqlite3_create_function_v2(db, Bytes("inbox_contains"), 2, 1, IntPtr.Zero, ContainsFunction,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        // Keep the working file readable by other SQLite tools while the inbox runs.
        Check(sqlite3_busy_timeout(db, 5000));
        Run("PRAGMA journal_mode=WAL");
        var version = int.Parse(Query("PRAGMA user_version")[0][0], CultureInfo.InvariantCulture);
        if (version < 0 || version > 15) throw new InvalidOperationException("Unsupported database version.");
        Transaction(() =>
        {
            Run(@"CREATE TABLE IF NOT EXISTS sources (name TEXT PRIMARY KEY, active INTEGER NOT NULL DEFAULT 1,
                anchors TEXT NOT NULL DEFAULT '', cursor TEXT NOT NULL DEFAULT '', head TEXT NOT NULL DEFAULT '',
                seen TEXT NOT NULL DEFAULT '', extra INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'Waiting for first check')");
            Run(@"CREATE TABLE IF NOT EXISTS posts (id TEXT PRIMARY KEY, source TEXT NOT NULL, title TEXT NOT NULL,
                path TEXT NOT NULL, published TEXT NOT NULL, first_seen TEXT NOT NULL, read_at TEXT)");
            Run("CREATE INDEX IF NOT EXISTS posts_unread ON posts(read_at, published DESC)");
            Run("CREATE INDEX IF NOT EXISTS posts_source_unread ON posts(source, read_at, published DESC, id DESC)");
            // Existing archived rows keep their IDs and read state when context is added.
            if (version < 2)
            {
                Run("ALTER TABLE posts ADD COLUMN author TEXT NOT NULL DEFAULT ''");
                Run("ALTER TABLE posts ADD COLUMN body TEXT NOT NULL DEFAULT ''");
                Run("ALTER TABLE posts ADD COLUMN image TEXT NOT NULL DEFAULT ''");
            }
            // Store the source type and the original link separately from Reddit's collection paths.
            if (version < 3)
            {
                Run("ALTER TABLE sources ADD COLUMN kind TEXT NOT NULL DEFAULT 'reddit'");
                Run("ALTER TABLE sources ADD COLUMN label TEXT NOT NULL DEFAULT ''");
                Run("ALTER TABLE posts ADD COLUMN original_url TEXT NOT NULL DEFAULT ''");
                Run("UPDATE sources SET label='r/' || name WHERE label=''");
                Run("UPDATE posts SET original_url='https://www.reddit.com' || path WHERE path LIKE '/r/%'");
            }
            if (version < 4)
            {
                // Do not start previously paused feeds until the user resumes collection globally.
                Run("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
                Run("INSERT OR IGNORE INTO settings VALUES ('collection_paused', CASE WHEN EXISTS (SELECT 1 FROM sources WHERE active=0) THEN '1' ELSE '0' END)");
            }
            // Keep old plain text as a fallback while newer captures add formatting.
            if (version < 5)
            {
                Run("ALTER TABLE posts ADD COLUMN content TEXT NOT NULL DEFAULT ''");
            }
            if (version < 6)
            {
                // Membership belongs to the source, so a feed can appear in more than one group.
                Run("CREATE TABLE feed_groups (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, position INTEGER NOT NULL)");
                Run("CREATE TABLE feed_group_members (group_id INTEGER NOT NULL, source TEXT NOT NULL, position INTEGER NOT NULL, PRIMARY KEY(group_id, source))");
                Run("ALTER TABLE sources ADD COLUMN last_attempt TEXT NOT NULL DEFAULT ''");
            }
            if (version < 7)
            {
                // Keep only the aliases needed to recognize posts the user has pruned.
                Run("CREATE TABLE deleted_posts (id TEXT PRIMARY KEY) WITHOUT ROWID");
                Run("PRAGMA user_version=7");
            }
            if (version < 8)
            {
                // Persist the rolling request window together with the archive, independent of account or feed.
                Run("CREATE TABLE reddit_requests (at TEXT PRIMARY KEY) WITHOUT ROWID");
                Run("PRAGMA user_version=8");
            }
            if (version < 9)
            {
                // A later successful check must not erase a previously detected coverage gap.
                Run("ALTER TABLE sources ADD COLUMN coverage_note TEXT NOT NULL DEFAULT ''");
                Run("PRAGMA user_version=9");
            }
            if (version < 10)
            {
                Run("ALTER TABLE sources ADD COLUMN feed_url TEXT NOT NULL DEFAULT ''");
                Run("PRAGMA user_version=10");
            }
            if (version < 11)
            {
                // Keep the display name separate from publisher titles and Reddit URL spelling.
                Run("ALTER TABLE sources ADD COLUMN custom_label TEXT NOT NULL DEFAULT ''");
                Run("PRAGMA user_version=11");
            }
            if (version < 12)
            {
                // Bulk-read ownership stays with the article until another explicit read action.
                Run("ALTER TABLE posts ADD COLUMN bulk_mark TEXT");
                Run("CREATE INDEX posts_bulk_mark ON posts(bulk_mark) WHERE bulk_mark IS NOT NULL");
                Run("ALTER TABLE posts ADD COLUMN arrival INTEGER NOT NULL DEFAULT 0");
                Run("ALTER TABLE sources ADD COLUMN rss_since TEXT NOT NULL DEFAULT ''");
                Run("ALTER TABLE sources ADD COLUMN rss_baseline_pending INTEGER NOT NULL DEFAULT 0");
                Run("CREATE INDEX posts_arrival ON posts(arrival,id)");
                Run("INSERT INTO settings VALUES ('last_arrival','0')");
                Run("PRAGMA user_version=12");
            }
            if (version < 13)
            {
                // Retain the discussion destination independently of the headline link.
                Run("ALTER TABLE posts ADD COLUMN discussion TEXT NOT NULL DEFAULT ''");
                Run("ALTER TABLE sources ADD COLUMN open_discussion INTEGER NOT NULL DEFAULT 0");
                Run("PRAGMA user_version=13");
            }
            if (version < 14)
            {
                Run("ALTER TABLE posts ADD COLUMN marked INTEGER NOT NULL DEFAULT 0");
                Run("CREATE INDEX posts_marked ON posts(published,id) WHERE marked=1");
                Run("PRAGMA user_version=14");
            }
            if (version < 15)
            {
                // Keep the chosen boundary, and commit initial progress alongside each saved page.
                Run("ALTER TABLE sources ADD COLUMN reddit_floor TEXT NOT NULL DEFAULT ''");
                Run("ALTER TABLE sources ADD COLUMN reddit_initial_remaining INTEGER NOT NULL DEFAULT -1");
                Run("ALTER TABLE sources ADD COLUMN reddit_initial_ids TEXT NOT NULL DEFAULT ''");
                Run("PRAGMA user_version=15");
            }
        });
    }

    public List<Source> Sources(int limit = -1, bool dueOnly = false)
    {
        // Revisit quiet feeds less often; unfinished walks remain eligible at the busy-feed interval.
        var due = dueOnly ? @" AND (last_attempt='' OR julianday(last_attempt) <= julianday('now') -
            CASE WHEN cursor<>'' OR reddit_initial_remaining>=0 OR EXISTS (SELECT 1 FROM posts WHERE source=sources.name AND published >= strftime('%Y-%m-%dT%H:%M:%S','now','-1 day'))
            THEN 2.0/24 ELSE 12.0/24 END)" : "";
        return Query("SELECT name,anchors,cursor,head,seen,extra,status,label,reddit_floor,reddit_initial_remaining,reddit_initial_ids FROM sources WHERE kind='reddit'" + due + " ORDER BY last_attempt,name LIMIT ?", limit.ToString(CultureInfo.InvariantCulture))
            .Select(r => new Source { Name = r[0], Anchors = r[1], Cursor = r[2], Head = r[3], Seen = r[4], ExtraPage = r[5] == "1", Status = r[6], Label = r[7],
                RedditFloor = r[8], InitialRemaining = int.Parse(r[9], CultureInfo.InvariantCulture), InitialIds = r[10] }).ToList();
    }

    public void RecordAttempt(string name)
    {
        Run("UPDATE sources SET last_attempt=? WHERE name=?", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), name);
    }

    public bool HasSource(string name)
    {
        return Query("SELECT 1 FROM sources WHERE name=?", name).Count != 0;
    }

    internal static string RssKey(string value)
    {
        using (var hash = SHA256.Create())
            return "rss_" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
    }

    public string AddRss(Uri url, int group)
    {
        string name = null;
        Transaction(() =>
        {
            name = RssSubscriptionKey(url);
            Run("INSERT OR IGNORE INTO sources(name, label, kind, feed_url) VALUES (?,?,'rss',?)", name, url.Host, url.AbsoluteUri);
            if (group > 0) Run(@"INSERT OR IGNORE INTO feed_group_members(group_id,source,position)
                SELECT id,?,COALESCE((SELECT MAX(position)+1 FROM feed_group_members WHERE group_id=feed_groups.id),0)
                FROM feed_groups WHERE id=?", name, group.ToString(CultureInfo.InvariantCulture));
        });
        return name;
    }

    private string RssSubscriptionKey(Uri url)
    {
        // Resolve the current URL inside the caller's transaction; older keys may reflect a previous URL.
        var existing = Query("SELECT name FROM sources WHERE kind='rss' AND feed_url=? LIMIT 1", url.AbsoluteUri).SingleOrDefault();
        if (existing != null) return existing[0];
        var key = RssKey(url.AbsoluteUri);
        // The original URL can be subscribed to again after the existing subscription moves.
        for (int i = 1; HasSource(key); i++) key = RssKey(url.AbsoluteUri + "\n" + i.ToString(CultureInfo.InvariantCulture));
        return key;
    }

    public enum RssSourceEdit { Saved, Missing, Stale, Duplicate }

    public RssSourceEdit EditRssSource(string source, string version, Uri url)
    {
        var result = RssSourceEdit.Missing;
        Transaction(() =>
        {
            var current = RssToCheck(source);
            if (current == null) return;
            if (current.FeedUrl == url.AbsoluteUri) { result = RssSourceEdit.Saved; return; }
            if (RssKey(current.FeedUrl) != version) { result = RssSourceEdit.Stale; return; }
            if (Query("SELECT 1 FROM sources WHERE kind='rss' AND feed_url=? LIMIT 1", url.AbsoluteUri).Count != 0)
            { result = RssSourceEdit.Duplicate; return; }

            // Preserve the subscription key and its article identities; make the new URL due for collection.
            Run("UPDATE sources SET feed_url=?,last_attempt='',status='Source URL changed. Waiting for the next check.' WHERE name=?", url.AbsoluteUri, source);
            result = RssSourceEdit.Saved;
        });
        return result;
    }

    public bool RecordRssAttempt(Source source)
    {
        lock (gate)
        {
            Run("UPDATE sources SET last_attempt=? WHERE name=? AND kind='rss' AND feed_url=?",
                Stamp(DateTimeOffset.UtcNow), source.Name, source.FeedUrl);
            return Query("SELECT changes()")[0][0] == "1";
        }
    }

    public bool NoteRss(Source source, string status)
    {
        lock (gate)
        {
            Run("UPDATE sources SET status=? WHERE name=? AND kind='rss' AND feed_url=?", status, source.Name, source.FeedUrl);
            return Query("SELECT changes()")[0][0] == "1";
        }
    }

    public Source RssToCheck(string name = null)
    {
        // Manual checks target an exact key. Scheduled checks rotate through feeds due once an hour.
        var rows = name == null
            ? Query(@"SELECT name,label,feed_url FROM sources WHERE kind='rss'
                AND (last_attempt='' OR julianday(last_attempt) <= julianday('now') - 1.0/24) ORDER BY last_attempt,name LIMIT 1")
            : Query("SELECT name,label,feed_url FROM sources WHERE kind='rss' AND name=?", name);
        return rows.Count == 0 ? null : new Source { Name = rows[0][0], Label = rows[0][1], FeedUrl = rows[0][2] };
    }

    public int SaveRss(string source, string title, IEnumerable<Post> posts, string expectedUrl = null)
    {
        int added = 0;
        Transaction(() =>
        {
            var policy = Query("SELECT rss_since,rss_baseline_pending,feed_url FROM sources WHERE name=? AND kind='rss'", source).SingleOrDefault();
            // Discard a response from the previous URL if the subscription changed during the download.
            if (expectedUrl != null && (policy == null || policy[2] != expectedUrl)) { added = -1; return; }
            if (policy == null) return;
            if (policy[0] != "")
            {
                var cutoff = DateTimeOffset.Parse(policy[0], CultureInfo.InvariantCulture);
                var candidates = new List<Post>();
                foreach (var post in posts)
                {
                    // During the first successful fetch, remember undated entries by ID without adding them to the archive.
                    bool skipped = post.publicationKnown ? DateTimeOffset.Parse(post.created, CultureInfo.InvariantCulture) <= cutoff : policy[1] == "1";
                    if (skipped)
                    {
                        if (policy[1] == "1") Run("INSERT OR IGNORE INTO deleted_posts(id) VALUES (?)", post.id);
                    }
                    else candidates.Add(post);
                }
                posts = candidates;
            }
            added = SavePosts(source, posts);
            Run("UPDATE sources SET label=CASE WHEN ?='' THEN label ELSE ? END,rss_baseline_pending=0 WHERE name=? AND kind='rss'", title, title, source);
        });
        return added;
    }

    public List<string[]> RssSubscriptions()
    {
        return Query("SELECT name,COALESCE(NULLIF(custom_label,''),label),feed_url FROM sources WHERE kind='rss' ORDER BY label,name");
    }

    public string[] RssBody(string id)
    {
        var rows = Query("SELECT p.content,p.original_url,s.feed_url FROM posts p JOIN sources s ON s.name=p.source WHERE p.id=? AND s.kind='rss'", id);
        return rows.Count == 0 || string.IsNullOrEmpty(rows[0][0]) ? null : rows[0];
    }

    public string RssArticleUrl(string id)
    {
        var rows = Query("SELECT p.original_url FROM posts p JOIN sources s ON s.name=p.source WHERE p.id=? AND s.kind='rss' AND COALESCE(p.image,'')=''", id);
        return rows.Count == 0 ? "" : rows[0][0];
    }

    public bool SetOpenDiscussion(string source, bool enabled)
    {
        lock (gate)
        {
            Run("UPDATE sources SET open_discussion=? WHERE name=? AND kind='rss'", enabled ? "1" : "0", source);
            return Query("SELECT changes()")[0][0] == "1";
        }
    }

    public int RestoreRssBodies(string source, IEnumerable<Post> posts)
    {
        int updated = 0;
        Transaction(() =>
        {
            // Fill in HTML lost by the old collector. Never add rows or alter read state or dates here.
            foreach (var post in posts.Where(p => !string.IsNullOrEmpty(p.content)))
            {
                Run("UPDATE posts SET content=?,discussion=COALESCE(NULLIF(?,''),discussion) WHERE id=? AND source=? AND COALESCE(content,'')=''",
                    post.content, post.discussion, post.id, source);
                if (Query("SELECT changes()")[0][0] == "1") updated++;
            }
        });
        return updated;
    }

    public Tuple<int, int> ImportRss(IEnumerable<OpmlFeed> feeds, bool newOnly)
    {
        int added = 0, existing = 0;
        Transaction(() =>
        {
            var cutoff = newOnly ? Stamp(DateTimeOffset.UtcNow) : "";
            var folders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Groups()) if (!folders.ContainsKey(folder.Name)) folders.Add(folder.Name, folder.Id);
            var visited = new HashSet<string>();
            foreach (var entry in feeds)
            {
                var name = RssSubscriptionKey(entry.Url);
                if (visited.Add(name))
                {
                    // Import labels and the first-fetch choice only for new subscriptions.
                    Run("INSERT OR IGNORE INTO sources(name,label,kind,feed_url,custom_label,rss_since,rss_baseline_pending) VALUES (?,?,'rss',?,?,?,?)",
                        name, entry.Url.Host, entry.Url.AbsoluteUri, entry.Label, cutoff, newOnly ? "1" : "0");
                    if (Query("SELECT changes()")[0][0] == "1") added++;
                    else existing++;
                }
                if (entry.Group == "") continue;
                int group;
                if (!folders.TryGetValue(entry.Group, out group))
                {
                    Run("INSERT INTO feed_groups(name,position) SELECT ?,COALESCE(MAX(position)+1,0) FROM feed_groups", entry.Group);
                    group = int.Parse(Query("SELECT last_insert_rowid()")[0][0], CultureInfo.InvariantCulture);
                    folders.Add(entry.Group, group);
                }
                var id = group.ToString(CultureInfo.InvariantCulture);
                Run("INSERT OR IGNORE INTO feed_group_members(group_id,source,position) VALUES (?,?,COALESCE((SELECT MAX(position)+1 FROM feed_group_members WHERE group_id=?),0))", id, name, id);
            }
        });
        return Tuple.Create(added, existing);
    }

    public bool IsPaused { get { return Query("SELECT value FROM settings WHERE key='collection_paused'")[0][0] == "1"; } }

    private static string Stamp(DateTimeOffset time) { return time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }

    public void SetRedditCooldown(DateTimeOffset until)
    {
        Run("INSERT OR REPLACE INTO settings VALUES ('reddit_retry_after',max(?,COALESCE((SELECT value FROM settings WHERE key='reddit_retry_after'),'')))", Stamp(until));
    }

    public string RedditLimit(DateTimeOffset now, bool reserve)
    {
        string reason = null;
        Transaction(() =>
        {
            // Reserve before sending, so a restart cannot undo a request that Reddit may already have received.
            Run("DELETE FROM reddit_requests WHERE at<=?", Stamp(now.AddHours(-1)));
            var cooldown = Query("SELECT value FROM settings WHERE key='reddit_retry_after' AND value>?", Stamp(now));
            var window = Query("SELECT count(*), min(at), max(at) FROM reddit_requests")[0];
            if (cooldown.Count != 0) reason = "Reddit rate limit until " + DateTimeOffset.Parse(cooldown[0][0]).ToLocalTime().ToString("g") + ".";
            else if (int.Parse(window[0]) >= 60) reason = "Hourly listing request limit reached. Next slot: " + DateTimeOffset.Parse(window[1]).AddHours(1).ToLocalTime().ToString("T") + ".";
            else if (string.CompareOrdinal(window[2], Stamp(now.AddSeconds(-10))) > 0) reason = "Please wait ten seconds between listing requests.";
            else if (reserve) Run("INSERT INTO reddit_requests VALUES (?)", Stamp(now));
        });
        return reason;
    }

    public void SetPaused(bool paused)
    {
        Run("UPDATE settings SET value=? WHERE key='collection_paused'", paused ? "1" : "0");
    }

    public void AddSource(string name, int group = 0, int initialCount = 100, string since = "")
    {
        // Preserve the supplied casing for Reddit requests while using the same local subscription key.
        var label = "r/" + name;
        name = name.ToLowerInvariant();
        Transaction(() =>
        {
            // Adding an existing feed to another group must not restart or change collection.
            Run("INSERT OR IGNORE INTO sources(name,label,kind,reddit_floor,reddit_initial_remaining) VALUES (?,?,'reddit',?,?)",
                name, label, since, (since == "" ? initialCount : 0).ToString(CultureInfo.InvariantCulture));
            Run("UPDATE sources SET label=? WHERE name=? AND label=? AND kind='reddit'", label, name, "r/" + name);
            if (group > 0) Run(@"INSERT OR IGNORE INTO feed_group_members(group_id,source,position)
                SELECT id,?,COALESCE((SELECT MAX(position)+1 FROM feed_group_members WHERE group_id=feed_groups.id),0)
                FROM feed_groups WHERE id=?", name, group.ToString(CultureInfo.InvariantCulture));
        });
    }

    private void InsertSource(string name, string label)
    {
        Run("INSERT OR IGNORE INTO sources(name,label,kind) VALUES (?,?,'reddit')", name, label);
    }

    public void ImportGroups(IEnumerable<FeedGroup> folders, IDictionary<string, string> labels)
    {
        Transaction(() =>
        {
            // Metadata-only import: keep articles, read state and every source's collection checkpoint.
            int position = 0;
            foreach (var folder in folders)
            {
                Run("INSERT OR IGNORE INTO feed_groups(name,position) VALUES (?,?)", folder.Name, (position++).ToString(CultureInfo.InvariantCulture));
                var group = Query("SELECT id FROM feed_groups WHERE name=?", folder.Name)[0][0];
                int memberPosition = 0;
                foreach (var member in folder.Members.Distinct())
                {
                    InsertSource(member, labels[member]);
                    Run("UPDATE sources SET label=? WHERE name=? AND kind='reddit'", labels[member], member);
                    Run("INSERT OR IGNORE INTO feed_group_members(group_id,source,position) VALUES (?,?,?)",
                        group, member, (memberPosition++).ToString(CultureInfo.InvariantCulture));
                }
            }
            // Clear only the old, manually inserted archive note.
            Run("UPDATE sources SET status='' WHERE name='fireemblem' AND status=?", "October 13-November 2 backfilled once from Arctic Shift. Archive coverage is not guaranteed.");
        });
    }

    public List<FeedGroup> Groups()
    {
        return Query(@"SELECT g.id,g.name,m.source FROM feed_groups g LEFT JOIN feed_group_members m ON m.group_id=g.id
            ORDER BY g.position,g.id,m.position,m.source").GroupBy(row => row[0]).Select(rows =>
            {
                var group = new FeedGroup { Id = int.Parse(rows.Key, CultureInfo.InvariantCulture), Name = rows.First()[1] };
                group.Members.AddRange(rows.Where(row => row[2] != null).Select(row => row[2]));
                return group;
            }).ToList();
    }

    public int AddGroup(string name)
    {
        int id = 0;
        Transaction(() =>
        {
            // New groups follow the existing sidebar order.
            Run("INSERT OR IGNORE INTO feed_groups(name,position) SELECT ?,COALESCE(MAX(position)+1,0) FROM feed_groups", name);
            id = int.Parse(Query("SELECT id FROM feed_groups WHERE name=?", name)[0][0], CultureInfo.InvariantCulture);
        });
        return id;
    }

    public bool MoveFeed(string source, int fromGroup, int toGroup)
    {
        bool moved = false;
        var from = fromGroup.ToString(CultureInfo.InvariantCulture);
        var to = toGroup.ToString(CultureInfo.InvariantCulture);
        Transaction(() =>
        {
            if (!HasSource(source) || Query("SELECT id FROM feed_groups WHERE id=?", to).Count == 0) return;
            // Reject a stale drag before changing membership; zero refers to a genuinely ungrouped feed.
            var memberships = Query("SELECT group_id FROM feed_group_members WHERE source=?", source);
            if (fromGroup == 0 ? memberships.Count != 0 : !memberships.Any(row => row[0] == from)) return;
            if (fromGroup != toGroup)
            {
                // Only move the dragged membership. Other copies of the feed and its articles stay in place.
                Run(@"INSERT OR IGNORE INTO feed_group_members(group_id,source,position)
                    VALUES (?,?,COALESCE((SELECT MAX(position)+1 FROM feed_group_members WHERE group_id=?),0))", to, source, to);
                if (fromGroup != 0) Run("DELETE FROM feed_group_members WHERE group_id=? AND source=?", from, source);
            }
            moved = true;
        });
        return moved;
    }

    public bool RenameFeed(string source, string label)
    {
        lock (gate)
        {
            Run("UPDATE sources SET custom_label=? WHERE name=?", label, source);
            return Query("SELECT changes()")[0][0] == "1";
        }
    }

    public bool ReorganizeFeed(string source, IEnumerable<int> groups)
    {
        bool saved = false;
        Transaction(() =>
        {
            var desired = new HashSet<string>(groups.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            var valid = new HashSet<string>(Query("SELECT id FROM feed_groups").Select(row => row[0]));
            if (!HasSource(source) || !desired.IsSubsetOf(valid)) return;
            // Change only the memberships the reader added or removed, in a single transaction.
            var current = Query("SELECT group_id FROM feed_group_members WHERE source=?", source).Select(row => row[0]).ToList();
            foreach (var id in current.Where(id => !desired.Contains(id)))
                Run("DELETE FROM feed_group_members WHERE source=? AND group_id=?", source, id);
            foreach (var id in desired.Except(current))
                Run(@"INSERT INTO feed_group_members(group_id,source,position)
                    VALUES (?,?,COALESCE((SELECT MAX(position)+1 FROM feed_group_members WHERE group_id=?),0))", id, source, id);
            saved = true;
        });
        return saved;
    }

    public bool DeleteFeed(string source)
    {
        bool deleted = false;
        Transaction(() =>
        {
            if (!HasSource(source)) return;
            // Remove a subscription from all groups and from the archive atomically.
            Run("DELETE FROM posts WHERE source=?", source);
            Run("DELETE FROM feed_group_members WHERE source=?", source);
            Run("DELETE FROM sources WHERE name=?", source);
            deleted = true;
        });
        return deleted;
    }

    // Save a complete sidebar order without moving feeds between folders.
    public void SetGroupOrder(IList<int> orderedIds)
    {
        Transaction(() =>
        {
            var existing = Query("SELECT id FROM feed_groups").Select(row => int.Parse(row[0], CultureInfo.InvariantCulture)).ToList();
            // Reject stale or incomplete orders before assigning any positions.
            if (existing.Count != orderedIds.Count || !new HashSet<int>(existing).SetEquals(orderedIds))
                throw new ArgumentException("The group order must contain each existing group once.", "orderedIds");
            for (int i = 0; i < orderedIds.Count; i++)
                Run("UPDATE feed_groups SET position=? WHERE id=?", i.ToString(CultureInfo.InvariantCulture), orderedIds[i].ToString(CultureInfo.InvariantCulture));
        });
    }

    public void Note(string source, string status)
    {
        Run("UPDATE sources SET status=? WHERE name=?", status, source);
    }

    public void ResetWalk(string source, string status)
    {
        // An invalid continuation cannot advance the last successful overlap point.
        Run("UPDATE sources SET cursor='', head='', seen='', extra=0, status=? WHERE name=?", status, source);
    }

    public void RecordGap(Source scan, string status)
    {
        // Start future checks from this walk's newest saved posts rather than repeatedly chasing unreachable IDs.
        Run(@"UPDATE sources SET anchors=CASE WHEN ?='' THEN anchors ELSE ? END, cursor='',head='',seen='',extra=0,
            status=?,coverage_note=CASE WHEN coverage_note='' THEN ? ELSE coverage_note END WHERE name=?", scan.Head, scan.Head,
            status, "Possible earlier gap: the check could not reach previously saved posts on " + DateTimeOffset.UtcNow.ToLocalTime().ToString("g") + ".", scan.Name);
    }

    public List<string[]> SeedRedditBridge(DateTimeOffset settledThrough)
    {
        var result = new List<string[]>();
        Transaction(() =>
        {
            if (!IsPaused) throw new InvalidOperationException("Pause collection before preparing the archive handoff.");
            foreach (var row in Query("SELECT name,anchors,cursor FROM sources WHERE kind='reddit' ORDER BY name"))
            {
                if (row[1] != "" || row[2] != "") { result.Add(new[] { row[0], "existing", "0" }); continue; }
                // Recheck the archive's unsettled tail directly. The collector only counts IDs seen unpinned on Reddit.
                var candidates = Query(@"SELECT id FROM posts WHERE source=? AND published<=? ORDER BY published DESC,id DESC LIMIT 24",
                    row[0], Stamp(settledThrough)).Select(p => p[0]).Where(id => Regex.IsMatch(id, @"\At3_[a-z0-9]+\z")).ToArray();
                if (candidates.Length == 0)
                    Run("UPDATE sources SET coverage_note=? WHERE name=?", "Archive overlap could not be set up: no earlier saved posts to compare.", row[0]);
                else
                    Run("UPDATE sources SET anchors=? WHERE name=?", string.Join(",", candidates), row[0]);
                result.Add(new[] { row[0], candidates.Length == 0 ? "no earlier posts" : "seeded", candidates.Length.ToString(CultureInfo.InvariantCulture) });
            }
        });
        return result;
    }

    public int SavePage(Source scan, IEnumerable<Post> posts, bool complete)
    {
        int added = 0;
        Transaction(() =>
        {
            if (!HasSource(scan.Name)) return; // Do not bring back articles from a deleted feed.
            added = SavePosts(scan.Name, posts);
            // Commit the page and the cursor together so a restart cannot skip unsaved posts.
            var remaining = scan.InitialRemaining.ToString(CultureInfo.InvariantCulture);
            if (complete)
                Run("UPDATE sources SET anchors=?,cursor='',head='',seen='',extra=0,status=?,reddit_floor=?,reddit_initial_remaining=?,reddit_initial_ids=? WHERE name=?",
                     scan.Head, scan.Status, scan.RedditFloor, remaining, scan.InitialIds, scan.Name);
            else
                Run("UPDATE sources SET cursor=?,head=?,seen=?,extra=?,status=?,reddit_floor=?,reddit_initial_remaining=?,reddit_initial_ids=? WHERE name=?",
                     scan.Cursor, scan.Head, scan.Seen, scan.ExtraPage ? "1" : "0", scan.Status, scan.RedditFloor, remaining, scan.InitialIds, scan.Name);
        });
        return added;
    }

    public void ImportHistoricalPosts(string name, IEnumerable<Post> posts, bool keepExisting = false)
    {
        // Historical posts can be added without moving the New listing's checkpoint.
        Transaction(() => { InsertSource(name, "r/" + name); SavePosts(name, posts, keepExisting); });
    }

    private int SavePosts(string name, IEnumerable<Post> posts, bool keepExisting = false)
    {
        var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var arrival = LatestArrival();
        int added = 0;
        foreach (var post in posts)
        {
            // Rediscovery can refresh the title and link, never the user's read state.
            Run("INSERT OR IGNORE INTO posts(id,source,title,path,published,first_seen,arrival) SELECT ?,?,?,?,?,?,? WHERE NOT EXISTS (SELECT 1 FROM deleted_posts WHERE id=?)",
                post.id, name, post.title, post.path, post.created, now, (arrival + 1).ToString(CultureInfo.InvariantCulture), post.id);
            // A third-party backfill must not replace content already fetched directly.
            // Count only inserted rows, before the metadata UPDATE replaces SQLite's changes count.
            bool inserted = Query("SELECT changes()")[0][0] == "1";
            if (inserted) { added++; arrival++; }
            if (keepExisting && !inserted) continue;
            // A shorter listing card must not erase context captured on an earlier check.
            Run("UPDATE posts SET title=?,path=?,author=COALESCE(NULLIF(?,''),author),body=COALESCE(NULLIF(?,''),body),image=COALESCE(NULLIF(?,''),image),original_url=COALESCE(NULLIF(?,''),original_url),content=COALESCE(NULLIF(?,''),content),discussion=COALESCE(NULLIF(?,''),discussion) WHERE id=?",
                 post.title, post.path, post.author, post.body, post.image, post.originalUrl, post.content != null && post.content.Length <= RssCollector.MaxArticleHtml ? post.content : null, post.discussion, post.id);
        }
        // Publish the sequence in the same transaction as the articles, including across restarts.
        if (added > 0) Run("UPDATE settings SET value=? WHERE key='last_arrival'", arrival.ToString(CultureInfo.InvariantCulture));
        return added;
    }

    public long LatestArrival()
    {
        return long.Parse(Query("SELECT value FROM settings WHERE key='last_arrival'")[0][0], CultureInfo.InvariantCulture);
    }

    public List<string[]> Sidebar()
    {
        // Keep the reading sidebar independent of collection state.
        return Query(@"SELECT s.name,s.status,SUM(CASE WHEN p.id IS NOT NULL AND p.read_at IS NULL THEN 1 ELSE 0 END),COALESCE(NULLIF(s.custom_label,''),s.label),s.coverage_note,s.open_discussion
            FROM sources s LEFT JOIN posts p ON p.source=s.name GROUP BY s.name ORDER BY s.name");
    }

    public List<string[]> CheckStatuses(string source, int group)
    {
        // Refresh collector status without querying or re-rendering the reading queue.
        return Query(@"SELECT COALESCE(NULLIF(custom_label,''),label),status,coverage_note,last_attempt FROM sources s
            WHERE (?1='' OR name=?1) AND (CAST(?2 AS INTEGER)=0 OR EXISTS
                (SELECT 1 FROM feed_group_members m WHERE m.group_id=CAST(?2 AS INTEGER) AND m.source=s.name))
            ORDER BY name", source, group.ToString(CultureInfo.InvariantCulture));
    }

    public Dictionary<string, long> UnreadCounts(ArticleFilter dates = null)
    {
        // Use one SQLite snapshot for all badges, including feeds that appear in multiple groups.
        dates = dates ?? ArticleFilter.All;
        return Query("SELECT source,count(*) FROM posts WHERE 1" + StateFilter(dates.Flags < 0 ? ArticleFilter.Inbox : dates.Flags) + PublicationFilter + SearchFilter + " GROUP BY source",
            dates.StartUtc, dates.StartUtc, dates.EndUtc, dates.EndUtc, dates.Search, dates.Search, dates.Search)
            .ToDictionary(row => row[0], row => long.Parse(row[1], CultureInfo.InvariantCulture));
    }

    public long Count(bool unread, string source = "")
    {
        return long.Parse(Query("SELECT count(*) FROM posts WHERE (?='' OR source=?)"
            + (unread ? " AND read_at IS NULL" : ""), source, source)[0][0], CultureInfo.InvariantCulture);
    }

    public long[] SidebarArticleCounts(string source, int group, DateTimeOffset? recentAsOf = null)
    {
        // Count the complete archive for this row, independently of the reading view's filters.
        // Marked can overlap either read state; counting posts directly keeps the global total unique.
        var sql = "SELECT COUNT(CASE WHEN marked=1 THEN 1 END), COUNT(read_at), COUNT(*)";
        var args = new List<string>();
        // Only the tooltip needs the rolling count, based on publication time rather than when we saved the post.
        if (recentAsOf.HasValue)
        {
            sql += ", COUNT(CASE WHEN published>=? AND published<=? THEN 1 END)";
            args.Add(Stamp(recentAsOf.Value.AddHours(-24)));
            args.Add(Stamp(recentAsOf.Value));
        }
        sql += " FROM posts";
        if (source != "") { sql += " WHERE source=?"; args.Add(source); }
        else if (group != 0)
        {
            sql += " WHERE source IN (SELECT source FROM feed_group_members WHERE group_id=?)";
            args.Add(group.ToString(CultureInfo.InvariantCulture));
        }
        var result = Query(sql, args.ToArray());
        return result[0].Select(value => long.Parse(value, CultureInfo.InvariantCulture)).ToArray();
    }

    // Measure the main SQLite file on disk, independently of the temporary WAL file.
    public long DatabaseFileBytes { get { return new FileInfo(databasePath).Length; } }

    public List<string[]> Items(bool unread, string source = "", string before = "", string beforeId = "", int limit = 101, bool oldest = false, int group = 0,
        ArticleFilter dates = null, bool inclusive = false, long since = -1, long through = 0, string edge = "", string edgeId = "")
    {
        // Page from the last displayed row rather than an offset that shifts as rows are marked read.
        dates = dates ?? ArticleFilter.All;
        var comparison = oldest ? ">" : "<";
        var order = oldest ? " ASC" : " DESC";
        var groupId = group.ToString(CultureInfo.InvariantCulture);
        var sql = "SELECT id,source,title,original_url,published,read_at,author,body,image,CASE WHEN source LIKE 'rss\\_%' ESCAPE '\\' THEN CASE WHEN COALESCE(content,'')<>'' THEN '1' ELSE '' END ELSE content END,arrival,discussion,marked FROM posts WHERE (?='' OR source=?)"
            + " AND (?='0' OR source IN (SELECT source FROM feed_group_members WHERE group_id=?))"
             + StateFilter(dates.Flags < 0 ? (unread ? ArticleFilter.Unread | ArticleFilter.Unmarked | ArticleFilter.Marked : ArticleFilter.Everything) : dates.Flags) + PublicationFilter + SearchFilter;
        var args = new List<string> { source, source, groupId, groupId, dates.StartUtc, dates.StartUtc, dates.EndUtc, dates.EndUtc,
            dates.Search, dates.Search, dates.Search };
        if (since >= 0)
        {
            // Only merge into the loaded prefix. Later articles are picked up by normal pagination.
            var edgeComparison = oldest ? "<" : ">";
            sql += " AND arrival>CAST(? AS INTEGER) AND arrival<=CAST(? AS INTEGER)";
            args.AddRange(new[] { since.ToString(CultureInfo.InvariantCulture), through.ToString(CultureInfo.InvariantCulture) });
            if (edge != "")
            {
                sql += " AND (published " + edgeComparison + " ? OR (published=? AND id " + edgeComparison + "=?))";
                args.AddRange(new[] { edge, edge, edgeId });
            }
            sql += " ORDER BY arrival";
        }
        else
        {
            sql += " AND (?='' OR published " + comparison + " ? OR (published=? AND id " + comparison + (inclusive ? "=" : "") + " ?))"
                + " ORDER BY published" + order + ",id" + order;
            args.AddRange(new[] { before, before, before, beforeId });
        }
        args.Add(limit.ToString(CultureInfo.InvariantCulture));
        return Query(sql + " LIMIT ?", args.ToArray());
    }

    public string PublishedAt(string id)
    {
        var rows = Query("SELECT published FROM posts WHERE id=?", id);
        return rows.Count == 0 ? "" : rows[0][0];
    }

    public bool SetRead(string id, bool read)
    {
        lock (gate)
        {
            Run("UPDATE posts SET read_at=?,bulk_mark=NULL WHERE id=?" + (read ? " AND marked=0" : ""), read ? Stamp(DateTimeOffset.UtcNow) : null, id);
            // A stale tab cannot restore a post that has already been removed.
            return Query("SELECT changes()")[0][0] == "1";
        }
    }

    public string[] UpdateMark(string id, bool opened, bool toggle = false)
    {
        lock (gate)
        {
            // Opening an unmarked item must not change its existing read state.
            if (opened) Run("UPDATE posts SET marked=0,read_at=?,bulk_mark=NULL WHERE id=? AND marked=1", Stamp(DateTimeOffset.UtcNow), id);
            // Use the persisted mark, so another tab cannot turn a toggle into the wrong action.
            else if (toggle) Run("UPDATE posts SET marked=1-marked,read_at=CASE WHEN marked=1 THEN ? ELSE NULL END,bulk_mark=NULL WHERE id=?",
                Stamp(DateTimeOffset.UtcNow), id);
            else Run("UPDATE posts SET marked=1,read_at=NULL,bulk_mark=NULL WHERE id=?", id);
            return Query("SELECT marked,read_at FROM posts WHERE id=?", id).SingleOrDefault();
        }
    }

    public long MarkFeedRead(string source, ArticleFilter dates = null, string batch = null, long through = long.MaxValue)
    {
        // Mark all dates shown by the current filter, including posts outside the rendered page.
        dates = dates ?? ArticleFilter.All;
        lock (gate)
        {
            if (dates.Flags >= 0 && (dates.Flags & ArticleFilter.Inbox) != ArticleFilter.Inbox) return 0;
            Run("UPDATE posts SET read_at=?,bulk_mark=? WHERE source=? AND marked=0 AND read_at IS NULL AND arrival<=CAST(? AS INTEGER)" + PublicationFilter + SearchFilter,
                Stamp(DateTimeOffset.UtcNow), batch, source, through.ToString(CultureInfo.InvariantCulture), dates.StartUtc, dates.StartUtc, dates.EndUtc, dates.EndUtc,
                dates.Search, dates.Search, dates.Search);
            return long.Parse(Query("SELECT changes()")[0][0], CultureInfo.InvariantCulture);
        }
    }

    public long UndoFeedRead(string source, string batch)
    {
        lock (gate)
        {
            // A later manual read action or deletion takes priority over an old undo receipt.
            Run("UPDATE posts SET read_at=NULL,bulk_mark=NULL WHERE source=? AND bulk_mark=?", source, batch);
            return long.Parse(Query("SELECT changes()")[0][0], CultureInfo.InvariantCulture);
        }
    }

    public bool CanUndoFeedRead(string source, string batch)
    {
        return Query("SELECT 1 FROM posts WHERE source=? AND bulk_mark=? LIMIT 1", source, batch).Count != 0;
    }

    public long ReadCount(string cutoff)
    {
        return long.Parse(Query("SELECT count(*) FROM posts WHERE marked=0 AND read_at <= ?", cutoff)[0][0], CultureInfo.InvariantCulture);
    }

    public Tuple<long, bool> DeleteReadAndCompact(string cutoff)
    {
        lock (gate)
        {
            long deleted = 0;
            Transaction(() =>
            {
                // Commit the deletion and deduplication IDs together. Unread rows are outside both statements.
                deleted = ReadCount(cutoff);
                Run("INSERT OR IGNORE INTO deleted_posts(id) SELECT id FROM posts WHERE marked=0 AND read_at <= ?", cutoff);
                Run("DELETE FROM posts WHERE marked=0 AND read_at <= ?", cutoff);
            });
            if (deleted == 0) return Tuple.Create(0L, true);

            try
            {
                // VACUUM runs outside the transaction. A separate reader can delay the WAL truncation.
                Run("VACUUM");
                var checkpoint = Query("PRAGMA wal_checkpoint(TRUNCATE)")[0];
                return Tuple.Create(deleted, checkpoint[0] == "0");
            }
            catch (InvalidOperationException) { return Tuple.Create(deleted, false); }
        }
    }

    private void Transaction(Action write)
    {
        lock (gate)
        {
            Run("BEGIN IMMEDIATE");
            try { write(); Run("COMMIT"); }
            catch { Run("ROLLBACK"); throw; }
        }
    }

    private void Run(string sql, params string[] args) { Query(sql, args); }

    private List<string[]> Query(string sql, params string[] args)
    {
        lock (gate)
        {
            IntPtr stmt;
            Check(sqlite3_prepare_v2(db, Bytes(sql), -1, out stmt, IntPtr.Zero));
            try
            {
                // Bind values without putting user or Reddit text into SQL.
                for (int i = 0; i < args.Length; i++)
                    Check(args[i] == null ? sqlite3_bind_null(stmt, i + 1)
                        : sqlite3_bind_text(stmt, i + 1, Bytes(args[i]), -1, new IntPtr(-1)));
                var rows = new List<string[]>();
                int code;
                while ((code = sqlite3_step(stmt)) == 100)
                {
                    var row = new string[sqlite3_column_count(stmt)];
                    for (int i = 0; i < row.Length; i++)
                        row[i] = Text(sqlite3_column_text(stmt, i), sqlite3_column_bytes(stmt, i));
                    rows.Add(row);
                }
                if (code != 101) Check(code);
                return rows;
            }
            finally { sqlite3_finalize(stmt); }
        }
    }

    private static byte[] Bytes(string s) { return Encoding.UTF8.GetBytes(s + '\0'); }

    private static string Text(IntPtr p, int length)
    {
        if (p == IntPtr.Zero) return null;
        var bytes = new byte[length];
        Marshal.Copy(p, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    private void Check(int result)
    {
        if (result != 0) throw new InvalidOperationException("SQLite " + result + ": " + Marshal.PtrToStringAnsi(sqlite3_errmsg(db)));
    }

    public void Dispose()
    {
        lock (gate) { if (db != IntPtr.Zero) { sqlite3_close(db); db = IntPtr.Zero; } }
    }

    private static void ContainsText(IntPtr context, int argc, IntPtr argv)
    {
        // SQLite's built-in case folding is ASCII-only. Match literal Unicode text without treating % or _ as syntax.
        try
        {
            var text = Marshal.ReadIntPtr(argv);
            var term = Marshal.ReadIntPtr(argv, IntPtr.Size);
            var value = Text(sqlite3_value_text(text), sqlite3_value_bytes(text)) ?? "";
            var query = Text(sqlite3_value_text(term), sqlite3_value_bytes(term)) ?? "";
            sqlite3_result_int(context, value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0);
        }
        catch { sqlite3_result_int(context, 0); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SqlFunction(IntPtr context, int argc, IntPtr argv);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_create_function_v2(IntPtr db, byte[] name, int argc, int encoding, IntPtr app, SqlFunction function, IntPtr step, IntPtr final, IntPtr destroy);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_value_text(IntPtr value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_value_bytes(IntPtr value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_result_int(IntPtr context, int value);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] text, int length, IntPtr destructor);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int index);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr stmt, int index);
}
