using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal static class PublisherPreview
{
    private const int HeadLimit = 128 * 1024;
    private static readonly SemaphoreSlim Slots = new SemaphoreSlim(3);
    private static readonly Regex MetadataTags = new Regex(@"<!--.*?-->|<(?:script|style)\b[^>]*>.*?</(?:script|style)\s*>|<meta\b(?:[^>""']|""[^""]*""|'[^']*')*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(150));
    private static readonly Regex Attributes = new Regex(@"\s+([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
        RegexOptions.None, TimeSpan.FromMilliseconds(150));

    internal static async Task<Uri> Lookup(string original)
    {
        Uri page;
        if (!Uri.TryCreate(original, UriKind.Absolute, out page) || !await Slots.WaitAsync(1500)) return null;
        try
        {
            // Walk redirects ourselves, validating and pinning the resolved address at every hop.
            for (int hop = 0; hop < 4; hop++)
            {
                var address = await ResolvePublic(page);
                if (address == null) return null;
                var response = await ReadHead(page, address);
                if (response == null) return null;
                if (response.Status >= 300 && response.Status < 400)
                {
                    if (response.Location == null || !Uri.TryCreate(page, response.Location, out page)) return null;
                    continue;
                }
                if (response.Status != 200 || !response.IsHtml) return null;
                var image = FindImage(response.Html, page);
                return image != null && await ResolvePublic(image) != null ? image : null;
            }
        }
        catch (Exception) { /* A preview is optional; publisher and platform failures must leave the card readable. */ }
        finally { Slots.Release(); }
        return null;
    }

    internal static Uri FindImage(string html, Uri page)
    {
        Uri twitter = null;
        foreach (Match tag in MetadataTags.Matches(html))
        {
            if (!tag.Value.StartsWith("<meta", StringComparison.OrdinalIgnoreCase)) continue;
            var attrs = Attributes.Matches(tag.Value).Cast<Match>().ToList();
            Func<string, string> value = key =>
            {
                var attr = attrs.FirstOrDefault(m => string.Equals(m.Groups[1].Value, key, StringComparison.OrdinalIgnoreCase));
                return attr == null ? "" : WebUtility.HtmlDecode(attr.Groups.Cast<Group>().Skip(2).First(g => g.Success).Value).Trim();
            };
            var property = value("property");
            if (property == "") property = value("name");
            var content = value("content");
            Uri candidate;
            if (content == "" || !Uri.TryCreate(page, content, out candidate) || !IsWebUrl(candidate)) continue;
            // Prefer the publisher's Open Graph image and use its Twitter card image as a fallback.
            if (property.Equals("og:image", StringComparison.OrdinalIgnoreCase) || property.Equals("og:image:url", StringComparison.OrdinalIgnoreCase)) return candidate;
            if (twitter == null && (property.Equals("twitter:image", StringComparison.OrdinalIgnoreCase)
                || property.Equals("twitter:image:src", StringComparison.OrdinalIgnoreCase))) twitter = candidate;
        }
        return twitter;
    }

    private static bool IsWebUrl(Uri uri)
    {
        return (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.IsDefaultPort && uri.UserInfo == ""
            && (uri.HostNameType == UriHostNameType.Dns || uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6);
    }

    private static async Task<IPAddress> ResolvePublic(Uri uri)
    {
        if (!IsWebUrl(uri) || uri.IsLoopback) return null;
        IPAddress literal;
        if (IPAddress.TryParse(uri.DnsSafeHost, out literal)) return IsPublic(literal) ? literal : null;
        // A suffix-free hostname would resolve against the user's local DNS search list.
        var host = uri.IdnHost.TrimEnd('.');
        if (!host.Contains(".") || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return null;
        var lookup = Dns.GetHostAddressesAsync(uri.IdnHost);
        if (await Task.WhenAny(lookup, Task.Delay(2000)) != lookup) return null;
        return (await lookup).Where(IsPublic).OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault();
    }

    internal static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (b.Length == 16)
        {
            // Limit IPv6 to ordinary global unicast, excluding special-use and tunnel prefixes.
            return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && (b[1] == 2 ||
                (b[1] == 1 && (b[2] < 2 || (b[2] == 0x0d && b[3] == 0xb8)))));
        }
        return b[0] > 0 && b[0] < 224 && b[0] != 10 && b[0] != 127
            && !(b[0] == 100 && b[1] >= 64 && b[1] < 128)
            && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            && !(b[0] == 192 && (b[1] == 168 || (b[1] == 0 && (b[2] == 0 || b[2] == 2))))
            && !(b[0] == 198 && (b[1] == 18 || b[1] == 19 || (b[1] == 51 && b[2] == 100)))
            && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    private sealed class PageHead
    {
        public int Status;
        public string Location, Html;
        public bool IsHtml;
    }

    private static async Task<PageHead> ReadHead(Uri uri, IPAddress address)
    {
        // Windows curl can connect to the address we checked while preserving the hostname for HTTPS.
        var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        if (!File.Exists(curl)) return null;
        var target = address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();
        var pinned = uri.HostNameType == UriHostNameType.Dns ? " --resolve " + uri.IdnHost + ":" + uri.Port + ":" + target : "";
        // Use the same ASCII hostname in the URL and the pin, including for internationalized domains.
        var requestUri = uri.HostNameType == UriHostNameType.Dns ? new UriBuilder(uri) { Host = uri.IdnHost }.Uri : uri;
        var safeUrl = requestUri.AbsoluteUri.Replace("\"", "%22").Replace("\\", "%5C");
        var start = new ProcessStartInfo(curl,
            "--disable --silent --globoff --noproxy \"*\" --proto \"=http,https\" --connect-timeout 3 --max-time 5 --compressed --include"
            + pinned + " --url \"" + safeUrl + "\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8
        };
        using (var proc = Process.Start(start))
        {
            try
            {
                // Stop reading as soon as the head is available, without downloading the full article.
                var text = new StringBuilder();
                var buffer = new char[4096];
                PageHead response = null;
                int bodyOffset = 0;
                while (text.Length < HeadLimit)
                {
                    int count = await proc.StandardOutput.ReadAsync(buffer, 0, Math.Min(buffer.Length, HeadLimit - text.Length));
                    if (count == 0) break;
                    text.Append(buffer, 0, count);
                    var current = text.ToString();
                    if (response == null)
                    {
                        var boundary = current.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (boundary < 0) continue;
                        response = ParseHeaders(current.Substring(0, boundary));
                        if (response == null || response.Status != 200 || !response.IsHtml) return response;
                        bodyOffset = boundary + 4;
                    }
                    var end = current.IndexOf("</head>", bodyOffset, StringComparison.OrdinalIgnoreCase);
                    if (end >= 0) { response.Html = current.Substring(bodyOffset, end - bodyOffset); return response; }
                }
                if (response != null) response.Html = text.ToString(bodyOffset, text.Length - bodyOffset);
                return response;
            }
            finally { if (!proc.HasExited) proc.Kill(); }
        }
    }

    private static PageHead ParseHeaders(string headers)
    {
        var lines = headers.Split(new[] { "\r\n" }, StringSplitOptions.None);
        var status = lines[0].Split(' ');
        int code;
        if (status.Length < 2 || !int.TryParse(status[1], out code)) return null;
        var response = new PageHead { Status = code };
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("Location:", StringComparison.OrdinalIgnoreCase)) response.Location = line.Substring(9).Trim();
            if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
            {
                var type = line.Substring(13).Split(';')[0].Trim();
                response.IsHtml = type.Equals("text/html", StringComparison.OrdinalIgnoreCase) || type.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
            }
        }
        return response;
    }
}
