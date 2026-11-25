using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal sealed class RssBrowser : IDisposable
{
    private readonly string profile;
    private Task<CoreWebView2Environment> environment;
    private Form active;

    public RssBrowser(string profile) { this.profile = profile; }

    public async Task<byte[]> Fetch(Uri uri, bool visible)
    {
        // Keep this browser on the publisher's HTTPS origin, separate from the Reddit sign-in profile.
        if (!await IsPublicOrigin(uri)) throw new InvalidDataException("The feed browser requires a public HTTPS feed URL.");
        var alias = await PublicWwwAlias(uri);
        if (active != null) throw new InvalidDataException("A feed is already open in the feedbox browser.");

        var complete = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool challenged = false;
        using (var window = new Form { Text = "feedbox - " + uri.IdnHost, Width = 900, Height = 680,
            ShowInTaskbar = visible, Opacity = visible ? 1 : 0 })
        using (var web = new WebView2 { Dock = DockStyle.Fill })
        {
            active = window;
            window.Controls.Add(web);
            window.FormClosed += (s, e) => complete.TrySetException(new InvalidDataException(RssCollector.NeedsVisit));
            window.Shown += async (s, e) =>
            {
                if (!visible) window.Hide();
                try
                {
                    if (environment == null || environment.IsFaulted) environment = CoreWebView2Environment.CreateAsync(null, profile);
                    var env = await environment;
                    if (window.IsDisposed) return;
                    await web.EnsureCoreWebView2Async(env);
                    if (window.IsDisposed) return;
                    var core = web.CoreWebView2;
                    core.Settings.IsPasswordAutosaveEnabled = false;
                    core.Settings.IsGeneralAutofillEnabled = false;
                    core.Settings.IsWebMessageEnabled = false;
                    core.Settings.AreHostObjectsAllowed = false;
                    core.NewWindowRequested += (sender, args) => args.Handled = true;
                    core.DownloadStarting += (sender, args) => args.Cancel = true;
                    core.PermissionRequested += (sender, args) => args.State = CoreWebView2PermissionState.Deny;

                    // Some publishers canonicalize www to the bare host while verifying a visit.
                    core.NavigationStarting += (sender, args) => args.Cancel = !IsPublisherUri(uri, alias, args.Uri);
                    core.FrameNavigationStarting += (sender, args) => args.Cancel = !AllowedResource(uri, alias, args.Uri);
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                    core.WebResourceRequested += (sender, args) =>
                    {
                        if (!AllowedResource(uri, alias, args.Request.Uri))
                            args.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
                    };

                    // The page may navigate again on its own. Capture only XML returned for the subscribed URL.
                    core.WebResourceResponseReceived += async (sender, args) =>
                    {
                        Uri received;
                        if (complete.Task.IsCompleted || !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out received)
                            || !IsPublisherUri(uri, alias, args.Request.Uri) || received.PathAndQuery != uri.PathAndQuery) return;
                        var response = args.Response;
                        if (response.Headers.Contains("cf-mitigated") && RssCollector.IsChallenge(response.Headers.GetHeader("cf-mitigated")))
                        { challenged = true; return; }
                        if (response.StatusCode != 200 || !response.Headers.Contains("Content-Type")
                            || !IsXml(response.Headers.GetHeader("Content-Type"))) return;
                        try
                        {
                            using (var content = await response.GetContentAsync())
                            using (var data = new MemoryStream())
                            {
                                if (content == null) return;
                                var buffer = new byte[8192];
                                int count;
                                while ((count = await content.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    if (data.Length + count > RssCollector.MaxBytes) throw new InvalidDataException("The RSS response is too large.");
                                    data.Write(buffer, 0, count);
                                }
                                if (!window.IsDisposed) complete.TrySetResult(data.ToArray());
                            }
                        }
                        catch (Exception error) { if (!window.IsDisposed) complete.TrySetException(error); }
                    };
                    if (visible) AddVisitNavigation(window, core, uri, alias);
                    core.Navigate(uri.AbsoluteUri);
                }
                catch (Exception error) { if (!window.IsDisposed) complete.TrySetException(error); }
            };
            try
            {
                window.Show();
                if (await Task.WhenAny(complete.Task, Task.Delay(visible ? 180000 : 12000)) == complete.Task)
                    return await complete.Task;
                throw new InvalidDataException(challenged || visible ? RssCollector.NeedsVisit : "The feed browser did not receive RSS or Atom XML.");
            }
            finally { active = null; }
        }
    }

    private static void AddVisitNavigation(Form window, CoreWebView2 core, Uri feed, Uri alias)
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 36, ColumnCount = 3, RowCount = 1 };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var address = new TextBox { Text = feed.AbsoluteUri, Anchor = AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Site address" };
        var go = new Button { Text = "Go", AutoSize = true };
        var back = new Button { Text = "Back to feed", AutoSize = true };
        bar.Controls.Add(address, 0, 0);
        bar.Controls.Add(go, 1, 0);
        bar.Controls.Add(back, 2, 0);
        window.Controls.Add(bar);

        // Manual navigation shares the verification session but cannot change the feed being collected.
        Action navigate = () =>
        {
            Uri target;
            if (!Uri.TryCreate(feed, address.Text.Trim(), out target) || !IsPublisherUri(feed, alias, target.AbsoluteUri))
            {
                MessageBox.Show(window, "Enter a page on " + feed.GetLeftPart(UriPartial.Authority) + ".", "feedbox");
                return;
            }
            core.Navigate(target.AbsoluteUri);
        };
        go.Click += (s, e) => navigate();
        address.KeyDown += (s, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            navigate();
        };
        back.Click += (s, e) => core.Navigate(feed.AbsoluteUri);
        core.SourceChanged += (s, e) => { if (IsPublisherUri(feed, alias, core.Source)) address.Text = core.Source; };
    }

    internal static bool IsXml(string contentType)
    {
        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return type == "text/xml" || type == "application/xml" || type.EndsWith("+xml", StringComparison.Ordinal) && type != "application/xhtml+xml";
    }

    private static bool SameOrigin(Uri origin, string url)
    {
        Uri target;
        return Uri.TryCreate(url, UriKind.Absolute, out target) && target.Scheme == Uri.UriSchemeHttps
            && target.UserInfo == "" && target.IsDefaultPort && target.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPublisherUri(Uri origin, Uri alias, string url)
    {
        return SameOrigin(origin, url) || alias != null && SameOrigin(alias, url);
    }

    private static bool AllowedResource(Uri origin, Uri alias, string url)
    {
        // Turnstile uses local blank and inline frames; neither loads a separate network origin.
        return url == "about:blank" || url == "about:srcdoc"
            || IsPublisherUri(origin, alias, url) || SameOrigin(new Uri("https://challenges.cloudflare.com/"), url);
    }

    private static async Task<Uri> PublicWwwAlias(Uri origin)
    {
        // Limit the exception to a single exact hostname and validate its DNS independently.
        var host = origin.IdnHost;
        var alternate = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host.Substring(4) : "www." + host;
        var alias = new UriBuilder(origin) { Host = alternate }.Uri;
        return await IsPublicOrigin(alias) ? alias : null;
    }

    private static async Task<bool> IsPublicOrigin(Uri uri)
    {
        if (!SameOrigin(uri, uri.AbsoluteUri) || uri.HostNameType != UriHostNameType.Dns || !uri.IdnHost.Contains(".")) return false;
        var dns = Dns.GetHostAddressesAsync(uri.IdnHost);
        if (await Task.WhenAny(dns, Task.Delay(2000)) != dns) return false;
        try
        {
            var ips = await dns;
            return ips.Length != 0 && ips.All(PublisherPreview.IsPublic);
        }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

    public void Dispose() { if (active != null) active.Close(); }
}
