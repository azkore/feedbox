using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal sealed class App : Form
{
    private readonly Store store;
    private readonly Collector collector;
    private readonly RssCollector rss = new RssCollector();
    private readonly Inbox inbox;
    private readonly string profile;
    private readonly WebView2 view = new WebView2 { Dock = DockStyle.Fill };
    private readonly TextBox address = new TextBox { Dock = DockStyle.Top, ReadOnly = true };
    private readonly NotifyIcon tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "feedbox" };
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 2 * 60 * 1000 };
    private CoreWebView2Environment environment;
    private bool running, ready, needsSignIn, exiting;
    private Task<string> rssCheck;
    private Source rssFeed;
    private bool checkingAllRss, checkingAllReddit;
    private volatile FeedCheckStatus allCheckStatus = new FeedCheckStatus(false, "");

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Keep the same mutex so old and new builds cannot collect or open the profile concurrently.
        using (var mutex = new Mutex(false, "Local\\RedditInbox"))
        {
            bool owned;
            try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) { MessageBox.Show("feedbox is already running."); return; }
            try
            {
                // The legacy compiler targets .NET Framework defaults that include obsolete TLS protocols.
                // Let Windows choose TLS before the RSS HTTP client is initialized.
                AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
                AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false);
                var config = AppConfig.Load(AppDomain.CurrentDomain.BaseDirectory, args);
                using (var store = new Store(config.Database))
                using (var window = new App(store, config.Profile, config.Port)) Application.Run(window);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "feedbox could not start"); }
            finally { mutex.ReleaseMutex(); }
        }
    }

    private App(Store store, string profile, int port)
    {
        this.store = store;
        this.profile = profile;
        collector = new Collector(store);
        // Start on the WebView thread, but give the browser the task for this particular check.
        inbox = new Inbox(store, port, feed => (Task<string>)Invoke(new Func<Task<string>>(() => CheckRequested(feed))),
            (includeRss, includeReddit) => (bool)Invoke(new Func<bool>(() => StartAllChecks(includeRss, includeReddit))), () => allCheckStatus);
        Text = "feedbox - Reddit sign-in";
        ClientSize = new Size(1060, 800);
        ShowInTaskbar = false;

        // The window is used for interactive sign-in. Ordinary reviews happen in the user's browser.
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36 };
        var hide = new Button { AutoSize = true, Text = "Hide and check" };
        hide.Click += async (s, e) => { Hide(); await Check(true); };
        bar.Controls.Add(hide);
        Controls.Add(view);
        Controls.Add(address);
        Controls.Add(bar);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open inbox", null, (s, e) => Process.Start(inbox.Url));
        menu.Items.Add("Open Reddit to sign in", null, (s, e) => SignIn());
        menu.Items.Add("Check now", null, async (s, e) => await CheckRequested(null));
        menu.Items.Add("Exit", null, (s, e) => { exiting = true; Close(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (s, e) => Process.Start(inbox.Url);
        tray.Visible = true;
        timer.Tick += async (s, e) => await Task.WhenAll(Check(false), CheckRss());

        Shown += async (s, e) =>
        {
            // Keep a native window handle for WebView2, but make no window visible during collection.
            Hide();
            // RSS does not depend on a WebView or a Reddit session.
            timer.Start();
            try
            {
                environment = await CoreWebView2Environment.CreateAsync(null, profile);
                await view.EnsureCoreWebView2Async(environment);
                SetUp(view, address);
                ready = true;
                await Task.WhenAll(Check(false), CheckRss());
            }
            catch (Exception ex) { tray.ShowBalloonTip(6000, "feedbox", "WebView2 could not start: " + ex.GetType().Name, ToolTipIcon.Error); }
        };
        FormClosing += (s, e) =>
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        FormClosed += (s, e) => { timer.Dispose(); rss.Dispose(); tray.Dispose(); inbox.Dispose(); };
    }

    private void SignIn()
    {
        // Never move the collector away from its current request or open login without user action.
        if (!ready || running) { tray.ShowBalloonTip(5000, "feedbox", "A check is running or WebView2 is starting.", ToolTipIcon.Info); return; }
        Show();
        Activate();
        view.CoreWebView2.Navigate("https://www.reddit.com/login/");
    }

    private void SetUp(WebView2 control, TextBox location)
    {
        var core = control.CoreWebView2;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.SourceChanged += (s, e) =>
        {
            // Display the origin and path but keep sign-in parameters and fragments out of the app UI.
            Uri url;
            location.Text = Uri.TryCreate(core.Source, UriKind.Absolute, out url) ? url.GetLeftPart(UriPartial.Path) : "";
        };
        core.NewWindowRequested += async (s, e) =>
        {
            var deferral = e.GetDeferral();
            Form popup = null;
            try
            {
                // Block incidental popups during collection. A sign-in popup keeps the original opener and profile.
                if (!Visible) { e.Handled = true; return; }
                popup = new Form { Text = "Reddit sign-in", ClientSize = new Size(720, 760) };
                var child = new WebView2 { Dock = DockStyle.Fill };
                var childAddress = new TextBox { ReadOnly = true, Dock = DockStyle.Top };
                popup.Controls.Add(child);
                popup.Controls.Add(childAddress);
                popup.Show(this);
                await child.EnsureCoreWebView2Async(environment);
                SetUp(child, childAddress);
                child.CoreWebView2.WindowCloseRequested += (a, b) => popup.Close();
                e.NewWindow = child.CoreWebView2;
                e.Handled = true;
            }
            catch (Exception) { e.Handled = true; if (popup != null) popup.Close(); }
            finally { deferral.Complete(); }
        };
    }

    private async Task<bool> EnsureRedditPage()
    {
        var core = view.CoreWebView2;
        if (core.IsSuspended) core.Resume();
        Uri url;
        if (Uri.TryCreate(core.Source, UriKind.Absolute, out url) && url.Scheme == "https" && url.Host == "www.reddit.com" && !url.AbsolutePath.StartsWith("/login"))
            return true;
        // An ordinary Reddit document supplies the origin for the collector's HTML requests.
        var nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs> completed = (s, e) => nav.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += completed;
        try
        {
            core.Navigate("https://www.reddit.com/");
            return await Task.WhenAny(nav.Task, Task.Delay(15000)) == nav.Task && await nav.Task;
        }
        finally { core.NavigationCompleted -= completed; }
    }

    private async Task<string> CheckRequested(string feed)
    {
        if (feed == null)
        {
            var results = await Task.WhenAll(Check(true), CheckRss());
            return string.Join(" ", results.Where(result => !string.IsNullOrEmpty(result)));
        }
        return store.RssToCheck(feed) != null ? await CheckRss(feed) : await Check(true, feed);
    }

    private sealed class CheckProgress
    {
        public readonly string Type;
        public readonly string[] Feeds;
        public int Checked, Failed, Skipped;
        public string Detail;

        public CheckProgress(string type, string[] feeds) { Type = type; Feeds = feeds; }

        public string Describe()
        {
            return Type + ": " + Checked + "/" + Feeds.Length + " checked" + (Failed == 0 ? "" : ", " + Failed + " failed")
                + (Skipped == 0 ? "" : ", " + Skipped + " skipped")
                + (Detail == null ? "." : ". " + Detail);
        }
    }

    private bool StartAllChecks(bool includeRss, bool includeReddit)
    {
        if (exiting || allCheckStatus.Running || store.IsPaused) return false;
        // Check enabled subscriptions regardless of their next scheduled check time.
        var paused = store.PausedFeeds();
        var rssBatch = includeRss ? new CheckProgress("RSS", store.RssSubscriptions().Select(row => row[0]).Where(name => !paused.Contains(name)).ToArray()) : null;
        var redditBatch = includeReddit ? new CheckProgress("Reddit", store.Sources().Select(source => source.Name).Where(name => !paused.Contains(name)).ToArray()) : null;
        checkingAllRss = rssBatch != null;
        checkingAllReddit = redditBatch != null;
        RunAllChecks(rssBatch, redditBatch);
        return true;
    }

    private async void RunAllChecks(CheckProgress rssBatch, CheckProgress redditBatch)
    {
        var batches = new[] { rssBatch, redditBatch }.Where(batch => batch != null).ToArray();
        Func<string> describe = () => string.Join(" ", batches.Select(batch => batch.Describe()));
        Action report = () => allCheckStatus = new FeedCheckStatus(true, describe());
        report();
        try
        {
            // Return the HTTP acknowledgement before starting checks; Reddit and RSS can progress independently.
            await Task.Yield();
            await Task.WhenAll(RunFeedBatch(rssBatch, false, report), RunFeedBatch(redditBatch, true, report));
            var stopped = !exiting && store.IsPaused && batches.Any(batch => batch.Checked + batch.Skipped < batch.Feeds.Length);
            allCheckStatus = new FeedCheckStatus(false, (stopped ? "Collection paused. " : "") + describe());
        }
        catch (Exception)
        {
            allCheckStatus = new FeedCheckStatus(false, "Could not complete the feed check. " + describe());
        }
        finally { checkingAllRss = checkingAllReddit = false; }
    }

    private async Task RunFeedBatch(CheckProgress batch, bool reddit, Action report)
    {
        if (batch == null) return;
        try
        {
            foreach (var feed in batch.Feeds)
            {
                if (exiting || store.IsPaused) return;
                if (!store.HasSource(feed)) { batch.Skipped++; report(); continue; }
                if (reddit)
                {
                    // Wait for a request slot without bypassing the shared budget or a rate-limit cooldown.
                    while (true)
                    {
                        if (exiting || store.IsPaused) return;
                        if (store.IsFeedPaused(feed)) break;
                        if (!ready || Visible || needsSignIn)
                        {
                            batch.Detail = Visible ? "Close the Reddit sign-in window before checking." : "The Reddit collector is not ready.";
                            return;
                        }
                        var limit = store.RedditLimit(DateTimeOffset.UtcNow, false);
                        if (!running && limit == null) break;
                        batch.Detail = limit ?? "Waiting for the current Reddit check.";
                        report();
                        await Task.Delay(TimeSpan.FromSeconds(10));
                    }
                }
                batch.Detail = null;
                var outcome = reddit ? await Check(true, feed, batch: true) : await CheckRss(feed, batch: true);
                if (outcome == "Check paused.") batch.Skipped++;
                else
                {
                    batch.Checked++;
                    if (outcome != "No new posts found." && !outcome.StartsWith("Added ", StringComparison.Ordinal)) batch.Failed++;
                }
                report();
            }
        }
        finally
        {
            // Let the normal schedule resume for each type as soon as its own batch ends.
            if (reddit) checkingAllReddit = false;
            else checkingAllRss = false;
            report();
        }
    }

    private async Task<string> CheckRss(string name = null, bool batch = false)
    {
        if (exiting || (name == null && (store.IsPaused || checkingAllRss))) return "";
        // A bulk check must respect pauses even after waiting for another RSS request.
        if (batch && (store.IsPaused || store.IsFeedPaused(name))) return "Check paused.";
        if (rssCheck != null && !rssCheck.IsCompleted)
        {
            // A just-added feed can be picked up by the timer before its manual check starts.
            // Share that result instead of showing an empty inbox or fetching the same feed twice.
            if (name == null) return "";
            var requested = name == rssFeed.Name ? store.RssToCheck(name) : null;
            // A manual check after an edit must use the new URL rather than reuse an older result.
            bool sameFeed = requested != null && requested.FeedUrl == rssFeed.FeedUrl;
            var result = await rssCheck;
            return sameFeed ? result : await CheckRss(name, batch);
        }
        var source = store.RssToCheck(name);
        if (source == null) return "";
        rssFeed = source;
        return await (rssCheck = RunRssCheck(source));
    }

    private async Task<string> RunRssCheck(Source source)
    {
        try { return await rss.Check(store, source); }
        catch (Exception)
        {
            const string problem = "Could not complete the RSS check.";
            if (!exiting) store.NoteRss(source, problem);
            return problem;
        }
    }

    private async Task<string> Check(bool manual, string feed = null, bool batch = false)
    {
        // Only an explicit single-feed request can run while scheduled collection is paused.
        bool single = manual && feed != null;
        bool overridePause = single && !batch;
        if (batch && (store.IsPaused || store.IsFeedPaused(feed))) return "Check paused.";
        if (!single && checkingAllReddit) return "A check of all Reddit feeds is already running.";
        if (!ready || running || Visible || exiting || (!single && store.IsPaused) || (needsSignIn && !manual))
        {
            return running ? "Another check is already running." : Visible ? "Close the Reddit sign-in window before checking." : "The collector is not ready to check.";
        }
        // Rotate through large subscription lists rather than opening every source in one run.
        var sources = single ? store.Sources().Where(s => s.Name == feed).ToList() : store.Sources(1, true);
        if (sources.Count == 0) return "No eligible feed to check.";
        // Check the shared limit before even opening Reddit's origin document.
        var limited = store.RedditLimit(DateTimeOffset.UtcNow, false);
        if (limited != null) { if (manual) store.Note(sources[0].Name, limited); return limited; }
        running = true;
        try
        {
            // Limit concurrency and pause the whole run on an account-wide block or rate limit.
            if (!await EnsureRedditPage())
            {
                foreach (var source in sources) store.Note(source.Name, "Could not open Reddit. No catch-up point changed.");
                return "Could not open Reddit. No catch-up point changed.";
            }
            foreach (var source in sources)
            {
                if (exiting) break;
                if (!overridePause && (store.IsPaused || store.IsFeedPaused(source.Name))) return "Check paused.";
                store.RecordAttempt(source.Name);
                var result = await collector.Check(view.CoreWebView2, source, overridePause);
                if (result.auth)
                {
                    foreach (var pending in sources.SkipWhile(s => s != source).Skip(1)) store.Note(pending.Name, result.problem);
                    tray.ShowBalloonTip(6000, "feedbox", "Reddit needs attention. Open Reddit from the tray.", ToolTipIcon.Info);
                    needsSignIn = true;
                    store.SetPaused(true);
                    break;
                }
                needsSignIn = false;
                if (result.retry > 0)
                {
                    var nextAllowedCheck = DateTimeOffset.UtcNow.AddSeconds(result.retry);
                    store.SetRedditCooldown(nextAllowedCheck);
                    store.Note(source.Name, Collector.PostCount(result.Added, false) + " Reddit rate-limited the check. Checks can resume after " + nextAllowedCheck.ToLocalTime().ToString("g") + ".");
                    break;
                }
                if (source != sources.Last()) await Task.Delay(TimeSpan.FromSeconds(3));
            }
            var checkedSource = store.Sources().FirstOrDefault(s => s.Name == sources[0].Name);
            return checkedSource == null ? "The feed was deleted." : checkedSource.Status;
        }
        catch (Exception ex)
        {
            // Leave all stored cursors intact after an unexpected local failure.
            tray.ShowBalloonTip(6000, "feedbox", "Collection failed: " + ex.GetType().Name, ToolTipIcon.Warning);
            return "Collection failed: " + ex.GetType().Name;
        }
        finally { running = false; }
    }
}
