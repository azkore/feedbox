(() => {
    const main = document.querySelector('main');
    const nav = document.querySelector('aside nav');
    let entries = main.querySelector('#entries');
    let status = main.querySelector('#key-status');
    let header = main.querySelector('.feed-header');
    let selected = null;
    let busy = false;
    let navigation;
    let displayedUrl = location.href;
    let readRevision = 0;
    let readerTail = 0;
    const rows = () => Array.from(entries.querySelectorAll('article[data-post]'));
    const viewFeed = url => new URL(url).searchParams.get('feed') || '';
    let visitFeed = viewFeed(displayedUrl);
    let visitStart = header.dataset.visitStart;
    let visitRead = 0;
    let visitTotal = 0;

    function visitHeaders() {
        return visitFeed ? { 'X-Feedbox-Read-Since': visitStart } : undefined;
    }

    function acceptVisitCount(response) {
        const read = response.headers.get('X-Feedbox-Visit-Read');
        const total = response.headers.get('X-Feedbox-Visit-Total');
        if (read !== null && total !== null) {
            visitRead = Number(read);
            visitTotal = Number(total);
        }
    }

    function showVisitCount() {
        // Show progress only on the selected feed and its shortcut, once at least one article is read.
        const selected = 'a.feed[aria-current=page] > .count-badge > .visit-progress, a.feed.is-current-pin > .count-badge > .visit-progress';
        for (const progress of nav.querySelectorAll('.visit-progress:not(:empty)'))
            if (!progress.matches(selected)) progress.textContent = '';
        for (const progress of nav.querySelectorAll(selected)) progress.textContent = visitRead > 0 ? `${visitRead}/${visitTotal}` : '';
    }

    // Preserve browser preferences and bookmarks when switching to the new storage names.
    function migrateBrowserKeys(storage) {
        for (const key of Object.keys(storage)) {
            if (!key.startsWith('feed-inbox.')) continue;
            const renamed = 'feedbox.' + key.slice('feed-inbox.'.length);
            if (storage.getItem(renamed) === null) storage.setItem(renamed, storage.getItem(key));
            storage.removeItem(key);
        }
    }
    try { migrateBrowserKeys(localStorage); } catch { /* Reading also works without stored preferences. */ }
    try { migrateBrowserKeys(sessionStorage); } catch { /* The browser may block session storage. */ }

    // Use the view's filters as the bookmark key, independent of receipts and pagination URLs.
    function readingKey(url) {
        const params = new URL(url).searchParams;
        const flags = params.get('flags') ?? (params.get('all') === '1' ? '15' : '5');
        // Keep bookmarks made before the checkbox filter replaced Unread and View all articles.
        const state = flags === '5' ? '' : flags === '15' ? '1' : 'flags:' + flags;
        return 'feedbox.position:' + JSON.stringify(['feed', 'group', 'all', 'oldest', 'from', 'to', 'q'].map(key => key === 'all' ? state : params.get(key) || ''));
    }

    function readingPosition(url) {
        try { return localStorage.getItem(readingKey(url)); }
        catch { return null; }
    }

    let menuCheck;
    async function checkFeed(checkForm) {
        const button = checkForm.querySelector('button');
        if (button.disabled) return;
        const body = new URLSearchParams(new FormData(checkForm));
        body.set('inline', '1');
        const feed = body.get('target-feed') ?? body.get('feed');
        const action = checkForm.getAttribute('action');
        const visit = action === '/visit-feed';
        const currentFeed = header.querySelector('form[action="/check-feed"]')?.elements.feed.value;
        const otherFeed = body.has('target-feed') && feed !== currentFeed;
        const name = checkForm.dataset.targetLabel || checkForm.closest('li')?.querySelector('b').textContent
            || header.querySelector('h1').textContent;
        const key = (visit ? '/check-feed' : action) + ':' + feed;
        if (checks.get(key)?.pending) return;
        const state = { pending: true, text: visit ? `Opening ${name} in feedbox…` : feed ? `Checking ${name}…` : 'Checking…', seconds: 0 };
        checks.set(key, state);
        const feedback = header.querySelector('#feed-action-feedback');
        if (otherFeed) menuCheck = state;
        // Give sidebar actions their own result without replacing the selected feed or batch status.
        function report() {
            showCheck();
            if (otherFeed && menuCheck === state && feedback.isConnected)
                renderCheckResult(feedback, state, feed, name, true);
        }
        report();
        const started = Date.now();
        const timer = setInterval(() => {
            state.seconds = Math.floor((Date.now() - started) / 1000);
            report();
        }, 1000);
        try {
            const response = await fetch(action, { method: 'POST', body });
            state.text = await response.text() || 'No result received.';
            state.warning = !response.ok || response.headers.get('X-Feedbox-Check-Warning') === '1';
            state.needsVisit = response.headers.get('X-Feedbox-Needs-Visit') === '1';
        } catch {
            state.text = 'Lost contact with feedbox before receiving the result.';
            state.warning = true;
        } finally {
            clearInterval(timer);
            state.pending = false;
            report();
            refreshCheckStatus(header.querySelector('.check-status'));
            refreshUnreadCounts();
        }
    }

    function renderCheckResult(feedback, state, feed, name, prefix = false) {
        feedback.textContent = state.pending ? state.text + (state.seconds ? ` ${state.seconds}s` : '')
            : (prefix ? `${name}: ` : '') + state.text;
        feedback.classList.toggle('check-warning', !state.pending && Boolean(state.warning));
        const action = feedback.parentElement.querySelector('.check-visit');
        if (!action) return;
        // Bind the action to the feed that produced the warning, even in an aggregate view.
        action.hidden = state.pending || !state.needsVisit || !feed;
        if (!action.hidden) {
            const form = action.querySelector('form');
            form.elements['target-feed'].value = feed;
            form.dataset.targetLabel = name;
        }
    }

    let batchRequest = false;
    let batchPoll = false;

    async function refreshBatchStatus() {
        const feedback = header.querySelector('#check-feedback[data-check-batch]');
        if (!feedback || batchRequest || batchPoll) return;
        const form = header.querySelector('form[action="/check-all"]');
        const wasRunning = feedback.dataset.running === '1';
        batchPoll = true;
        try {
            const response = await fetch('/check-all-status');
            if (!response.ok) return;
            const { Running, Message } = await response.json();
            if (!feedback.isConnected || batchRequest) return;
            feedback.dataset.running = Running ? '1' : '0';
            if (feedback.textContent !== Message) feedback.textContent = Message;
            feedback.classList.toggle('check-warning', !Running && Message.startsWith('Could not complete'));
            if (form) form.querySelector('button').disabled = Running;
            if (wasRunning && !Running) {
                refreshCheckStatus(header.querySelector('.check-status'));
                refreshUnreadCounts();
            }
        } catch { /* A transient local connection error can be retried on the next poll. */ }
        finally { batchPoll = false; }
    }

    async function checkAll(form) {
        const button = form.querySelector('button');
        if (button.disabled || batchRequest) return;
        const feedback = header.querySelector('#check-feedback');
        const fields = new URLSearchParams(new FormData(form));
        fields.set('inline', '1');
        form.querySelector('details').open = false;
        button.disabled = true;
        batchRequest = true;
        try {
            const response = await fetch(form.action, { method: 'POST', body: fields });
            const message = await response.text();
            if (form.isConnected) {
                feedback.textContent = message;
                feedback.classList.toggle('check-warning', !response.ok);
            }
            if (!response.ok) return;
            if (form.isConnected) feedback.dataset.running = '1';
        } catch {
            if (form.isConnected) {
                feedback.textContent = 'Lost contact with feedbox.';
                feedback.classList.add('check-warning');
            }
        } finally {
            batchRequest = false;
            // Keep the button available for another attempt if the request did not start a batch.
            if (form.isConnected) button.disabled = feedback.dataset.running === '1';
        }
        await refreshBatchStatus();
    }

    setInterval(() => {
        if (!document.hidden && header.querySelector('#check-feedback').dataset.running === '1') refreshBatchStatus();
    }, 3000);

    // Keep the result associated with its feed even if the reader switches away mid-check.
    const checks = new Map();
    function showCheck() {
        const batchForm = header.querySelector('form[action="/check-all"]');
        if (batchForm) {
            batchForm.querySelector('button').disabled = batchRequest || header.querySelector('#check-feedback').dataset.running === '1';
            return;
        }
        const form = header.querySelector('form[action="/check-feed"], form[action="/check"]');
        if (!form) return;
        const state = checks.get(form.getAttribute('action') + ':' + form.elements.feed.value);
        if (!state) return;
        const button = form.querySelector('button');
        button.disabled = state.pending;
        renderCheckResult(header.querySelector('#check-feedback'), state, form.elements.feed.value, header.querySelector('h1').textContent);
    }
    showCheck();

    async function pageFrom(response) {
        if (!response.ok) throw new Error(await response.text() || 'Could not update the inbox.');
        return new DOMParser().parseFromString(await response.text(), 'text/html');
    }

    const statusRefreshes = new WeakSet();
    async function refreshCheckStatus(details) {
        if (!details.open || statusRefreshes.has(details)) return;
        statusRefreshes.add(details);
        try {
            // Read only the local status list and keep the dropdown and its scroll position intact.
            const response = await fetch(details.dataset.statusUrl);
            const update = (await pageFrom(response)).querySelector('ul');
            if (update && details.isConnected && details.open) {
                const list = details.querySelector('ul');
                const scroll = list.scrollTop;
                list.replaceChildren(...update.children);
                list.scrollTop = scroll;
            }
        } catch { /* Keep the last result available if the local app is restarting. */ }
        finally { statusRefreshes.delete(details); }
    }

    main.addEventListener('toggle', event => {
        if (event.target.matches('.check-status')) refreshCheckStatus(event.target);
    }, true);
    setInterval(() => refreshCheckStatus(header.querySelector('.check-status')), 30000);

    let countsPending = false;
    async function refreshUnreadCounts() {
        if (document.hidden || busy || navigation || countsPending) return;
        countsPending = true;
        const view = entries;
        const revision = readRevision;
        try {
            // Periodic counts must use the same dates as the rendered articles.
            const url = new URL('/unread-counts', displayedUrl);
            const filter = new URL(displayedUrl).searchParams;
            url.searchParams.set('flags', document.body.dataset.flags);
            url.searchParams.set('feed', filter.get('feed') || '');
            if (filter.has('group')) url.searchParams.set('group', filter.get('group'));
            for (const bound of ['from', 'to', 'q']) if (filter.has(bound)) url.searchParams.set(bound, filter.get(bound));
            const response = await fetch(url, { headers: visitHeaders() });
            if (!response.ok) return;
            const { feeds, total, arrival, archive, icons, warnings, pausedFeeds } = await response.json();
            // Discard a snapshot taken before a newer read action or view change.
            if (busy || navigation || entries !== view || readRevision !== revision) return;
            acceptVisitCount(response);
            if (archive) updateHeadingCounts(archive);
            const unread = name => Object.hasOwn(feeds, name) ? feeds[name] : 0;
            const availableIcons = new Set(icons || []);
            const feedWarnings = new Set(warnings || []);
            const paused = new Set(pausedFeeds || []);
            for (const link of document.querySelectorAll('aside .feed[data-feed]')) {
                let count = total;
                if (link.dataset.feed) {
                    count = unread(link.dataset.feed);
                    if (availableIcons.has(link.dataset.feed)) addFeedIcon(link, link.dataset.feed);
                    setFeedCheckState(link, feedWarnings.has(link.dataset.feed), paused.has(link.dataset.feed));
                }
                else if (link.dataset.group !== '0') {
                    // Sum this group's feeds only; the global total counts each post once.
                    const members = link.closest('.feed-group').querySelectorAll('.group-members .feed[data-feed]');
                    count = Array.from(members).reduce((sum, member) => sum + unread(member.dataset.feed), 0);
                }
                link.querySelector('.count').textContent = count;
            }
            updateMoreFeeds();
            if (arrival > Number(view.dataset.arrival)) await refreshArticles(view, revision);
        } catch { /* Preserve the current badges if the local app cannot be reached. */ }
        finally { countsPending = false; }
    }

    function setFeedCheckState(link, warning, paused) {
        // Paused names stay gray even if a previous check failed.
        link.classList.toggle('has-check-warning', warning);
        link.classList.toggle('feed-paused', paused);
        if (paused) link.setAttribute('aria-description', 'Feed paused');
        else if (warning) link.setAttribute('aria-description', 'Check status has a warning');
        else link.removeAttribute('aria-description');
    }

    function addFeedIcon(link, key) {
        const label = link.querySelector('.feed-label');
        if (label.querySelector('.feed-site-icon')) return;
        const icon = document.createElement('img');
        icon.className = 'feed-site-icon';
        icon.alt = '';
        icon.width = icon.height = 16;
        icon.loading = 'lazy';
        icon.src = '/feed-icon?feed=' + encodeURIComponent(key);
        label.prepend(icon);
    }

    // Restore the built-in symbol if an icon file was removed or could not be decoded.
    document.addEventListener('error', event => {
        if (event.target.matches?.('.feed-site-icon')) event.target.hidden = true;
    }, true);

    function updateHeadingCounts(counts) {
        const heading = header.querySelector('.heading-counts');
        for (const field of ['read', 'total', 'marked']) {
            const value = String(counts[field]);
            const counter = heading.querySelector(`[data-count=${field}]`);
            if (counter.textContent !== value) counter.textContent = value;
        }
        heading.querySelector('[data-count-part=read]').hidden = counts.read === 0;
    }

    async function refreshArticles(view, revision) {
        const more = document.querySelector('#more');
        const edge = more?.href;
        const url = new URL('/new-articles', displayedUrl);
        const filter = new URL(displayedUrl).searchParams;
        url.searchParams.set('flags', document.body.dataset.flags);
        for (const key of ['feed', 'group', 'oldest', 'from', 'to', 'q']) if (filter.has(key)) url.searchParams.set(key, filter.get(key));
        if (edge) {
            const boundary = new URL(edge).searchParams;
            for (const key of ['before', 'id']) url.searchParams.set(key, boundary.get(key));
        }
        url.searchParams.set('since', view.dataset.arrival);
        const response = await fetch(url);
        if (!response.ok) return;
        const delta = (await pageFrom(response)).querySelector('#entries');
        if (!delta || busy || navigation || entries !== view || readRevision !== revision || document.querySelector('#more')?.href !== edge) return;

        const currentRows = rows();
        const ids = new Set(currentRows.map(row => row.dataset.post));
        const incoming = [...delta.querySelectorAll('article[data-post]')].filter(row => !ids.has(row.dataset.post));
        if (incoming.length) {
            const anchor = selected && onScreen(selected) ? selected : currentRows.find(onScreen);
            const top = anchor?.getBoundingClientRect().top;
            const direction = filter.get('oldest') === '1' ? 1 : -1;
            const compareValue = (a, b) => a < b ? -1 : a > b ? 1 : 0;
            const compare = (a, b) => direction * (compareValue(a.dataset.published, b.dataset.published) || compareValue(a.dataset.post, b.dataset.post));
            // Keep existing article nodes, focus, and the position of the article already being read.
            const merged = currentRows.concat(incoming).sort(compare);
            let next = null;
            for (let i = merged.length - 1; i >= 0; i--) {
                const row = merged[i];
                if (!ids.has(row.dataset.post)) view.insertBefore(row, next);
                next = row;
            }
            for (const node of view.querySelectorAll(':scope > h2, :scope > p')) node.remove();
            let day;
            for (const row of merged) {
                if (row.dataset.day === day) continue;
                day = row.dataset.day;
                const heading = document.createElement('h2');
                heading.textContent = day;
                view.insertBefore(heading, row);
            }
            if (anchor) window.scrollBy(0, anchor.getBoundingClientRect().top - top);
            for (const row of incoming) observePublisherPreview(row);
        }
        view.dataset.arrival = delta.dataset.arrival;
        const bulk = header.querySelector('form[action="/mark-feed-read"]');
        if (bulk) bulk.elements.through.value = delta.dataset.arrival;
    }

    setInterval(refreshUnreadCounts, 30000);
    document.addEventListener('visibilitychange', refreshUnreadCounts);

    function applyView(page, url, historyMode, focus, resume = false) {
        const nextMain = page.querySelector('main');
        const nextNav = page.querySelector('aside nav');
        const nextOverview = page.querySelector('.sidebar-header > .feed');
        // Refresh form tokens and targets while keeping the disclosure and its sort control mounted.
        const panels = [...document.querySelectorAll('.sidebar-tools-panel > details')];
        const nextPanels = [...page.querySelectorAll('.sidebar-tools-panel > details')];
        if (!nextMain?.querySelector('#entries') || !nextMain.querySelector('.feed-header') || !nextNav || !nextOverview || panels.length !== nextPanels.length)
            throw new Error('feedbox returned an incomplete view.');

        // Update the server-rendered view without rebuilding the sidebar shell or the document.
        const sidebarScroll = nav.scrollTop;
        const sameView = historyMode === 'preserve' || url === displayedUrl;
        // A new article filter changes what the sidebar counts; switching feeds does not.
        if (sidebarCountScope(url, page.body.dataset.flags) !== sidebarCountScope(displayedUrl, document.body.dataset.flags))
            countFeedOrder.clear();
        const readerScroll = window.scrollY;
        const statusOpen = sameView && header.querySelector('.check-status').open;
        closeFeedMenu();
        closeCountTip();
        previewObserver.disconnect();
        document.querySelector('.sidebar-header > .feed').replaceWith(nextOverview);
        nav.replaceChildren(...nextNav.children);
        panels.forEach((panel, i) => panel.replaceChildren(...nextPanels[i].children));
        main.replaceChildren(...nextMain.children);
        syncReaderEdges();
        setReaderTail(0);
        document.body.dataset.unread = page.body.dataset.unread;
        document.body.dataset.flags = page.body.dataset.flags;
        entries = main.querySelector('#entries');
        observePublisherPreviews();
        header = main.querySelector('.feed-header');
        // Filters and same-feed actions replace the HTML but do not start a new visit.
        const nextFeed = viewFeed(url);
        if (nextFeed !== visitFeed) {
            visitFeed = nextFeed;
            visitStart = header.dataset.visitStart;
            visitRead = 0;
            visitTotal = 0;
        }
        status = main.querySelector('#key-status');
        header.querySelector('.check-status').open = statusOpen;
        selected = null;
        displayedUrl = url;
        applyFeedSort();
        restoreGroups();
        sortSidebarFeeds();
        updateMoreFeeds();
        nav.scrollTop = sidebarScroll;
        revealCurrentFeed();
        showCheck();

        if (historyMode === 'push' && url !== location.href) history.pushState(null, '', url);
        if (focus) {
            const heading = header.querySelector('h1');
            heading.tabIndex = -1;
            heading.focus({ preventScroll: true });
        }
        const anchor = resume && readingPosition(url);
        const row = anchor && rows().find(article => article.dataset.post === anchor);
        if (row) select(row, false);
        else window.scrollTo(0, historyMode === 'preserve' ? readerScroll : 0);
    }

    function showError(error) { status.textContent = error.message || 'Could not update the inbox.'; }

    async function navigate(href, fromHistory = false) {
        // Only the last requested view may replace the document regions.
        if (busy && !fromHistory) return;
        navigation?.abort();
        const request = navigation = new AbortController();
        main.setAttribute('aria-busy', 'true');
        try {
            const url = new URL(href, location.href);
            const anchor = !url.searchParams.has('before') && readingPosition(url);
            if (anchor) url.searchParams.set('at', anchor);
            // Recount against the destination filters while keeping the same feed visit boundary.
            const headers = viewFeed(url) === visitFeed ? visitHeaders() : undefined;
            const response = await fetch(url, { signal: request.signal, cache: 'no-store', headers });
            const page = await pageFrom(response);
            if (navigation === request) {
                const visible = new URL(href, location.href);
                visible.searchParams.delete('at');
                acceptVisitCount(response);
                applyView(page, visible.href, fromHistory ? 'history' : 'push', true, true);
            }
        } catch (error) {
            if (!request.signal.aborted) {
                if (fromHistory) location.reload(); // The address already changed; resync the view.
                else showError(error);
            }
        } finally {
            if (navigation === request) { navigation = null; main.removeAttribute('aria-busy'); }
        }
    }

    window.addEventListener('popstate', () => navigate(location.href, true));

    let menuButton = null;
    function closeFeedMenu() {
        if (!menuButton) return;
        menuButton.setAttribute('aria-expanded', 'false');
        main.querySelector('.feed-menu').hidden = true;
        menuButton = null;
    }

    nav.addEventListener('click', event => {
        const button = event.target.closest('.feed-options');
        if (!button) return;
        if (button === menuButton) { closeFeedMenu(); return; }
        closeFeedMenu();
        menuButton = button;
        button.setAttribute('aria-expanded', 'true');
        const menu = main.querySelector('.feed-menu');
        const link = button.closest('.feed-row').querySelector('a.feed');
        const feed = link.dataset.feed ?? link.dataset.pinnedFeed;
        // Bind the menu to the clicked feed, independently of the open reading view.
        for (const form of menu.querySelectorAll('form')) form.elements['target-feed'].value = feed;
        const pause = menu.querySelector('form[action="/collection"] button');
        const paused = link.classList.contains('feed-paused');
        pause.value = paused ? 'resume' : 'pause';
        pause.textContent = paused ? 'Resume' : 'Pause';
        const refresh = menu.querySelector('form[action="/check-feed"]');
        refresh.dataset.targetLabel = link.querySelector('.feed-label').textContent;
        refresh.querySelector('button').disabled = Boolean(checks.get('/check-feed:' + feed)?.pending);
        const anchor = button.getBoundingClientRect();
        // Place the shared menu outside the scrolling sidebar so the last row's menu cannot be clipped.
        menu.hidden = false;
        menu.style.left = `${Math.max(8, anchor.right - menu.offsetWidth)}px`;
        menu.style.top = `${anchor.bottom + menu.offsetHeight <= innerHeight ? anchor.bottom : Math.max(8, anchor.top - menu.offsetHeight)}px`;
        menu.querySelector('button').focus({ preventScroll: true });
    });

    nav.addEventListener('scroll', closeFeedMenu);
    document.addEventListener('pointerdown', event => {
        if (!event.target.closest('.feed-options, .feed-menu')) closeFeedMenu();
    });
    document.addEventListener('keydown', event => {
        if (menuButton && event.key === 'Escape') {
            menuButton.focus({ preventScroll: true });
            closeFeedMenu();
        }
    });

    const countTip = document.querySelector('#sidebar-count-tip');
    let pendingCountTip;

    function closeCountTip() {
        if (!pendingCountTip) return;
        clearTimeout(pendingCountTip.timer);
        pendingCountTip.controller.abort();
        pendingCountTip.link.removeAttribute('aria-describedby');
        pendingCountTip = null;
        countTip.hidden = true;
    }

    function openCountTip(link) {
        if (pendingCountTip?.link === link) return;
        closeCountTip();
        if (!link || menuButton) return;

        // Crossing the sidebar should not send a request for every row along the way.
        const state = { link, controller: new AbortController() };
        pendingCountTip = state;
        state.timer = setTimeout(async () => {
            try {
                const target = new URL(link.href);
                const url = new URL('/sidebar-counts', location.href);
                for (const field of ['feed', 'group']) if (target.searchParams.has(field)) url.searchParams.set(field, target.searchParams.get(field));
                const response = await fetch(url, { signal: state.controller.signal });
                if (!response.ok) return;
                const counts = await response.json();
                if (pendingCountTip !== state || !link.isConnected || link.closest('[hidden]')) return;

                // Position outside the scrolling list and keep the popup inside the viewport.
                const labels = ['marked', 'read', 'unread', 'total'];
                countTip.replaceChildren(...labels.flatMap((label, index) => {
                    const line = document.createElement('span');
                    line.dataset.count = label;
                    line.textContent = `${label}: ${counts[label].toLocaleString()}`;
                    return index === 0 ? [line] : ['\n', line];
                }));
                countTip.append(`\n\nlast 24h: ${counts.last24h.toLocaleString()}\nlast week: ${counts.lastWeek.toLocaleString()}`);
                const anchor = link.getBoundingClientRect();
                countTip.hidden = false;
                countTip.style.left = `${Math.max(8, Math.min(anchor.right + 6, innerWidth - countTip.offsetWidth - 8))}px`;
                countTip.style.top = `${Math.max(8, Math.min(anchor.top, innerHeight - countTip.offsetHeight - 8))}px`;
                link.setAttribute('aria-describedby', countTip.id);
            } catch { /* A cancelled or failed count request should not interrupt reading. */ }
        }, 300);
    }

    // The overview title, normal rows, and pinned rows share the count tooltip.
    const sidebar = nav.closest('aside');
    sidebar.addEventListener('pointerover', event => {
        if (event.pointerType !== 'touch') openCountTip(event.target.closest('a.feed'));
    });
    sidebar.addEventListener('pointerout', event => {
        if (pendingCountTip?.link.contains(event.target) && !pendingCountTip.link.contains(event.relatedTarget)) closeCountTip();
    });
    sidebar.addEventListener('focusin', event => openCountTip(event.target.closest('a.feed')));
    sidebar.addEventListener('focusout', closeCountTip);
    nav.addEventListener('scroll', closeCountTip, { passive: true });
    sidebar.addEventListener('pointerdown', closeCountTip);
    document.addEventListener('keydown', event => { if (event.key === 'Escape') closeCountTip(); });
    window.addEventListener('resize', closeCountTip);
    window.addEventListener('blur', closeCountTip);

    let sourceRequest;
    async function loadFeedSource(dialog, form) {
        sourceRequest?.abort();
        const request = sourceRequest = new AbortController();
        const error = dialog.querySelector('.feed-editor-error');
        const input = form.elements.url;
        const save = form.querySelector('button[type=submit]');
        const feed = form.elements['edited-feed'].value;
        input.value = '';
        input.disabled = save.disabled = true;
        form.elements.version.value = '';
        error.textContent = 'Loading feed URL…';
        // Ignore a late response if the dialog was closed, replaced, or opened for a different feed.
        const isCurrent = () => sourceRequest === request && dialog.isConnected && dialog.open && !form.hidden
            && form.elements['edited-feed'].value === feed;
        try {
            const url = '/feed-source?feed=' + encodeURIComponent(feed);
            const response = await fetch(url, { signal: request.signal });
            if (!response.ok) throw new Error(await response.text());
            const source = await response.json();
            if (!isCurrent()) return;
            input.value = source.url;
            form.elements.version.value = source.version;
            input.disabled = save.disabled = false;
            error.textContent = '';
        } catch (failure) {
            if (isCurrent()) error.textContent = failure.message || 'Could not load the feed URL.';
        }
    }

    main.addEventListener('click', event => {
        if (event.target.closest('.cancel-feed-edit')) { main.querySelector('.feed-editor').close(); return; }
        const action = event.target.closest('.feed-menu button')?.dataset.edit;
        if (!action || !menuButton) return;
        const link = menuButton.closest('.feed-row').querySelector('a.feed');
        const feed = link.dataset.feed ?? link.dataset.pinnedFeed;
        const name = link.querySelector('.feed-label').textContent;
        const dialog = main.querySelector('.feed-editor');
        const settings = dialog.querySelector('form[action="/feed-settings"]');
        const remove = dialog.querySelector('form[action="/delete-feed"]');
        const deleting = action === 'delete';
        dialog.querySelector('h2').textContent = deleting ? `Delete ${name}?` : `Settings for ${name}`;
        dialog.querySelector('.feed-editor-error').textContent = '';
        const form = deleting ? remove : settings;
        settings.hidden = deleting;
        remove.hidden = !deleting;
        form.elements['edited-feed'].value = feed;
        const isRss = link.classList.contains('rss-feed');
        if (!deleting) {
            settings.elements.label.value = name;
            settings.elements.open.value = link.dataset.openDiscussion === '1' ? 'discussion' : 'original';
            const rss = settings.querySelector('.feed-rss-settings');
            rss.hidden = rss.disabled = !isRss;
            settings.querySelector('button[type=submit]').disabled = false;
            // Include hidden feed rows: unread filtering does not change membership.
            const groups = new Set([...nav.querySelectorAll('.group-members a.feed[data-feed]')].filter(a => a.dataset.feed === feed).map(a => a.dataset.group));
            for (const input of settings.querySelectorAll('[name=membership]')) input.checked = groups.has(input.value);
        }
        menuButton.focus({ preventScroll: true });
        closeFeedMenu();
        dialog.showModal();
        if (deleting) remove.querySelector('.cancel-feed-edit').focus();
        else {
            settings.elements.label.select();
            if (isRss) loadFeedSource(dialog, settings);
        }
    });

    let draggedFeed = null;
    let highlightedGroup = null;
    function highlightDrop(group) {
        highlightedGroup?.classList.remove('feed-drop-target');
        highlightedGroup = group;
        highlightedGroup?.classList.add('feed-drop-target');
    }

    nav.addEventListener('dragstart', event => {
        const link = event.target.closest('a.feed[draggable=true]');
        if (!link || busy || navigation || !nav.querySelector('form[action="/move-feed"]')) return;
        // Track drags started here; a link dropped from another page cannot move a subscription.
        draggedFeed = { feed: link.dataset.feed, group: link.dataset.group };
        event.dataTransfer.setData('text/plain', link.textContent.trim());
        event.dataTransfer.effectAllowed = 'move';
    });

    function dropGroup(event) {
        const group = event.target.closest('.group-heading');
        return group && group.querySelector('.group-link').dataset.group !== draggedFeed.group ? group : null;
    }

    nav.addEventListener('dragover', event => {
        if (!draggedFeed) return;
        const target = dropGroup(event);
        highlightDrop(target);
        if (!target) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
    });
    nav.addEventListener('dragleave', event => {
        if (!nav.contains(event.relatedTarget)) highlightDrop(null);
    });
    document.addEventListener('dragend', () => { draggedFeed = null; highlightDrop(null); });

    nav.addEventListener('drop', async event => {
        if (!draggedFeed) return;
        event.preventDefault();
        const target = dropGroup(event);
        const moved = draggedFeed;
        draggedFeed = null;
        highlightDrop(null);
        if (!target || busy || navigation) return;
        const form = nav.querySelector('form[action="/move-feed"]');
        if (!form) return;
        const fields = new URLSearchParams(new FormData(form));
        fields.set('moved-feed', moved.feed);
        fields.set('from-group', moved.group);
        fields.set('to-group', target.querySelector('.group-link').dataset.group);
        const view = entries;
        busy = true;
        try {
            // Apply the new sidebar only after SQLite confirms the move.
            const response = await fetch(form.action, { method: 'POST', body: fields });
            const page = await pageFrom(response);
            if (entries === view && !navigation) {
                if (response.url !== location.href) history.replaceState(null, '', response.url);
                applyView(page, response.url, 'preserve', false);
            }
        } catch (error) { showError(error); }
        finally { busy = false; }
    });

    document.addEventListener('click', async event => {
        // Intercept only inbox controls; let the browser handle new tabs and original articles.
        if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        const link = event.target.closest('aside a.feed, .toolbar > a, a.heading-filter, .date-clear, .clear-search, #more');
        if (!link) return;
        event.preventDefault();
        if (link.id !== 'more') { navigate(link.href); return; }
        if (busy || navigation) return;
        busy = true;
        try { await loadMore(link); }
        catch (error) { showError(error); }
        finally { busy = false; }
    });

    document.addEventListener('submit', async event => {
        const form = event.target;
        if (form.closest('.date-filter, .archive-search, .state-filter')) {
            event.preventDefault();
            const params = new URLSearchParams(new FormData(form));
            if (form.closest('.state-filter')) {
                // Keep the checkbox pairs independent and store the result in the view URL.
                const flags = params.getAll('state').reduce((mask, value) => mask | Number(value), 0);
                params.delete('state');
                params.delete('apply-flags');
                if (flags !== 5) params.set('flags', flags);
            }
            // Changing dates starts a new reading stream; its first URL has no pagination cursor.
            for (const bound of ['from', 'to', 'q']) if (!params.get(bound)?.trim()) params.delete(bound);
            navigate(form.action + '?' + params);
            return;
        }
        const action = form.getAttribute('action');
        if (!['/check-feed', '/visit-feed', '/check', '/check-all', '/read', '/mark-feed-read', '/undo-feed-read', '/source', '/rss-source', '/group', '/collection', '/delete-read', '/feed-settings', '/delete-feed', '/import-opml'].includes(action)) return;
        event.preventDefault();
        if (busy || navigation) return;
        if (form.closest('.feed-menu')) {
            menuButton?.focus({ preventScroll: true });
            closeFeedMenu();
        }
        if (action === '/check-all') { await checkAll(form); return; }
        if (action === '/check-feed' || action === '/visit-feed' || action === '/check') { await checkFeed(form); return; }

        const button = event.submitter || form.querySelector('button');
        const fields = new URLSearchParams(new FormData(form));
        if (button?.name) fields.set(button.name, button.value);
        // Match the toolbar's arrival cutoff so newly arriving articles remain unread.
        if (action === '/mark-feed-read' && fields.has('target-feed')) fields.set('through', entries.dataset.arrival);
        if (action === '/delete-read') {
            const count = button.dataset.deleteCount;
            if (!window.confirm(`Delete ${count} read articles from all feeds and compact the database? This cannot be undone.`)) return;
            fields.set('confirmed', '1');
        }
        busy = true;
        if (button) button.disabled = true;
        const view = entries;
        try {
            if (action === '/read') {
                const row = form.closest('article[data-post]');
                if (fields.get('mark') === 'unread') await setRead(row, false);
                else await readAndAdvance(row);
            } else {
                if (action === '/import-opml') {
                    const file = form.elements['opml-file'].files[0];
                    if (!file || file.size > 1024 * 1024) throw new Error('Choose an OPML file up to 1 MiB.');
                    const bytes = new Uint8Array(await file.arrayBuffer());
                    let binary = '';
                    for (let i = 0; i < bytes.length; i += 8192) binary += String.fromCharCode(...bytes.subarray(i, i + 8192));
                    fields.delete('opml-file');
                    // Keep the original bytes so the XML parser can honor the file's encoding.
                    fields.set('opml', btoa(binary));
                }
                // Named controls can shadow form.action; use the route read from the attribute.
                const response = await fetch(action, { method: 'POST', body: fields, headers: visitHeaders() });
                const page = await pageFrom(response);
                if (entries === view && !navigation) {
                    acceptVisitCount(response);
                    const nextUrl = new URL(response.url);
                    // A receipt or pause toggle is not a new browser history entry.
                    const mode = action === '/group' || action === '/rss-source' ? 'push' : 'preserve';
                    if (mode === 'preserve' && nextUrl.href !== location.href) history.replaceState(null, '', nextUrl.href);
                    applyView(page, nextUrl.href, mode, action === '/group');
                }
            }
        } catch (error) {
            const editorError = form.closest('.feed-editor')?.querySelector('.feed-editor-error');
            if (editorError) editorError.textContent = error.message;
            else showError(error);
        }
        finally { if (button) button.disabled = false; busy = false; }
    });

    const readerInsetKey = 'feedbox.reader-inset';
    let preferredInset = 22;
    try {
        const saved = Number(localStorage.getItem(readerInsetKey));
        if (Number.isFinite(saved) && saved >= 8) preferredInset = saved;
    } catch { /* The card edges still work when browser storage is unavailable. */ }
    let readerInset = preferredInset;
    let readerInsetMax = preferredInset;
    let edgeDrag;

    function syncReaderEdges() {
        for (const handle of main.querySelectorAll('.reader-resize')) {
            handle.setAttribute('aria-valuenow', readerInset);
            handle.setAttribute('aria-valuemax', readerInsetMax);
        }
    }

    function setReaderInset(inset, save = false) {
        // Allow narrow columns while leaving some usable text width after all four gaps.
        const sidebar = parseFloat(getComputedStyle(main).getPropertyValue('--reader-sidebar'));
        const pane = document.documentElement.clientWidth - sidebar;
        const minimumText = Math.min(180, pane / 2);
        readerInsetMax = Math.max(8, Math.floor((pane - minimumText) / 4));
        readerInset = Math.round(Math.min(readerInsetMax, Math.max(8, inset)));
        main.style.setProperty('--reader-inset', `${readerInset}px`);
        syncReaderEdges();
        if (save) {
            preferredInset = readerInset;
            try { localStorage.setItem(readerInsetKey, String(readerInset)); } catch { /* Keep the current inset. */ }
        }
    }

    // Keep dragging when the pointer leaves the edge or the reading view is replaced.
    main.addEventListener('pointerdown', event => {
        const edge = event.target.closest('.reader-resize');
        if (!edge || event.button !== 0 || edgeDrag) return;
        event.preventDefault();
        edgeDrag = { pointer: event.pointerId, x: event.clientX, inset: readerInset, direction: edge.dataset.edge === 'left' ? 1 : -1 };
        main.setPointerCapture(event.pointerId);
        document.body.classList.add('resizing-reader');
    });
    const moveReaderEdge = (event, save = false) => {
        if (edgeDrag?.pointer !== event.pointerId) return;
        setReaderInset(edgeDrag.inset + (event.clientX - edgeDrag.x) * edgeDrag.direction, save);
    };
    main.addEventListener('pointermove', moveReaderEdge);
    main.addEventListener('pointerup', event => moveReaderEdge(event, true));
    main.addEventListener('lostpointercapture', event => {
        if (edgeDrag?.pointer !== event.pointerId) return;
        edgeDrag = null;
        document.body.classList.remove('resizing-reader');
        setReaderInset(preferredInset);
    });
    main.addEventListener('keydown', event => {
        const edge = event.target.closest('.reader-resize');
        if (!edge || !['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
        event.preventDefault();
        const movingRight = event.key === 'ArrowRight';
        setReaderInset(readerInset + (movingRight === (edge.dataset.edge === 'left') ? 4 : -4), true);
    });

    const splitter = document.querySelector('.sidebar-resize');
    const widthKey = 'feedbox.sidebar-width';
    const maxWidth = () => Math.max(160, Math.min(560, innerWidth - 330));
    let preferred = 272;
    try {
        const saved = Number(localStorage.getItem(widthKey));
        if (saved > 0 && Number.isFinite(saved)) preferred = saved;
    } catch { /* Resizing still works when storage is unavailable. */ }

    function sidebarWidth(width, save = false) {
        const actual = Math.round(Math.min(maxWidth(), Math.max(160, width)));
        document.documentElement.style.setProperty('--sidebar-width', `${actual}px`);
        splitter.setAttribute('aria-valuenow', actual);
        splitter.setAttribute('aria-valuemax', maxWidth());
        setReaderInset(preferredInset);
        if (save) {
            preferred = actual;
            try { localStorage.setItem(widthKey, String(actual)); } catch { /* Keep the current width. */ }
        }
    }
    // Keep the chosen width when a smaller window temporarily clamps it.
    sidebarWidth(preferred);
    window.addEventListener('resize', () => sidebarWidth(preferred));
    splitter.addEventListener('pointerdown', event => {
        if (event.button !== 0) return;
        event.preventDefault();
        splitter.setPointerCapture(event.pointerId);
        document.body.classList.add('resizing-sidebar');
    });
    splitter.addEventListener('pointermove', event => {
        if (splitter.hasPointerCapture(event.pointerId)) sidebarWidth(event.clientX);
    });
    splitter.addEventListener('pointerup', event => {
        if (splitter.hasPointerCapture(event.pointerId)) sidebarWidth(event.clientX, true);
    });
    splitter.addEventListener('lostpointercapture', () => document.body.classList.remove('resizing-sidebar'));
    splitter.addEventListener('keydown', event => {
        if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
        event.preventDefault();
        sidebarWidth(Number(splitter.getAttribute('aria-valuenow')) + (event.key === 'ArrowLeft' ? -12 : 12), true);
    });

    const collapseKey = 'feedbox.collapsed-groups';
    let collapsed = new Set();
    try { collapsed = new Set(JSON.parse(localStorage.getItem(collapseKey) || '[]')); }
    catch { /* Use the expanded view when saved state is unavailable. */ }
    function showGroup(toggle, expanded) {
        const id = toggle.getAttribute('aria-controls');
        document.getElementById(id).hidden = !expanded;
        toggle.setAttribute('aria-expanded', String(expanded));
    }

    function restoreGroups() {
        // Show all overrides saved collapses temporarily. Otherwise keep a selected feed visible.
        for (const toggle of nav.querySelectorAll('.group-toggle')) {
            const id = toggle.getAttribute('aria-controls');
            toggle.disabled = showAll.checked;
            showGroup(toggle, showAll.checked || !collapsed.has(id) || Boolean(document.getElementById(id).querySelector('[aria-current=page]')));
        }
    }

    const sidebarTools = document.querySelector('.sidebar-tools');
    const feedSort = sidebarTools.querySelector('.feed-sort');
    const showAll = sidebarTools.querySelector('.show-all-feeds');
    const showAllKey = 'feedbox.show-all-feeds';
    // Close the options panel on outside clicks or Escape, without changing the reading view.
    document.addEventListener('click', event => {
        if (sidebarTools.open && !sidebarTools.contains(event.target)) sidebarTools.open = false;
    });
    document.addEventListener('keydown', event => {
        if (event.key !== 'Escape' || event.defaultPrevented || !sidebarTools.open) return;
        sidebarTools.open = false;
        if (sidebarTools.contains(event.target)) sidebarTools.querySelector('summary').focus({ preventScroll: true });
    });
    const feedSortKey = 'feedbox.feed-sort';
    const originalFeedOrder = new WeakMap();
    const countFeedOrder = new Map();
    let sidebarPinTargets = [];
    const feedNames = new Intl.Collator(undefined, { sensitivity: 'base' });
    try {
        const mode = localStorage.getItem(feedSortKey);
        if (['name', 'group-count', 'all-count'].includes(mode)) feedSort.value = mode;
        showAll.checked = localStorage.getItem(showAllKey) === '1';
    } catch { /* Sorting still works when browser storage is unavailable. */ }

    // Show all needs groups even if the usual view is flat. Keep the saved sort for when it is unchecked.
    const flatFeedSort = () => !showAll.checked && feedSort.value === 'all-count';

    function applyFeedSort() {
        syncGroupedSelection();
        const flat = flatFeedSort();
        for (const section of nav.querySelectorAll(':scope > .feed-section-label, #ungrouped-feeds')) section.hidden = flat;
        const existing = nav.querySelector('#flat-feeds');
        if (!flat) { existing?.remove(); return; }
        if (existing) return;

        // Keep memberships for editing; only real feed links belong in the flat list.
        const list = document.createElement('div');
        list.className = 'ungrouped-feeds';
        list.id = 'flat-feeds';
        const activeFeed = new URL(displayedUrl).searchParams.get('feed');
        const seen = new Set();
        for (const original of nav.querySelectorAll('.feed-row > a.feed[data-feed]')) {
            const source = original.dataset.feed;
            if (seen.has(source)) continue;
            seen.add(source);
            const copy = original.parentElement.cloneNode(true);
            const link = copy.querySelector('a.feed');
            const url = new URL(link.href);
            url.searchParams.delete('group');
            link.href = url.href;
            link.dataset.group = '0';
            link.draggable = false;
            if (source === activeFeed) link.setAttribute('aria-current', 'page');
            else link.removeAttribute('aria-current');
            list.append(copy);
        }
        const more = document.createElement('button');
        more.type = 'button';
        more.className = 'more-feeds';
        list.append(more);
        nav.append(list);
    }

    function activeFeedLinks() {
        // Both lists receive badge updates, but only the displayed list drives navigation.
        const selector = flatFeedSort() ? '#flat-feeds a.feed' : 'a.feed[data-feed]';
        return [...nav.querySelectorAll(selector)];
    }

    function syncGroupedSelection() {
        const view = new URL(displayedUrl).searchParams;
        const feed = view.get('feed');
        const group = view.get('group') || '0';
        const links = [...nav.querySelectorAll('.feed-group .feed-row > a.feed, #ungrouped-feeds .feed-row > a.feed')];
        const members = feed ? links.filter(link => link.dataset.feed === feed) : [];
        // A feed opened from the flat list has no group in its URL. Use its first membership.
        const current = members.find(link => link.dataset.group === group) || members[0];
        for (const link of links) {
            if (link === current) link.setAttribute('aria-current', 'page');
            else link.removeAttribute('aria-current');
        }
        for (const link of nav.querySelectorAll('.group-link'))
            link.toggleAttribute('data-active-group', link.dataset.group === (feed ? current?.dataset.group : group));
    }

    const hasMatchingArticles = link => Number(link.querySelector('.count').textContent) > 0;

    function sidebarCountScope(url, flags) {
        const params = new URL(url).searchParams;
        return JSON.stringify([flags, ...['from', 'to', 'q'].map(key => params.get(key) || '')]);
    }

    function sidebarFeedSections() {
        const flat = flatFeedSort();
        return [...nav.querySelectorAll('.group-members, .ungrouped-feeds')].filter(section => flat === (section.id === 'flat-feeds'));
    }

    function sortSidebarFeeds() {
        for (const section of sidebarFeedSections()) sortFeedRows(section, section.querySelector('.more-feeds'));
    }

    const feedRowKey = row => row.querySelector('a.feed').dataset.feed;
    const feedRowCount = row => Number(row.querySelector('.count').textContent);
    const feedRanks = rows => new Map(rows.map((row, i) => [feedRowKey(row), i]));

    function sortFeedRows(section, more) {
        const order = [...section.querySelectorAll(':scope > .feed-row')];
        if (!originalFeedOrder.has(section)) originalFeedOrder.set(section, new Map(order.map((row, i) => [row, i])));
        const original = originalFeedOrder.get(section);
        const byCount = feedSort.value !== 'name';
        const state = byCount ? countFeedOrder.get(section.id) : null;
        const previous = state?.ranks;
        // Restore saved positions when navigation replaces the sidebar; append newly added rows.
        const rank = row => previous ? previous.get(feedRowKey(row)) ?? previous.size : 0;
        const label = row => row.querySelector('.feed-label').textContent;
        const sorted = [...order].sort((a, b) => rank(a) - rank(b) || (byCount ? feedRowCount(b) - feedRowCount(a) : 0)
            || (section.id === 'flat-feeds' ? feedNames.compare(label(a), label(b)) : original.get(a) - original.get(b)));
        if (byCount) countFeedOrder.set(section.id, { ranks: feedRanks(sorted), hidden: state?.hidden });
        if (sorted.every((row, i) => row === order[i])) return;

        // Move only out-of-order rows, retaining focus and keeping More feeds at the end.
        const focused = section.contains(document.activeElement) ? document.activeElement : null;
        let next = more;
        for (let i = sorted.length - 1; i >= 0; i--) {
            const row = sorted[i];
            if (row.nextElementSibling !== next) section.insertBefore(row, next);
            next = row;
        }
        if (focused && document.activeElement !== focused) focused.focus({ preventScroll: true });
    }

    function placeReturningFeeds(section, more, returning) {
        const rows = [...section.querySelectorAll(':scope > .feed-row')];
        const visible = rows.filter(row => !row.hidden && !returning.has(row));
        // Insert before the first smaller count; the other rows keep their reading order even if their counts drifted.
        for (const row of [...returning].sort((a, b) => feedRowCount(b) - feedRowCount(a))) {
            const count = feedRowCount(row);
            const index = visible.findIndex(other => feedRowCount(other) < count);
            if (index < 0) {
                section.insertBefore(row, more);
                visible.push(row);
            } else {
                section.insertBefore(row, visible[index]);
                visible.splice(index, 0, row);
            }
        }
        return feedRanks([...section.querySelectorAll(':scope > .feed-row')]);
    }

    const expandedMoreFeeds = new Set();
    function updateMoreFeeds() {
        // Show all affects sidebar visibility only; the badges still reflect the current article filters.
        const flat = flatFeedSort();
        // Keep red feed names discoverable even when their counts and their group's count are zero.
        const redFeed = '.feed[data-feed].has-check-warning:not(.feed-paused)';
        for (const group of nav.querySelectorAll(':scope > .feed-group')) {
            group.hidden = flat || (!showAll.checked && Number(group.querySelector('.group-link .count').textContent) === 0
                && !group.querySelector(redFeed));
        }
        // Remember feeds hidden for having no matching articles, separately from manually collapsed groups.
        for (const section of sidebarFeedSections()) {
            const more = section.querySelector('.more-feeds');
            const expanded = expandedMoreFeeds.has(section.id);
            const state = feedSort.value === 'name' ? null : countFeedOrder.get(section.id);
            const hidden = new Set();
            const returning = new Set();
            const groupHidden = section.closest('.feed-group')?.hidden;
            let zeroUnread = 0;
            for (const link of section.querySelectorAll('.feed[data-feed]')) {
                // Warning feeds stay out of the More feeds bucket until the red status clears.
                const hasArticles = hasMatchingArticles(link);
                const tuckAway = !hasArticles && !link.matches(redFeed);
                link.hidden = tuckAway && !expanded && !showAll.checked;
                const row = link.parentElement;
                row.hidden = link.hidden;
                if (tuckAway) zeroUnread++;
                if (!state) continue;
                if (!hasArticles && (link.hidden || groupHidden)) hidden.add(link.dataset.feed);
                if (hasArticles && state.hidden?.has(link.dataset.feed)) returning.add(row);
            }
            if (state) {
                if (returning.size) state.ranks = placeReturningFeeds(section, more, returning);
                state.hidden = hidden;
            }
            more.hidden = showAll.checked || zeroUnread === 0;
            more.setAttribute('aria-expanded', String(expanded && zeroUnread > 0));
            more.textContent = expanded ? 'Show fewer feeds' : `${zeroUnread} more ${zeroUnread === 1 ? 'feed' : 'feeds'}`;
        }
        syncSidebarPins();
        showVisitCount();
    }

    showAll.closest('label').hidden = false;
    feedSort.closest('label').hidden = false;
    applyFeedSort();
    restoreGroups();
    sortSidebarFeeds();
    updateMoreFeeds();
    nav.addEventListener('scroll', updateSidebarPinVisibility, { passive: true });
    new ResizeObserver(() => {
        // Align the header counter and menu with the list whether the scrollbar overlays it or takes space.
        sidebar.style.setProperty('--sidebar-scrollbar', `${nav.offsetWidth - nav.clientWidth}px`);
        updateSidebarPinVisibility();
    }).observe(nav);
    showAll.addEventListener('change', () => {
        closeFeedMenu();
        try { localStorage.setItem(showAllKey, showAll.checked ? '1' : '0'); } catch { /* Use the choice for this tab. */ }
        applyFeedSort();
        restoreGroups();
        sortSidebarFeeds();
        updateMoreFeeds();
        revealCurrentFeed();
    });
    feedSort.addEventListener('change', () => {
        closeFeedMenu();
        countFeedOrder.clear();
        try { localStorage.setItem(feedSortKey, feedSort.value); } catch { /* Use the choice for this tab. */ }
        applyFeedSort();
        restoreGroups();
        sortSidebarFeeds();
        updateMoreFeeds();
        revealCurrentFeed();
    });
    nav.addEventListener('click', event => {
        const more = event.target.closest('.more-feeds');
        if (more) {
            // Keep each section's setting when the reader switches views within the page.
            const id = more.parentElement.id;
            if (!expandedMoreFeeds.delete(id)) expandedMoreFeeds.add(id);
            updateMoreFeeds();
            return;
        }
        const toggle = event.target.closest('.group-toggle');
        if (!toggle) return;
        const id = toggle.getAttribute('aria-controls');
        const expanded = document.getElementById(id).hidden;
        showGroup(toggle, expanded);
        if (expanded) collapsed.delete(id);
        else collapsed.add(id);
        try { localStorage.setItem(collapseKey, JSON.stringify([...collapsed])); } catch { /* Keep the current view. */ }
        updateSidebarPinVisibility();
    });

    const onScreen = row => {
        const rect = row.getBoundingClientRect();
        // Do not mark a row whose remaining pixels are entirely behind the sticky header.
        return rect.bottom > header.getBoundingClientRect().bottom && rect.top < window.innerHeight;
    };

    function setReaderTail(pixels) {
        if (pixels === readerTail) return;
        readerTail = pixels;
        main.style.setProperty('--reader-tail', `${pixels}px`);
    }

    function alignArticle(row) {
        // Align under the full sticky header, including any status message it displays.
        const scroll = window.scrollY;
        const top = Math.max(0, Math.round(scroll + row.getBoundingClientRect().top - header.offsetHeight - 8));
        // Reserve just enough trailing space for a short final article to reach that line.
        const bottom = scroll + main.getBoundingClientRect().bottom - readerTail;
        setReaderTail(Math.max(0, Math.ceil(top + document.scrollingElement.clientHeight - bottom)));
        window.scrollTo(0, top);
    }

    function select(row, focus = true) {
        if (!row) return;
        // Keep selection separate from the stored read state.
        selected?.removeAttribute('data-current');
        selected = row;
        row.setAttribute('data-current', 'true');
        try { localStorage.setItem(readingKey(displayedUrl), row.dataset.post); } catch { /* The current view still works. */ }
        if (focus) row.focus({ preventScroll: true });
        alignArticle(row);
    }

    // Clicking a different article selects it; leave focus on any link or button the reader clicked.
    main.addEventListener('click', event => {
        const row = event.target.closest('article[data-post]');
        if (row && event.target.closest('.article-bookmark')) {
            if (busy || navigation) return;
            if (row !== selected) select(row, false);
            toggleArticleMark(row);
            return;
        }
        if (row && row !== selected) select(row, false);
    });
    main.addEventListener('error', event => {
        if (event.target.matches('img.preview')) event.target.hidden = true;
    }, true);

    function moveFeed(direction) {
        // Keyboard feed switching follows visible sidebar entries.
        const all = activeFeedLinks().filter(link => link.dataset.feed);
        const current = all.findIndex(link => link.getAttribute('aria-current') === 'page');
        const unread = document.body.dataset.unread !== '0';
        const folder = flatFeedSort() ? null : nav.querySelector('.group-link[aria-current=page]');
        const groupFeeds = folder ? all.filter(link => link.dataset.group === folder.dataset.group) : [];
        const groupStart = direction > 0 ? groupFeeds[0] : groupFeeds.at(-1);
        let index = current >= 0 ? current + direction : groupStart ? all.indexOf(groupStart) : direction > 0 ? 0 : all.length - 1;
        // In Unread, skip sources with no unread posts. View all also makes read-only feeds reachable.
        while (index >= 0 && index < all.length) {
            const link = all[index];
            if (!link.closest('[hidden]') && (!unread || hasMatchingArticles(link))) {
                navigate(link.href);
                return;
            }
            index += direction;
        }
        status.textContent = 'No more feeds in this direction.';
    }

    async function loadMore(link) {
        const list = entries;
        const response = await fetch(link.href, { cache: 'no-store' });
        if (!response.ok) throw new Error('Could not load more posts.');
        const nextPage = new DOMParser().parseFromString(await response.text(), 'text/html');
        const nextEntries = nextPage.querySelector('#entries');
        if (!nextEntries) throw new Error('Could not load more posts.');
        if (entries !== list || navigation) return;
        // Continue the same reading stream so K can return to a row on the earlier page.
        const lastDay = Array.from(entries.querySelectorAll('h2')).at(-1);
        if (nextEntries.firstElementChild?.tagName === 'H2' && nextEntries.firstElementChild.textContent === lastDay?.textContent)
            nextEntries.firstElementChild.remove();
        const appended = [...nextEntries.children];
        entries.append(...appended);
        for (const row of appended) observePublisherPreview(row);
        const more = nextPage.querySelector('#more');
        if (more) link.setAttribute('href', more.getAttribute('href'));
        else link.remove();
        setReaderTail(0);
    }

    async function setRead(row, read) {
        if ((row.dataset.read === '1') === read || (read && row.dataset.marked === '1')) return;
        // Persist the action first. Leave the row in place for K or undo without disturbing the scroll position.
        const form = row.querySelector('form[action="/read"]');
        const fields = new URLSearchParams(new FormData(form));
        fields.set('inline', '1');
        fields.set('mark', read ? 'read' : 'unread');
        const response = await fetch(form.action, { method: 'POST', body: fields, headers: visitHeaders() });
        if (response.status !== 204) throw new Error(await response.text() || 'Could not update this post.');
        acceptVisitCount(response);
        updateArticleState(row, read, row.dataset.marked === '1');
    }

    async function readAndAdvance(row) {
        // Both forward actions load the next page if needed and wait for the read to save.
        status.textContent = '';
        const articles = rows();
        const index = articles.indexOf(row);
        const more = document.querySelector('#more');
        if (!articles[index + 1] && more) await loadMore(more);
        if (!row.isConnected || navigation) return;
        await setRead(row, true);
        if (!row.isConnected || navigation) return;

        const next = rows()[index + 1];
        if (next) select(next);
        else {
            status.textContent = 'End of this feed.';
            alignArticle(row);
        }
    }

    function inView(read, marked) {
        const flags = Number(document.body.dataset.flags);
        return Boolean((flags & (read ? 2 : 1)) && (flags & (marked ? 8 : 4)));
    }

    function updateArticleState(row, read, marked) {
        readRevision++;
        if (!row.isConnected) return;
        const wasRead = row.dataset.read === '1';
        const wasMarked = row.dataset.marked === '1';
        const delta = Number(inView(read, marked)) - Number(inView(wasRead, wasMarked));
        // The archive breakdown changes even when the article still matches the reading filter.
        if (read !== wasRead || marked !== wasMarked) {
            const count = field => Number(header.querySelector(`.heading-counts [data-count=${field}]`).textContent);
            const total = count('total');
            const readCount = Math.max(0, Math.min(total, count('read') + Number(read) - Number(wasRead)));
            const markedCount = Math.max(0, Math.min(total, count('marked') + Number(marked) - Number(wasMarked)));
            updateHeadingCounts({ marked: markedCount, read: readCount, total });
        }
        row.dataset.read = read ? '1' : '0';
        row.dataset.marked = marked ? '1' : '0';
        const bookmark = row.querySelector('.article-bookmark');
        bookmark.setAttribute('aria-pressed', String(marked));
        bookmark.title = marked ? 'Unmark article' : 'Mark article';
        // The checkmark displays the read state and remains inactive for protected articles.
        const button = row.querySelector('.article-read');
        button.value = read ? 'unread' : 'read';
        button.setAttribute('aria-pressed', String(read));
        button.title = marked ? 'Marked articles stay unread' : read ? 'Mark unread' : 'Mark read';
        button.disabled = marked;
        if (!delta) { showVisitCount(); return; }
        for (const feed of document.querySelectorAll('.feed[data-feed]')) {
            if (feed.dataset.feed && feed.dataset.feed !== row.dataset.source) continue;
            // A post contributes once to the total and once to each folder containing its feed.
            if (!feed.dataset.feed && feed.dataset.group !== '0') {
                const members = [...feed.closest('.feed-group').querySelectorAll('.feed[data-feed]')];
                if (!members.some(member => member.dataset.feed === row.dataset.source)) continue;
            }
            const count = feed.querySelector('.count');
            count.textContent = Math.max(0, Number(count.textContent) + delta);
        }
        updateMoreFeeds();
    }

    async function changeMark(row, action) {
        const fields = new URLSearchParams(new FormData(row.querySelector('form[action="/read"]')));
        fields.set('action', action);
        const response = await fetch('/mark-article', { method: 'POST', body: fields, headers: visitHeaders() });
        if (!response.ok) throw new Error(await response.text() || 'Could not update the mark.');
        const state = await response.json();
        acceptVisitCount(response);
        updateArticleState(row, Boolean(state.read), Boolean(state.marked));
    }

    async function toggleArticleMark(row) {
        // The bookmark and B share the same write and advance rules.
        status.textContent = '';
        busy = true;
        try {
            await changeMark(row, 'toggle');
            // Keep the target when both states are visible so the next toggle reverses it.
            if (row.isConnected && !navigation && !inView(row.dataset.read === '1', row.dataset.marked === '1')) await advanceAfterMark(row);
        } catch (error) { showError(error); }
        finally { busy = false; }
    }

    async function advanceAfterMark(row) {
        let articles = rows();
        const index = articles.indexOf(row);
        const more = document.querySelector('#more');
        if (!articles[index + 1] && more) {
            await loadMore(more);
            articles = rows();
        }
        if (!row.isConnected || navigation) return;
        // Keep the previous card available to K, even if its new state doesn't match the filter.
        const next = articles[index + 1];
        if (next) select(next);
        else {
            alignArticle(row);
            status.textContent = 'End of this view.';
        }
    }

    const previewObserver = new IntersectionObserver(changes => {
        for (const change of changes) {
            if (!change.isIntersecting || !change.target.isConnected) continue;
            previewObserver.unobserve(change.target);
            loadPublisherPreview(change.target);
        }
    });

    function observePublisherPreview(row) {
        if (row.matches('article[data-publisher-preview]') && !row.querySelector('iframe.rss-body')) previewObserver.observe(row);
    }

    function observePublisherPreviews() {
        for (const row of entries.querySelectorAll('article[data-publisher-preview]')) observePublisherPreview(row);
    }

    async function loadPublisherPreview(row) {
        // Each mounted card gets one lookup. J/K keeps the card and its image in place.
        if (!row.hasAttribute('data-publisher-preview')) return;
        row.removeAttribute('data-publisher-preview');
        try {
            const fields = new URLSearchParams({ id: row.dataset.post, token: row.querySelector('form[action="/read"] input[name="token"]').value });
            const response = await fetch('/publisher-preview', { method: 'POST', body: fields });
            if (response.status !== 200) return;
            const url = new URL(await response.text());
            if (!row.isConnected || row.querySelector('img.preview') || !['https:', 'http:'].includes(url.protocol)) return;
            const image = document.createElement('img');
            image.className = 'preview publisher-preview';
            image.alt = 'Publisher preview';
            image.referrerPolicy = 'no-referrer';
            image.loading = 'lazy';
            image.addEventListener('error', () => image.remove(), { once: true });
            image.src = url.href;
            const inner = row.querySelector('.article-inner');
            inner.insertBefore(image, inner.querySelector('iframe.rss-body, .context'));
        } catch { /* Missing metadata or a failed request leaves the feed article as it was. */ }
    }

    function mountRssBody(frame) {
        const doc = frame.contentDocument;
        const payload = doc?.getElementById('rss-payload');
        const body = doc?.getElementById('rss-content');
        if (!payload || !body) return;
        // Parse in an inert template so no publisher nodes load or run before inspection.
        const template = doc.createElement('template');
        template.innerHTML = payload.value;
        payload.remove();
        for (const element of template.content.querySelectorAll('script, iframe, frame, object, embed, base, meta[http-equiv], link[rel=import]')) element.remove();
        for (const element of template.content.querySelectorAll('*')) {
            for (const attribute of [...element.attributes]) {
                if (attribute.localName.toLowerCase().startsWith('on')) element.removeAttributeNode(attribute);
            }
            if (element.localName !== 'a' && element.localName !== 'area') continue;
            // Ordinary links leave the sandbox in a new tab; executable URLs stay as text.
            for (const attr of [...element.attributes].filter(a => a.localName === 'href')) {
                try {
                    const target = new URL(attr.value, doc.baseURI);
                    if (/^(javascript|vbscript|data):$/.test(target.protocol)) element.removeAttributeNode(attr);
                } catch { element.removeAttributeNode(attr); }
            }
            element.setAttribute('target', '_blank');
            element.setAttribute('rel', 'noopener noreferrer');
            element.removeAttribute('ping');
        }
        body.append(template.content);
        // Give images already present in the RSS body priority over a publisher lookup.
        const row = frame.closest('article[data-publisher-preview]');
        if (row) {
            if (body.querySelector('img[src], img[srcset], video[poster]')) row.removeAttribute('data-publisher-preview');
            else previewObserver.observe(row);
        }
        // Let long text and late-loading images size the frame without creating an inner scroller.
        const fit = () => { frame.style.height = `${Math.max(1, Math.ceil(body.getBoundingClientRect().height))}px`; };
        new ResizeObserver(fit).observe(body);
        fit();
        doc.addEventListener('keydown', event => handleReaderKey(event, frame.closest('article[data-post]')));
    }

    document.addEventListener('load', event => {
        if (event.target.matches?.('iframe.rss-body')) mountRssBody(event.target);
    }, true);
    // A cached body may have loaded while the deferred script was starting.
    for (const frame of document.querySelectorAll('iframe.rss-body')) mountRssBody(frame);
    observePublisherPreviews();

    async function handleReaderKey(event, frameRow = null) {
        if (event.defaultPrevented || event.repeat || event.isComposing || event.altKey || event.ctrlKey || event.metaKey
            || event.target.closest?.('input,textarea,select,[contenteditable=true],.feed-editor,.feed-menu')) return;
        const key = event.key.toLowerCase();
        const mark = key === 'b' && !event.shiftKey;
        if ((!mark && !['j', 'k', 'v'].includes(key)) || (key === 'v' && event.shiftKey)) return;
        event.preventDefault();
        if (busy || navigation) return;
        status.textContent = '';
        if (!mark && event.shiftKey) { moveFeed(key === 'j' ? 1 : -1); return; }

        const articles = rows();
        const current = frameRow && onScreen(frameRow) ? frameRow : selected && onScreen(selected) ? selected : null;
        // After a mouse scroll or on a fresh page, target a visible row without marking unseen rows.
        if (!current) select(articles.find(onScreen) || articles[0]);
        else if (current !== selected) select(current, false);
        if (!selected) return;
        if (mark) { await toggleArticleMark(selected); return; }
        if (key === 'v') {
            const row = selected;
            busy = true;
            try {
                const target = row.dataset.discussion || row.querySelector('a.original')?.href;
                if (!target) return;
                // Keep a handle just long enough to tell whether the browser allowed the tab to open.
                const tab = window.open('about:blank', '_blank');
                if (!tab) { status.textContent = 'Allow pop-ups to open this article.'; return; }
                tab.opener = null;
                tab.location.replace(target);
                await changeMark(row, 'opened');
                // Like J, keep the opened card in the current list until the view is replaced.
                if (row.isConnected && !navigation && !inView(row.dataset.read === '1', row.dataset.marked === '1'))
                    await advanceAfterMark(row);
            } catch (error) { showError(error); }
            finally { busy = false; }
            return;
        }
        if (!current) return;
        if (key === 'k') { select(articles[articles.indexOf(current) - 1]); return; }

        busy = true;
        try {
            await readAndAdvance(current);
        } catch (error) {
            status.textContent = error.message;
        } finally {
            busy = false;
        }
    }
    document.addEventListener('keydown', handleReaderKey);

    function syncSidebarPins() {
        const view = new URL(displayedUrl);
        const group = nav.querySelector('.feed-group:not([hidden]) .group-link[data-active-group]');
        const source = view.searchParams.get('feed');
        const links = source ? activeFeedLinks().filter(link => link.dataset.feed === source) : [];
        const selected = links.find(link => link.getAttribute('aria-current') === 'page');
        const feed = selected || links[0];
        // A feed opened without a group still has a shortcut when switching back to grouped sorting.
        const feedUrl = feed && new URL(feed.href);
        if (feedUrl && !selected && !view.searchParams.get('group')) feedUrl.searchParams.delete('group');
        sidebarPinTargets = [
            { pin: nav.querySelector('.pinned-group'), link: group, href: group?.href },
            { pin: nav.querySelector('.pinned-feed'), link: feed, href: feedUrl?.href },
        ];
        for (const { pin, link, href } of sidebarPinTargets) syncPinnedLink(pin, link, href);
        updateSidebarPinVisibility();
    }

    function syncPinnedLink(pin, link, href) {
        if (!link) { pin.hidden = true; return; }
        let shortcut = pin.querySelector('a.feed');
        if (shortcut?.href !== href) {
            // Copy the feed's options button as well as its link so both rows use the same menu.
            const row = link.dataset.feed ? link.closest('.feed-row').cloneNode(true) : null;
            shortcut = row ? row.querySelector('a.feed') : link.cloneNode(true);
            if (row) {
                row.hidden = false;
                row.querySelector('.feed-options').setAttribute('aria-expanded', 'false');
            }
            // The original link remains the source of counts and keyboard navigation.
            shortcut.removeAttribute('aria-current');
            shortcut.removeAttribute('data-active-group');
            shortcut.removeAttribute('aria-describedby');
            shortcut.hidden = false;
            if (shortcut.dataset.feed) shortcut.dataset.pinnedFeed = shortcut.dataset.feed;
            delete shortcut.dataset.feed;
            shortcut.draggable = false;
            shortcut.href = href;
            pin.replaceChildren(row || shortcut);
        }
        // The parent group can be pinned for context without becoming the selected view.
        shortcut.classList.toggle('is-current-pin', link.getAttribute('aria-current') === 'page');
        const count = link.querySelector('.count').textContent;
        const badge = shortcut.querySelector('.count');
        if (badge.textContent !== count) badge.textContent = count;
        setFeedCheckState(shortcut, link.classList.contains('has-check-warning'), link.classList.contains('feed-paused'));
        // An icon may arrive after the shortcut was first cloned.
        if (link.dataset.feed && link.querySelector('.feed-site-icon:not([hidden])')) addFeedIcon(shortcut, link.dataset.feed);
    }

    function updateSidebarPinVisibility() {
        const targets = sidebarPinTargets.filter(({ link }) => link);
        if (!targets.length) return;
        const block = nav.querySelector('.feed-pins');
        // The overview is outside the scroll area; only the optional shortcuts take space here.
        const rowHeight = parseFloat(getComputedStyle(targets[0].pin.querySelector('a.feed')).minHeight);
        const extra = targets.reduce((sum, { pin }) => sum + pin.getBoundingClientRect().height, 0);
        const baseHeight = block.getBoundingClientRect().height - extra;
        const navTop = nav.getBoundingClientRect().top + nav.clientTop;
        const navBottom = navTop + nav.clientHeight;
        const scroll = nav.scrollTop;
        const rows = targets.map(({ link }) => link.closest('[hidden]') ? null : link.getBoundingClientRect());

        // Adding a pin shifts normal rows down. Choose the fewest pins whose projected layout
        // leaves the other normal rows visible, rather than measuring only the current layout.
        const combinations = 1 << targets.length;
        let mask = 0;
        for (; mask < combinations - 1; mask++) {
            const height = (Number(Boolean(mask & 1)) + Number(Boolean(mask & 2))) * rowHeight;
            const nextScroll = Math.min(scroll, Math.max(0, nav.scrollHeight - extra + height - nav.clientHeight));
            const shift = height - extra + scroll - nextScroll;
            const pinnedBottom = navTop + baseHeight + height;
            if (rows.every((row, i) => (mask & (1 << i)) || (row && row.top + shift >= pinnedBottom - 1 && row.bottom + shift <= navBottom + 1))) break;
        }
        targets.forEach(({ pin }, i) => {
            pin.hidden = !(mask & (1 << i));
            if (pin.hidden && pin.contains(menuButton)) closeFeedMenu();
        });
    }

    // Keep the selected row clear of the labels pinned over the top of the list.
    function revealCurrentFeed() {
        const currentFeed = activeFeedLinks().find(link => link.getAttribute('aria-current') === 'page');
        if (!currentFeed || currentFeed.closest('[hidden], .feed-pins')) return;
        const row = currentFeed.getBoundingClientRect();
        const list = nav.getBoundingClientRect();
        const top = nav.querySelector('.feed-pins').getBoundingClientRect().bottom;
        if (row.bottom > list.bottom) nav.scrollTop += row.bottom - list.bottom;
        else if (row.top < top) nav.scrollTop -= top - row.top;
    }
    revealCurrentFeed();

    let seenInTab = false;
    try {
        seenInTab = sessionStorage.getItem('feedbox.visited') === '1';
        sessionStorage.setItem('feedbox.visited', '1');
    } catch { /* Navigation timing, when available, also distinguishes reloads. */ }
    const timing = performance.getEntriesByType('navigation')[0]?.type;
    const reloading = timing ? timing === 'reload' : seenInTab;
    if (!reloading && readingPosition(displayedUrl)) {
        navigate(displayedUrl);
    } else if (document.body.dataset.unread === '1') {
        // An explicit reload starts at the first remaining unread article rather than an old pixel offset.
        const previousRestoration = history.scrollRestoration;
        history.scrollRestoration = 'manual';
        const reloadedEntries = entries;
        window.addEventListener('pageshow', () => {
            // Align after the browser has finished restoring the document's history state.
            requestAnimationFrame(() => {
                if (entries === reloadedEntries && !navigation && !selected) {
                    const first = rows()[0];
                    if (first) select(first, false);
                    else window.scrollTo(0, 0);
                }
                history.scrollRestoration = previousRestoration;
            });
        }, { once: true });
    }
})();
