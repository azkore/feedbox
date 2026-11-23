using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

internal sealed class OpmlFeed
{
    public Uri Url;
    public string Label, Group;
}

internal static class Opml
{
    public const int MaxBytes = 1024 * 1024;

    public static List<OpmlFeed> Read(byte[] bytes, out int skipped)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("Choose an OPML file up to 1 MiB.");
        XDocument doc;
        // Accept declared encodings while keeping DTDs and external resources disabled.
        using (var stream = new MemoryStream(bytes))
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes }))
            doc = XDocument.Load(reader);
        var root = doc.Root;
        var body = root != null && Named(root, "opml") ? root.Elements().FirstOrDefault(e => Named(e, "body")) : null;
        if (body == null) throw new InvalidDataException("The file is not an OPML subscription list.");
        skipped = 0;
        var result = new List<OpmlFeed>();
        ReadChildren(body, "", result, ref skipped, 0);
        return result;
    }

    private static void ReadChildren(XElement parent, string group, List<OpmlFeed> result, ref int skipped, int depth)
    {
        if (depth > 32) throw new InvalidDataException("The OPML folders are nested too deeply.");
        foreach (var row in parent.Elements().Where(e => Named(e, "outline")))
        {
            var title = Attribute(row, "title");
            if (title == "") title = Attribute(row, "text");
            var link = Attribute(row, "xmlUrl");
            if (link == "")
            {
                // The local sidebar is flat; keep the nearest named folder for nested lists.
                ReadChildren(row, title == "" ? group : Clean(title, 80), result, ref skipped, depth + 1);
                continue;
            }
            Uri url;
            if (!RssCollector.TryFeedUrl(link, out url) || url.Host.Equals("reddit.com", StringComparison.OrdinalIgnoreCase)
                || url.Host.EndsWith(".reddit.com", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
            result.Add(new OpmlFeed { Url = url, Label = Clean(title, 120), Group = group });
        }
    }

    public static byte[] Export(List<string[]> feeds, List<FeedGroup> groups)
    {
        var byName = feeds.ToDictionary(row => row[0]);
        var grouped = new HashSet<string>();
        using (var output = new MemoryStream())
        {
            using (var xml = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
            {
                xml.WriteStartDocument();
                xml.WriteStartElement("opml");
                xml.WriteAttributeString("version", "2.0");
                xml.WriteStartElement("head");
                xml.WriteElementString("title", "Feed Inbox subscriptions");
                xml.WriteEndElement();
                xml.WriteStartElement("body");
                foreach (var group in groups)
                {
                    var members = group.Members.Where(byName.ContainsKey).OrderBy(id => byName[id][1], StringComparer.OrdinalIgnoreCase).ToArray();
                    if (members.Length == 0) continue;
                    xml.WriteStartElement("outline");
                    xml.WriteAttributeString("text", Clean(group.Name, 80));
                    foreach (var id in members) { WriteFeed(xml, byName[id]); grouped.Add(id); }
                    xml.WriteEndElement();
                }
                foreach (var row in feeds.Where(row => !grouped.Contains(row[0]))) WriteFeed(xml, row);
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            return output.ToArray();
        }
    }

    private static void WriteFeed(XmlWriter xml, string[] row)
    {
        xml.WriteStartElement("outline");
        xml.WriteAttributeString("type", "rss");
        xml.WriteAttributeString("text", Clean(row[1], 120));
        xml.WriteAttributeString("xmlUrl", row[2]);
        xml.WriteEndElement();
    }

    private static bool Named(XElement element, string name) { return element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase); }
    private static string Attribute(XElement element, string name)
    {
        var attr = element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        return attr == null ? "" : attr.Value.Trim();
    }

    private static string Clean(string value, int max)
    {
        var result = new StringBuilder();
        for (int i = 0; i < value.Length && result.Length < max; i++)
        {
            if (char.IsSurrogatePair(value, i) && result.Length + 2 <= max) result.Append(value[i++]).Append(value[i]);
            else if (XmlConvert.IsXmlChar(value[i]) && !char.IsControl(value[i])) result.Append(value[i]);
        }
        return result.ToString().Trim();
    }
}
