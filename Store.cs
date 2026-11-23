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
}

internal sealed class Source
{
    public string FeedUrl;
    public string Name, Label, Anchors, Cursor, Head, Seen, Status;
    public bool ExtraPage;
}

internal sealed class FeedGroup
{
    public int Id;
    public string Name;
    public readonly List<string> Members = new List<string>();
}

internal sealed class PublicationDates
{
    public static readonly PublicationDates All = new PublicationDates("", "", "", "");
    public readonly string From, To, StartUtc, EndUtc;
    public bool Active { get { return From != "" || To != ""; } }

    private PublicationDates(string from, string to, string startUtc, string endUtc)
    {
        From = from;
        To = to;
        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    public static bool TryCreate(string from, string to, out PublicationDates dates)
    {
        const string format = "yyyy-MM-dd";
        dates = All;
        from = from ?? "";
        to = to ?? "";
        DateTime first = DateTime.MinValue, last = DateTime.MaxValue.Date;
        if ((from != "" && !DateTime.TryParseExact(from, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out first))
            || (to != "" && !DateTime.TryParseExact(to, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out last)) || first > last) return false;
        if (from == "" && to == "") return true;

        // Convert each local midnight separately so the range still matches the date headings on DST days.
        // Use a UTC second prefix: saved articles have varying fractional precision but share this prefix.
        const string utcPrefix = "yyyy-MM-dd'T'HH:mm:ss";
        dates = new PublicationDates(from, to,
            from == "" ? "" : first.ToUniversalTime().ToString(utcPrefix, CultureInfo.InvariantCulture),
            to == "" || last == DateTime.MaxValue.Date ? "" : last.AddDays(1).ToUniversalTime().ToString(utcPrefix, CultureInfo.InvariantCulture));
        return true;
    }
}

internal sealed class Store : IDisposable
{
    private const string PublicationFilter = " AND (?='' OR published>=?) AND (?='' OR published<?)";
    private readonly object gate = new object();
    private readonly string databasePath;
    private IntPtr db;

    public Store(string path)
    {
        databasePath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        Check(sqlite3_open_v2(Bytes(path), out db, 6, IntPtr.Zero));
        // Keep the working file readable by other SQLite tools while the inbox runs.
        Check(sqlite3_busy_timeout(db, 5000));
        Run("PRAGMA journal_mode=WAL");
        var version = int.Parse(Query("PRAGMA user_version")[0][0], CultureInfo.InvariantCulture);
        if (version < 0 || version > 11) throw new InvalidOperationException("Unsupported database version.");
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
        });
    }

    public List<Source> Sources(int limit = -1, bool dueOnly = false)
    {
        // Revisit quiet feeds less often; unfinished walks remain eligible at the busy-feed interval.
        var due = dueOnly ? @" AND (last_attempt='' OR julianday(last_attempt) <= julianday('now') -
            CASE WHEN cursor<>'' OR EXISTS (SELECT 1 FROM posts WHERE source=sources.name AND published >= strftime('%Y-%m-%dT%H:%M:%S','now','-1 day'))
            THEN 2.0/24 ELSE 12.0/24 END)" : "";
        return Query("SELECT name,anchors,cursor,head,seen,extra,status,label FROM sources WHERE kind='reddit'" + due + " ORDER BY last_attempt,name LIMIT ?", limit.ToString(CultureInfo.InvariantCulture))
            .Select(r => new Source { Name = r[0], Anchors = r[1], Cursor = r[2], Head = r[3], Seen = r[4], ExtraPage = r[5] == "1", Status = r[6], Label = r[7] }).ToList();
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
        // Keep the subscription key unchanged if the publisher later changes its title.
        var name = RssKey(url.AbsoluteUri);
        Transaction(() =>
        {
            Run("INSERT OR IGNORE INTO sources(name, label, kind, feed_url) VALUES (?,?,'rss',?)", name, url.Host, url.AbsoluteUri);
            if (group > 0) Run(@"INSERT OR IGNORE INTO feed_group_members(group_id,source,position)
                SELECT id,?,COALESCE((SELECT MAX(position)+1 FROM feed_group_members WHERE group_id=feed_groups.id),0)
                FROM feed_groups WHERE id=?", name, group.ToString(CultureInfo.InvariantCulture));
        });
        return name;
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

    public int SaveRss(string source, string title, IEnumerable<Post> posts)
    {
        int added = 0;
        Transaction(() =>
        {
            if (title != "") Run("UPDATE sources SET label=? WHERE name=? AND kind='rss'", title, source);
            added = SavePosts(source, posts);
        });
        return added;
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

    public void AddSource(string name, int group = 0)
    {
        // Preserve the supplied casing for Reddit requests while using the same local subscription key.
        var label = "r/" + name;
        name = name.ToLowerInvariant();
        Transaction(() =>
        {
            InsertSource(name, label);
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
            added = SavePosts(scan.Name, posts);
            // Commit the page and the cursor together so a restart cannot skip unsaved posts.
            if (complete)
                Run("UPDATE sources SET anchors=?,cursor='',head='',seen='',extra=0,status=? WHERE name=?",
                    scan.Head, scan.Status, scan.Name);
            else
                Run("UPDATE sources SET cursor=?,head=?,seen=?,extra=?,status=? WHERE name=?",
                    scan.Cursor, scan.Head, scan.Seen, scan.ExtraPage ? "1" : "0", scan.Status, scan.Name);
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
        int added = 0;
        foreach (var post in posts)
        {
            // Rediscovery can refresh the title and link, never the user's read state.
            Run("INSERT OR IGNORE INTO posts(id,source,title,path,published,first_seen) SELECT ?,?,?,?,?,? WHERE NOT EXISTS (SELECT 1 FROM deleted_posts WHERE id=?)",
                post.id, name, post.title, post.path, post.created, now, post.id);
            // A third-party backfill must not replace content already fetched directly.
            // Count only inserted rows, before the metadata UPDATE replaces SQLite's changes count.
            bool inserted = Query("SELECT changes()")[0][0] == "1";
            if (inserted) added++;
            if (keepExisting && !inserted) continue;
            // A shorter listing card must not erase context captured on an earlier check.
            Run("UPDATE posts SET title=?,path=?,author=COALESCE(NULLIF(?,''),author),body=COALESCE(NULLIF(?,''),body),image=COALESCE(NULLIF(?,''),image),original_url=COALESCE(NULLIF(?,''),original_url),content=COALESCE(NULLIF(?,''),content) WHERE id=?",
                post.title, post.path, post.author, post.body, post.image, post.originalUrl, post.content != null && post.content.Length <= 100000 ? post.content : null, post.id);
        }
        return added;
    }

    public List<string[]> Sidebar()
    {
        // Keep the reading sidebar independent of collection state.
        return Query(@"SELECT s.name,s.status,SUM(CASE WHEN p.id IS NOT NULL AND p.read_at IS NULL THEN 1 ELSE 0 END),COALESCE(NULLIF(s.custom_label,''),s.label),s.coverage_note
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

    public Dictionary<string, long> UnreadCounts(PublicationDates dates = null)
    {
        // Use one SQLite snapshot for all badges, including feeds that appear in multiple groups.
        dates = dates ?? PublicationDates.All;
        return Query("SELECT source,count(*) FROM posts WHERE read_at IS NULL" + PublicationFilter + " GROUP BY source",
            dates.StartUtc, dates.StartUtc, dates.EndUtc, dates.EndUtc)
            .ToDictionary(row => row[0], row => long.Parse(row[1], CultureInfo.InvariantCulture));
    }

    public long Count(bool unread, string source = "")
    {
        return long.Parse(Query("SELECT count(*) FROM posts WHERE (?='' OR source=?)"
            + (unread ? " AND read_at IS NULL" : ""), source, source)[0][0], CultureInfo.InvariantCulture);
    }

    // Measure the main SQLite file on disk, independently of the temporary WAL file.
    public long DatabaseFileBytes { get { return new FileInfo(databasePath).Length; } }

    public List<string[]> Items(bool unread, string source = "", string before = "", string beforeId = "", int limit = 101, bool oldest = false, int group = 0,
        PublicationDates dates = null)
    {
        // Page from the last displayed row rather than an offset that shifts as rows are marked read.
        dates = dates ?? PublicationDates.All;
        var comparison = oldest ? ">" : "<";
        var order = oldest ? " ASC" : " DESC";
        return Query("SELECT id,source,title,original_url,published,read_at,author,body,image,content FROM posts WHERE (?='' OR source=?)"
            + " AND (?='0' OR source IN (SELECT source FROM feed_group_members WHERE group_id=?))"
             + (unread ? " AND read_at IS NULL" : "")
             + PublicationFilter
            + " AND (?='' OR published " + comparison + " ? OR (published=? AND id " + comparison + " ?)) ORDER BY published" + order + ",id" + order + " LIMIT ?",
             source, source, group.ToString(CultureInfo.InvariantCulture), group.ToString(CultureInfo.InvariantCulture), dates.StartUtc, dates.StartUtc, dates.EndUtc, dates.EndUtc,
             before, before, before, beforeId, limit.ToString(CultureInfo.InvariantCulture));
    }

    public bool SetRead(string id, bool read)
    {
        lock (gate)
        {
            Run("UPDATE posts SET read_at=? WHERE id=?", read ? DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) : null, id);
            // A stale tab cannot restore a post that has already been removed.
            return Query("SELECT changes()")[0][0] == "1";
        }
    }

    public long MarkFeedRead(string source, PublicationDates dates = null)
    {
        // Mark all dates shown by the current filter, including posts outside the rendered page.
        dates = dates ?? PublicationDates.All;
        lock (gate)
        {
            Run("UPDATE posts SET read_at=? WHERE source=? AND read_at IS NULL" + PublicationFilter,
                Stamp(DateTimeOffset.UtcNow), source, dates.StartUtc, dates.StartUtc, dates.EndUtc, dates.EndUtc);
            return long.Parse(Query("SELECT changes()")[0][0], CultureInfo.InvariantCulture);
        }
    }

    public long ReadCount(string cutoff)
    {
        return long.Parse(Query("SELECT count(*) FROM posts WHERE read_at <= ?", cutoff)[0][0], CultureInfo.InvariantCulture);
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
                Run("INSERT OR IGNORE INTO deleted_posts(id) SELECT id FROM posts WHERE read_at <= ?", cutoff);
                Run("DELETE FROM posts WHERE read_at <= ?", cutoff);
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
