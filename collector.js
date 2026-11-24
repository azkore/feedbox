(async (path, source, first, extractArticleBody) => {
    const origin = 'https://www.reddit.com';
    if (location.origin !== origin) return { problem: 'Open Reddit from the tray to sign in.', auth: true };

    // Follow Reddit's own HTML continuation in the same browser session.
    const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store' });
    const landed = new URL(response.url);
    if (landed.origin !== origin || landed.pathname.startsWith('/login') || [401, 403].includes(response.status)) {
        return { problem: 'Reddit refused the request. Open Reddit from the tray to check your sign-in.', auth: true };
    }
    if (response.status === 429) {
        const value = response.headers.get('Retry-After');
        const seconds = value && /^\d+$/.test(value) ? Number(value) : value ? (Date.parse(value) - Date.now()) / 1000 : 0;
        return { problem: 'Reddit rate-limited the check.', retry: Math.max(900, Math.ceil(seconds || 0)) };
    }
    if (!response.ok) {
        return { problem: 'Reddit returned HTTP ' + response.status, stale: !first && [400, 404, 410].includes(response.status) };
    }

    // Parse only the response HTML. No scrolling, animation or rendering is needed.
    const doc = new DOMParser().parseFromString(await response.text(), 'text/html');
    const cards = Array.from(doc.querySelectorAll('shreddit-post'));
    if (!cards.length && /prove your humanity|verify (that )?you are human|blocked by network security/i.test(doc.body.textContent)) {
        return { problem: 'Reddit needs an interactive check. Open Reddit from the tray.', auth: true };
    }
    if (first && !doc.querySelector('shreddit-app[user-logged-in="true"]')) {
        return { problem: 'Reddit did not return a signed-in listing. Open Reddit from the tray.', auth: true };
    }

    const entries = new Map();
    // Keep only counts for diagnosis; never store a response body or account details in status.
    let ads = 0, incomplete = 0, other = 0;
    for (const card of cards) {
        // Ads are not subreddit posts. Never save links outside this subreddit as its entries.
        if (card.hasAttribute('promoted') || card.closest('shreddit-ad-post')) { ads++; continue; }
        const id = card.getAttribute('thingid') || card.id;
        const match = /^t3_([a-z0-9]+)$/i.exec(id);
        const title = card.getAttribute('post-title') || card.querySelector('[slot=title]')?.textContent;
        const link = card.getAttribute('permalink');
        if (!match || !link || !title?.trim()) { incomplete++; continue; }
        const url = new URL(link, origin);
        const prefix = '/r/' + source + '/comments/' + match[1] + '/';
        if (url.origin !== origin || !url.pathname.toLowerCase().startsWith(prefix.toLowerCase())) { other++; continue; }
        const timestamp = Date.parse(card.getAttribute('created-timestamp'));
        const pinned = ['stickied', 'pinned', 'is-stickied', 'is-pinned'].some(a => card.hasAttribute(a) && card.getAttribute(a) !== 'false');

        // Preserve listing context as text and a Reddit-hosted preview. Do not save remote HTML.
        const textBody = card.querySelector('[slot=text-body], shreddit-post-text-body');
        const text = textBody?.cloneNode(true);
        text?.querySelectorAll('script,style').forEach(e => e.remove());
        text?.querySelectorAll('p,li,pre,blockquote,br,h1,h2,h3,h4').forEach(e => e.after('\n\n'));
        const body = text?.textContent.trim().replace(/\n{3,}/g, '\n\n').slice(0, 12000) || '';
        const previews = [card.getAttribute('content-href'), ...Array.from(card.querySelectorAll('img[src]'), e => e.getAttribute('src'))];
        let image = '';
        for (const preview of previews) {
            if (!preview) continue;
            try {
                const candidate = new URL(preview, origin);
                if (candidate.protocol === 'https:' && ['i.redd.it', 'preview.redd.it', 'external-preview.redd.it'].includes(candidate.hostname)) {
                    image = candidate.href;
                    break;
                }
            } catch { /* Ignore malformed preview URLs, without dropping the post. */ }
        }
        entries.set(id, {
            id, title: title.trim(), path: url.pathname, created: Number.isNaN(timestamp) ? null : new Date(timestamp).toISOString(), pinned,
            author: (card.getAttribute('author') || '').slice(0, 64), body, image,
            content: extractArticleBody(textBody, url.href)
        });
    }

    // Do not treat a response we cannot parse as a successful empty check.
    if (!entries.size) {
        const page = first ? 'New listing' : 'Continuation page';
        const detail = cards.length ? `${cards.length} cards: ${ads} ads, ${incomplete} missing required fields, ${other} unexpected links.` : 'No post cards in the response HTML.';
        return { problem: `${page}: ${detail} Catch-up point kept.` };
    }

    let next = '';
    for (const element of doc.querySelectorAll('faceplate-partial[src]')) {
        const url = new URL(element.getAttribute('src'), origin);
        if (url.origin === origin && url.pathname === '/svc/shreddit/community-more-posts/new/' && url.searchParams.has('after')) {
            next = url.pathname + url.search;
            break;
        }
    }
    // Match the avatar to this community's ID, not a post author or recommendation.
    let icon = '';
    const header = first && doc.querySelector('shreddit-subreddit-header');
    const community = header && header.getAttribute('subreddit-id');
    if (community && header.getAttribute('name')?.toLowerCase() === source.toLowerCase()) {
        for (const img of doc.querySelectorAll('img[src*="styles.redditmedia.com/"]')) {
            try {
                const candidate = new URL(img.getAttribute('src'), origin);
                if (candidate.protocol === 'https:' && candidate.hostname === 'styles.redditmedia.com'
                    && candidate.pathname.startsWith(`/${community}/styles/communityIcon`)) { icon = candidate.href; break; }
            } catch { /* An unusable icon URL must not affect the listing. */ }
        }
    }
    return { posts: Array.from(entries.values()), next, icon };
})
