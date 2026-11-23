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

    public static bool TryAppend(StringBuilder output, string content)
    {
        if (string.IsNullOrEmpty(content) || content.Length > 100000) return false;
        Body body;
        try { body = new JavaScriptSerializer { MaxJsonLength = 100000, RecursionLimit = 50 }.Deserialize<Body>(content); }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        if (body == null || body.nodes == null) return false;

        // Generate from fixed tags. Treat even locally stored data as untrusted.
        var html = new StringBuilder();
        foreach (var node in body.nodes) Render(html, node, 0);
        if (html.Length == 0) return false;
        output.Append("<div class=context>").Append(html);
        if (body.truncated) output.Append("<p class=continued>Continues on the original page.</p>");
        output.Append("</div>");
        return true;
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
