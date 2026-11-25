using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

internal static class ArticleBody
{
    private sealed class Body
    {
        public Node[] nodes { get; set; }
        public bool truncated { get; set; }
    }

    private sealed class Node
    {
        public string tag { get; set; }
        public string text { get; set; }
        public string href { get; set; }
        public Node[] children { get; set; }
    }

    private static readonly HashSet<string> Paired = new HashSet<string>
    {
        "p", "ul", "ol", "li", "blockquote", "pre", "code", "strong", "em", "b", "i", "del", "s"
    };

    private static readonly HashSet<string> PreviewBlocks = new HashSet<string>
    {
        "p", "ul", "ol", "blockquote", "pre", "h1", "h2", "h3", "h4", "h5", "h6"
    };

    public static bool TryAppend(StringBuilder output, string content, Uri redditPermalink = null)
    {
        if (string.IsNullOrEmpty(content) || content.Length > 100000) return false;
        Body body;
        try { body = new JavaScriptSerializer { MaxJsonLength = 100000, RecursionLimit = 50 }.Deserialize<Body>(content); }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        if (body == null || body.nodes == null) return false;

        // Repair previously saved listing previews when read, without rewriting the archive.
        if (redditPermalink != null) UnwrapPreview(body.nodes, redditPermalink);

        // Generate from fixed tags. Treat even locally stored data as untrusted.
        var html = new StringBuilder();
        foreach (var node in body.nodes) Render(html, node, 0);
        if (html.Length == 0) return false;
        output.Append("<div class=context>").Append(html);
        if (body.truncated) output.Append("<p class=continued>Continues on the original page.</p>");
        output.Append("</div>");
        return true;
    }

    private static void UnwrapPreview(Node[] nodes, Uri permalink)
    {
        // A prose link within a paragraph, or beside other body content, is not a preview wrapper.
        Node wrapper = null;
        foreach (var node in nodes)
        {
            if (node == null || node.tag == "text" && string.IsNullOrWhiteSpace(node.text)) continue;
            if (wrapper != null || node.tag != "a") return;
            wrapper = node;
        }
        Uri target;
        if (wrapper == null || wrapper.children == null || !Uri.TryCreate(wrapper.href, UriKind.Absolute, out target)
            || target.AbsoluteUri != permalink.AbsoluteUri) return;
        foreach (var child in wrapper.children)
        {
            if (child == null || !PreviewBlocks.Contains(child.tag)) continue;
            // Render an inert container by retaining its children and dropping only the link tag.
            wrapper.tag = null;
            return;
        }
    }

    private static void Render(StringBuilder html, Node node, int depth)
    {
        if (node == null || depth > 12) return;
        if (node.tag == "text") { html.Append(WebUtility.HtmlEncode(node.text)); return; }
        if (node.tag == "br" || node.tag == "hr") { html.Append('<').Append(node.tag).Append('>'); return; }

        string tag = null;
        Uri target;
        if (node.tag == "a" && Uri.TryCreate(node.href, UriKind.Absolute, out target)
            && (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp) && target.UserInfo == "")
        {
            tag = "a";
            html.Append("<a target=_blank rel='noopener noreferrer' href='").Append(WebUtility.HtmlEncode(target.AbsoluteUri)).Append("'>");
        }
        else if (node.tag != null && Paired.Contains(node.tag))
        {
            tag = node.tag;
            html.Append('<').Append(tag).Append('>');
        }
        else if (node.tag == "h1" || node.tag == "h2" || node.tag == "h3" || node.tag == "h4" || node.tag == "h5" || node.tag == "h6")
        {
            // Reserve h2 for the date groups used by the reader's pagination.
            tag = "h4";
            html.Append("<h4>");
        }

        if (node.children != null) foreach (var child in node.children) Render(html, child, depth + 1);
        if (tag != null) html.Append("</").Append(tag).Append('>');
    }
}
