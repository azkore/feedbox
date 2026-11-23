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
    private const int MaxBytes = 4 * 1024 * 1024;
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FeedInbox/1.0");
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

    public async Task<string> Check(Store store, Source source)
    {
        store.RecordAttempt(source.Name);
        string status;
        try
        {
            using (var response = await client.GetAsync(source.FeedUrl))
            {
                if (!response.IsSuccessStatusCode) status = "RSS feed returned HTTP " + (int)response.StatusCode + ".";
                else
                {
                    using (var data = await response.Content.ReadAsStreamAsync())
                    {
                        string title;
                        var posts = Parse(data, response.RequestMessage.RequestUri, source.Name, out title);
                        int added = store.SaveRss(source.Name, title, posts);
                        status = added == 0 ? "No new posts found." : "Added " + added + (added == 1 ? " new post." : " new posts.");
                    }
                }
            }
        }
        catch (OperationCanceledException) { status = "RSS feed did not answer within 25 seconds."; }
        catch (HttpRequestException) { status = "Could not download the RSS feed."; }
        catch (InvalidDataException ex) { status = ex.Message; }
        catch (IOException) { status = "Could not read the RSS response."; }
        catch (XmlException) { status = "The response is not readable RSS or Atom XML."; }
        store.Note(source.Name, status);
        return status;
    }

    internal static List<Post> Parse(Stream data, Uri origin, string source, out string title)
    {
        // Feed XML is untrusted. Parse its text without resolving entities or external files.
        XDocument doc;
        using (var reader = XmlReader.Create(data, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes }))
            doc = XDocument.Load(reader);
        var root = doc.Root;
        bool atom = root.Name == XName.Get("feed", "http://www.w3.org/2005/Atom");
        var channel = atom ? root : root.Name.LocalName == "rss" ? root.Element("channel") : null;
        if (channel == null) throw new InvalidDataException("The response is not an RSS or Atom feed.");
        var ns = channel.Name.Namespace;
        title = Text(Value(channel, ns + "title"), 120);
        var posts = new Dictionary<string, Post>();
        var now = DateTimeOffset.UtcNow;
        var entries = channel.Elements(ns + (atom ? "entry" : "item"));
        foreach (var entry in entries)
        {
            // Atom can advertise several links; use only the human-facing alternate link.
            string link = atom ? entry.Elements(ns + "link")
                .Where(e => (string)e.Attribute("rel") == null || (string)e.Attribute("rel") == "alternate")
                .Select(e => (string)e.Attribute("href")).FirstOrDefault() : Value(entry, "link");
            Uri original;
            if (!Uri.TryCreate(origin, link, out original) || string.IsNullOrWhiteSpace(link) || !IsHttp(original)) continue;
            var externalId = Value(entry, ns + (atom ? "id" : "guid"));
            var id = Store.RssKey(source + "\n" + (externalId == "" ? original.AbsoluteUri : externalId));
            if (posts.ContainsKey(id)) continue;
            var publication = atom ? Value(entry, ns + "published") : Value(entry, "pubDate");
            var published = publication == "" && atom ? Value(entry, ns + "updated") : publication;
            DateTimeOffset created;
            bool hasDate = DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out created);
            if (!hasDate) created = now;
            var heading = Text(Value(entry, ns + "title"), 300);
            var content = atom ? entry.Element(ns + "content") ?? entry.Element(ns + "summary")
                : entry.Element(XName.Get("encoded", "http://purl.org/rss/1.0/modules/content/")) ?? entry.Element("description");
            var author = atom ? Value(entry.Element(ns + "author"), ns + "name")
                : Value(entry, XName.Get("creator", "http://purl.org/dc/elements/1.1/"));
            posts.Add(id, new Post { id = id, title = heading == "" ? original.AbsoluteUri : heading,
                originalUrl = original.AbsoluteUri, path = "", author = Text(author, 120),
                created = created.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                publicationKnown = hasDate && publication != "" && created <= now,
                body = Text(content == null ? "" : content.HasElements ? string.Concat(content.Nodes()) : content.Value, 12000) });
        }
        if (posts.Count == 0 && entries.Any()) throw new InvalidDataException("The RSS feed has entries but no readable article links.");
        return posts.Values.ToList();
    }

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
