using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

internal sealed class RssCollector : IDisposable
{
    internal const int MaxBytes = 4 * 1024 * 1024;
    internal const int MaxArticleHtml = 100000;
    internal const string NeedsVisit = "The feed needs a visit in the feedbox browser.";
    private const string Changed = "The feed source changed during the check.";
    private readonly Dictionary<string, Uri> browserUrls = new Dictionary<string, Uri>();
    private readonly HttpClient client = new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        MaxAutomaticRedirections = 5,
        UseCookies = false
    });

    public RssCollector()
    {
        // Buffered HTTP reads apply both limits before the XML parser sees the response.
        client.Timeout = TimeSpan.FromSeconds(25);
        client.MaxResponseContentBufferSize = MaxBytes;
        // Use the standard Windows Chrome user-agent for direct feed requests.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/157.0.0.0 Safari/537.36");
    }

    public static bool TryFeedUrl(string value, out Uri url)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out url) && IsHttp(url)
            && url.Fragment == "" && url.AbsoluteUri.Length <= 2000;
    }

    private static bool IsHttp(Uri url)
    {
        return (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && url.UserInfo == "";
    }

    internal static bool IsChallenge(string header)
    {
        return string.Equals(header, "challenge", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> Check(Store store, Source source, Func<Uri, bool, Task<byte[]>> browser = null, bool visit = false)
    {
        if (!store.RecordRssAttempt(source)) return Changed;
        string status;
        try
        {
            Uri browserUrl;
            if (browser != null && browserUrls.TryGetValue(source.FeedUrl, out browserUrl))
                status = await CheckInBrowser(store, source, browser, browserUrl, visit);
            else if (browser != null && visit)
                status = await CheckInBrowser(store, source, browser, new Uri(source.FeedUrl), true);
            else
            {
                // Retry only a confirmed challenge; an ordinary HTTP error keeps its original meaning.
                Uri challenged = null;
                using (var response = await client.GetAsync(source.FeedUrl))
                {
                    var origin = response.RequestMessage.RequestUri;
                    IEnumerable<string> values;
                    if (response.Headers.TryGetValues("cf-mitigated", out values) && values.Any(IsChallenge)) challenged = origin;
                    if (challenged != null) status = "The site returned a browser challenge instead of RSS.";
                    else if (!response.IsSuccessStatusCode) status = "RSS feed returned HTTP " + (int)response.StatusCode + ".";
                    else using (var data = await response.Content.ReadAsStreamAsync())
                        status = Save(store, source, origin, data);
                }
                if (challenged != null && browser != null) status = await CheckInBrowser(store, source, browser, challenged, false);
            }
        }
        catch (OperationCanceledException) { status = "RSS feed did not answer within 25 seconds."; }
        catch (HttpRequestException) { status = "Could not download the RSS feed."; }
        catch (InvalidDataException ex) { status = ex.Message; }
        catch (IOException) { status = "Could not read the RSS response."; }
        catch (XmlException) { status = "The response is not readable RSS or Atom XML."; }
        return store.NoteRss(source, status) ? status : Changed;
    }

    private async Task<string> CheckInBrowser(Store store, Source source, Func<Uri, bool, Task<byte[]>> browser, Uri uri, bool visit)
    {
        // Keep cookies and verification on HTTPS, including for older HTTP subscriptions.
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsDefaultPort) uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        var bytes = await browser(uri, visit);
        using (var data = new MemoryStream(bytes, false))
        {
            var status = Save(store, source, uri, data);
            // Reuse the proven transport on later checks in this app run.
            if (status != Changed) browserUrls[source.FeedUrl] = uri;
            return status;
        }
    }

    private static string Save(Store store, Source source, Uri origin, Stream data)
    {
        // Both transports use the same parser, article identity, and source version check.
        string title;
        Uri icon, site;
        var posts = Parse(data, origin, source.Name, out title, out icon, out site);
        int added = store.SaveRss(source.Name, title, posts, source.FeedUrl);
        if (added < 0) return Changed;
        store.Icons.Discover(source.Name, icon, site ?? new Uri(origin, "/"), () =>
        {
            var current = store.RssToCheck(source.Name);
            return current != null && current.FeedUrl == source.FeedUrl;
        });
        return added == 0 ? "No new posts found." : "Added " + added + (added == 1 ? " new post." : " new posts.");
    }

    internal static List<Post> Parse(Stream data, Uri origin, string source, out string title)
    {
        Uri icon, site;
        return Parse(data, origin, source, out title, out icon, out site);
    }

    internal static List<Post> Parse(Stream data, Uri origin, string source, out string title, out Uri icon, out Uri site)
    {
        // Feed XML is untrusted. Parse its text without resolving entities or external files.
        XDocument doc;
        using (var reader = XmlReader.Create(data, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes }))
            doc = XDocument.Load(reader);
        var root = doc.Root;
        bool atom = root.Name == XName.Get("feed", "http://www.w3.org/2005/Atom");
        bool rdf = root.Name == XName.Get("RDF", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");
        XNamespace rss1 = "http://purl.org/rss/1.0/";
        XNamespace rss09 = "http://my.netscape.com/rdf/simple/0.9/";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        // RDF-based RSS uses a namespaced channel with items and images beside it.
        var channel = atom ? root : rdf ? root.Element(rss1 + "channel") ?? root.Element(rss09 + "channel")
            : root.Name.LocalName == "rss" ? root.Element("channel") : null;
        if (channel == null) throw new InvalidDataException("The response is not an RSS or Atom feed.");
        var ns = channel.Name.Namespace;
        title = Text(Value(channel, ns + "title"), 120);
        // Feed-level metadata describes the publisher, independently of the individual article links.
        var image = atom ? Value(channel, ns + "icon") : Value((rdf ? root : channel).Element(ns + "image"), ns + "url");
        if (atom && image == "") image = Value(channel, ns + "logo");
        var homepage = atom ? channel.Elements(ns + "link").Where(e => (string)e.Attribute("rel") == null || (string)e.Attribute("rel") == "alternate")
            .Select(e => (string)e.Attribute("href")).FirstOrDefault() : Value(channel, ns + "link");
        icon = MetadataUrl(origin, image);
        site = MetadataUrl(origin, homepage);
        var posts = new Dictionary<string, Post>();
        var now = DateTimeOffset.UtcNow;
        var entries = (rdf ? root : channel).Elements(ns + (atom ? "entry" : "item"));
        foreach (var entry in entries)
        {
            // Atom can advertise several links; use only the human-facing alternate link.
            string link = atom ? entry.Elements(ns + "link")
                .Where(e => (string)e.Attribute("rel") == null || (string)e.Attribute("rel") == "alternate")
                .Select(e => (string)e.Attribute("href")).FirstOrDefault() : Value(entry, ns + "link");
            Uri original;
            if (!Uri.TryCreate(origin, link, out original) || string.IsNullOrWhiteSpace(link) || !IsHttp(original)) continue;
            var externalId = Value(entry, ns + (atom ? "id" : "guid"));
            var id = Store.RssKey(source + "\n" + (externalId == "" ? original.AbsoluteUri : externalId));
            if (posts.ContainsKey(id)) continue;
            // RDF feeds use the Dublin Core date; older feeds may have no date at all.
            var publication = atom ? Value(entry, ns + "published") : rdf ? Value(entry, dc + "date") : Value(entry, ns + "pubDate");
            var published = publication == "" && atom ? Value(entry, ns + "updated") : publication;
            DateTimeOffset created;
            bool hasDate = DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out created);
            if (!hasDate) created = now;
            var heading = Text(Value(entry, ns + "title"), 300);
            var content = atom ? entry.Element(ns + "content") ?? entry.Element(ns + "summary")
                : entry.Element(XName.Get("encoded", "http://purl.org/rss/1.0/modules/content/")) ?? entry.Element(ns + "description");
            var author = atom ? Value(entry.Element(ns + "author"), ns + "name")
                : Value(entry, dc + "creator");
            // Keep the publisher's article HTML separately from the searchable text.
            var payload = content == null ? "" : content.HasElements ? string.Concat(content.Nodes()) : content.Value;
            var contentType = content == null ? "" : (string)content.Attribute("type");
            var html = !atom || contentType == "html" || contentType == "xhtml";
            var comments = atom ? entry.Elements(ns + "link").Where(e => (string)e.Attribute("rel") == "replies"
                    && ((string)e.Attribute("type") ?? "text/html") == "text/html").Select(e => (string)e.Attribute("href")).FirstOrDefault()
                : Value(entry, ns + "comments");
            Uri discussion = null;
            bool hasDiscussion = !string.IsNullOrWhiteSpace(comments) && Uri.TryCreate(origin, comments, out discussion) && IsHttp(discussion);
            posts.Add(id, new Post { id = id, title = heading == "" ? original.AbsoluteUri : heading,
                originalUrl = original.AbsoluteUri, path = "", author = Text(author, 120),
                created = created.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                publicationKnown = hasDate && publication != "" && created <= now,
                body = html ? Text(payload, 12000) : Bounded(payload.Trim(), 12000),
                content = html && payload.Length <= MaxArticleHtml && payload.IndexOf('<') >= 0 ? payload : null,
                discussion = hasDiscussion ? discussion.AbsoluteUri : null });
        }
        if (posts.Count == 0 && entries.Any()) throw new InvalidDataException("The RSS feed has entries but no readable article links.");
        return posts.Values.ToList();
    }

    private static Uri MetadataUrl(Uri origin, string value)
    {
        Uri uri;
        return !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(origin, value.Trim(), out uri) && IsHttp(uri) ? uri : null;
    }

    private static string Bounded(string value, int limit) { return value.Length > limit ? value.Substring(0, limit) : value; }

    private static string Value(XElement element, XName name)
    {
        return element == null ? "" : ((string)element.Element(name) ?? "").Trim();
    }

    private static string Text(string value, int limit)
    {
        // Strip markup for the plain-text article renderer, which escapes text again on output.
        if (value.Length > 120000) value = value.Substring(0, 120000);
        value = Regex.Replace(value, @"(?is)<(script|style)\b[^>]*>.*?</\1\s*>", "");
        value = Regex.Replace(value, @"(?i)<(?:br\b[^>]*|/(?:p|div|li|h[1-6])\s*)>", "\n\n");
        value = WebUtility.HtmlDecode(Regex.Replace(value, "<[^<>]*>", "")).Trim();
        return value.Length > limit ? value.Substring(0, limit) : value;
    }

    public void Dispose() { client.Dispose(); }
}
