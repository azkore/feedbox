function extractArticleBody(root, base) {
    const allowed = new Set(['p', 'ul', 'ol', 'li', 'blockquote', 'pre', 'code', 'br', 'hr', 'strong', 'em', 'b', 'i', 'del', 's', 'a', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6']);
    const excluded = new Set(['script', 'style', 'template', 'svg', 'iframe', 'form', 'button', 'input', 'video', 'audio', 'noscript']);
    let characters = 0;
    let visited = 0;
    let truncated = false;

    function walk(parent, depth, inCode) {
        const result = [];
        // Bound nesting and size before sending structured text across the WebView boundary.
        for (const child of parent.childNodes) {
            if (++visited > 1500 || depth > 12 || characters >= 12000) { truncated = true; break; }
            if (child.nodeType === Node.TEXT_NODE) {
                const text = inCode ? child.nodeValue : child.nodeValue.replace(/[\t\r\n ]+/g, ' ');
                const remaining = 12000 - characters;
                if (text.length > remaining) truncated = true;
                const value = text.slice(0, remaining);
                if (value) result.push({ tag: 'text', text: value });
                characters += value.length;
                continue;
            }
            if (child.nodeType !== Node.ELEMENT_NODE) continue;
            const tag = child.localName;
            // Keep content from inert wrappers, but drop active controls and their descendants.
            if (excluded.has(tag) || child.hasAttribute('hidden')) continue;
            const children = walk(child, depth + 1, inCode || tag === 'pre' || tag === 'code');
            if (!allowed.has(tag)) { result.push(...children); continue; }
            let href = '';
            if (tag === 'a') {
                try {
                    const target = new URL(child.getAttribute('href'), base);
                    if (child.hasAttribute('href') && ['http:', 'https:'].includes(target.protocol) && !target.username && !target.password && target.href.length <= 2048)
                        href = target.href;
                } catch { /* Invalid links keep their readable text. */ }
            }
            result.push({ tag, href, children });
        }
        return result;
    }

    if (!root) return '';
    const nodes = walk(root, 0, false);
    const body = nodes.filter(node => node.tag !== 'text' || node.text.trim());
    const wrapper = body[0];
    // Reddit makes the entire listing preview clickable. Keep its contents, not that navigation link.
    if (body.length === 1 && wrapper.tag === 'a' && wrapper.href === base
        && wrapper.children.some(node => /^(?:p|ul|ol|blockquote|pre|h[1-6])$/.test(node.tag))) {
        nodes.splice(nodes.indexOf(wrapper), 1, ...wrapper.children);
    }
    return nodes.length ? JSON.stringify({ nodes, truncated }) : '';
}
