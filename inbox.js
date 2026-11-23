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

    // Use the view's filters as the bookmark key, independent of receipts and pagination URLs.
    function readingKey(url) {
        const params = new URL(url).searchParams;
        return 'feed-inbox.position:' + JSON.stringify(['feed', 'group', 'all', 'oldest', 'from', 'to', 'q'].map(key => params.get(key) || ''));
    }

    function readingPosition(url) {
        try { return localStorage.getItem(readingKey(url)); }
        catch { return null; }
    }

    async function checkFeed(checkForm) {
        const button = checkForm.querySelector('button');
        if (button.disabled) return;
        const body = new URLSearchParams(new FormData(checkForm));
        body.set('inline', '1');
        const feed = body.get('feed');
        const state = { pending: true, text: feed ? `Checking ${header.querySelector('h1').textContent}…` : 'Checking…', seconds: 0 };
        checks.set(checkForm.getAttribute('action') + ':' + feed, state);
        showCheck();
        const started = Date.now();
        const timer = setInterval(() => {
            state.seconds = Math.floor((Date.now() - started) / 1000);
            showCheck();
        }, 1000);
        try {
            const response = await fetch(checkForm.action, { method: 'POST', body });
            state.text = await response.text() || 'No result received.';
        } catch {
            state.text = 'Lost contact with Feed Inbox before receiving the result.';
        } finally {
            clearInterval(timer);
            state.pending = false;
            showCheck();
            refreshCheckStatus(header.querySelector('.check-status'));
            refreshUnreadCounts();
        }
    }

    // Keep the result associated with its feed even if the reader switches away mid-check.
    const checks = new Map();
    function showCheck() {
        const form = header.querySelector('form[action="/check-feed"], form[action="/check"]');
        if (!form) return;
        const state = checks.get(form.getAttribute('action') + ':' + form.elements.feed.value);
        if (!state) return;
        const button = form.querySelector('button');
        button.disabled = state.pending;
        header.querySelector('#check-feedback').textContent = state.pending && state.seconds ? `${state.text} ${state.seconds}s` : state.text;
    }

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
            for (const bound of ['from', 'to', 'q']) if (filter.has(bound)) url.searchParams.set(bound, filter.get(bound));
            const response = await fetch(url);
            if (!response.ok) return;
            const { feeds, total, arrival } = await response.json();
            // Discard a snapshot taken before a newer read action or view change.
            if (busy || navigation || entries !== view || readRevision !== revision) return;
            const unread = name => Object.hasOwn(feeds, name) ? feeds[name] : 0;
            for (const link of nav.querySelectorAll('.feed[data-feed]')) {
                let count = total;
                if (link.dataset.feed) count = unread(link.dataset.feed);
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

    async function refreshArticles(view, revision) {
        const more = document.querySelector('#more');
        const edge = more?.href;
        const url = new URL('/new-articles', displayedUrl);
        const filter = new URL(displayedUrl).searchParams;
        for (const key of ['feed', 'group', 'all', 'oldest', 'from', 'to', 'q']) if (filter.has(key)) url.searchParams.set(key, filter.get(key));
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
        const panels = [...document.querySelectorAll('aside details')];
        const nextPanels = [...page.querySelectorAll('aside details')];
        if (!nextMain?.querySelector('#entries') || !nextMain.querySelector('.feed-header') || !nextNav || panels.length !== nextPanels.length)
            throw new Error('Feed Inbox returned an incomplete view.');

        // Update the server-rendered view without rebuilding the sidebar shell or the document.
        const sidebarScroll = nav.scrollTop;
        const sameView = historyMode === 'preserve' || url === displayedUrl;
        const readerScroll = window.scrollY;
        const statusOpen = sameView && header.querySelector('.check-status').open;
        closeFeedMenu();
        nav.replaceChildren(...nextNav.children);
        panels.forEach((panel, i) => panel.replaceChildren(...nextPanels[i].children));
        main.replaceChildren(...nextMain.children);
        setReaderTail(0);
        document.body.dataset.unread = page.body.dataset.unread;
        entries = main.querySelector('#entries');
        header = main.querySelector('.feed-header');
        status = main.querySelector('#key-status');
        header.querySelector('.check-status').open = statusOpen;
        selected = null;
        restoreGroups();
        updateMoreFeeds();
        nav.scrollTop = sidebarScroll;
        revealCurrentFeed();
        showCheck();

        displayedUrl = url;
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
            const response = await fetch(url, { signal: request.signal, cache: 'no-store' });
            const page = await pageFrom(response);
            if (navigation === request) {
                const visible = new URL(href, location.href);
                visible.searchParams.delete('at');
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

    main.addEventListener('click', event => {
        if (event.target.closest('.cancel-feed-edit')) { main.querySelector('.feed-editor').close(); return; }
        const action = event.target.closest('.feed-menu button')?.dataset.edit;
        if (!action || !menuButton) return;
        const link = menuButton.closest('.feed-row').querySelector('a.feed');
        const name = link.querySelector('.feed-label').textContent;
        const dialog = main.querySelector('.feed-editor');
        const rename = dialog.querySelector('form[action="/rename-feed"]');
        const reorganize = dialog.querySelector('form[action="/reorganize-feed"]');
        dialog.querySelector('h2').textContent = action === 'rename' ? 'Rename feed' : `Reorganize ${name}`;
        dialog.querySelector('.feed-editor-error').textContent = '';
        rename.hidden = action !== 'rename';
        reorganize.hidden = action !== 'reorganize';
        const form = action === 'rename' ? rename : reorganize;
        form.elements['edited-feed'].value = link.dataset.feed;
        if (action === 'rename') rename.elements.label.value = name;
        else {
            // Include hidden feed rows: unread filtering does not change membership.
            const groups = new Set([...nav.querySelectorAll('a.feed[data-feed]')].filter(a => a.dataset.feed === link.dataset.feed).map(a => a.dataset.group));
            for (const input of reorganize.querySelectorAll('[name=membership]')) input.checked = groups.has(input.value);
        }
        menuButton.focus({ preventScroll: true });
        closeFeedMenu();
        dialog.showModal();
        if (action === 'rename') rename.elements.label.select();
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
        const link = event.target.closest('aside nav a.feed, .toolbar > a, .date-clear, #more');
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
        if (form.closest('.date-filter')) {
            event.preventDefault();
            const params = new URLSearchParams(new FormData(form));
            // Changing dates starts a new reading stream; its first URL has no pagination cursor.
            for (const bound of ['from', 'to', 'q']) if (!params.get(bound)?.trim()) params.delete(bound);
            navigate(form.action + '?' + params);
            return;
        }
        const action = form.getAttribute('action');
        if (!['/check-feed', '/check', '/read', '/mark-feed-read', '/undo-feed-read', '/source', '/rss-source', '/group', '/collection', '/delete-read', '/rename-feed', '/reorganize-feed'].includes(action)) return;
        event.preventDefault();
        if (busy || navigation) return;
        if (action === '/check-feed' || action === '/check') { await checkFeed(form); return; }

        const button = event.submitter || form.querySelector('button');
        const fields = new URLSearchParams(new FormData(form));
        if (button?.name) fields.set(button.name, button.value);
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
                await setRead(form.closest('article[data-post]'), fields.get('mark') !== 'unread');
            } else {
                const response = await fetch(form.action, { method: 'POST', body: fields });
                const page = await pageFrom(response);
                if (entries === view && !navigation) {
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

    const splitter = document.querySelector('.sidebar-resize');
    const widthKey = 'feed-inbox.sidebar-width';
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

    const collapseKey = 'feed-inbox.collapsed-groups';
    let collapsed = new Set();
    try { collapsed = new Set(JSON.parse(localStorage.getItem(collapseKey) || '[]')); }
    catch { /* Use the expanded view when saved state is unavailable. */ }
    function showGroup(toggle, expanded) {
        const id = toggle.getAttribute('aria-controls');
        document.getElementById(id).hidden = !expanded;
        toggle.setAttribute('aria-expanded', String(expanded));
    }

    function restoreGroups() {
        // Keep the selected feed visible when entering it from a bookmark or shortcut.
        for (const toggle of nav.querySelectorAll('.group-toggle')) {
            const id = toggle.getAttribute('aria-controls');
            showGroup(toggle, !collapsed.has(id) || Boolean(document.getElementById(id).querySelector('[aria-current=page]')));
        }
    }
    restoreGroups();

    const expandedMoreFeeds = new Set();
    function updateMoreFeeds() {
        for (const section of nav.querySelectorAll('.group-members, .ungrouped-feeds')) {
            const more = section.querySelector('.more-feeds');
            const expanded = expandedMoreFeeds.has(section.id);
            let zeroUnread = 0;
            for (const link of section.querySelectorAll('.feed[data-feed]')) {
                // Apply the same zero-unread rule to the current feed as to every other feed.
                const tuckAway = Number(link.querySelector('.count').textContent) === 0;
                link.hidden = tuckAway && !expanded;
                if (link.parentElement.classList.contains('feed-row')) link.parentElement.hidden = link.hidden;
                if (tuckAway) zeroUnread++;
            }
            more.hidden = zeroUnread === 0;
            more.setAttribute('aria-expanded', String(expanded && zeroUnread > 0));
            more.textContent = expanded ? 'Show fewer feeds' : `${zeroUnread} more ${zeroUnread === 1 ? 'feed' : 'feeds'}`;
        }
    }

    updateMoreFeeds();
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
        if (row && row !== selected) select(row, false);
    });
    main.addEventListener('error', event => {
        if (event.target.matches('img.preview')) event.target.hidden = true;
    }, true);

    function moveFeed(direction) {
        // Keyboard feed switching follows visible sidebar entries.
        const all = Array.from(document.querySelectorAll('.feed[data-feed]')).filter(link => link.dataset.feed);
        const current = all.findIndex(link => link.getAttribute('aria-current') === 'page');
        const folder = document.querySelector('.group-link[aria-current=page]');
        const groupFeeds = folder ? all.filter(link => link.dataset.group === folder.dataset.group) : [];
        const groupStart = direction > 0 ? groupFeeds[0] : groupFeeds.at(-1);
        let index = current >= 0 ? current + direction : groupStart ? all.indexOf(groupStart) : direction > 0 ? 0 : all.length - 1;
        // In Unread, skip sources with no unread posts. View all also makes read-only feeds reachable.
        while (index >= 0 && index < all.length) {
            const link = all[index];
            if (!link.closest('[hidden]') && (document.body.dataset.unread === '0' || Number(link.querySelector('.count').textContent) > 0)) {
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
        entries.append(...nextEntries.children);
        const more = nextPage.querySelector('#more');
        if (more) link.setAttribute('href', more.getAttribute('href'));
        else link.remove();
        setReaderTail(0);
    }

    async function setRead(row, read) {
        if ((row.dataset.read === '1') === read) return;
        // Persist the action first. Leave the row in place for K or undo without disturbing the scroll position.
        const form = row.querySelector('form[action="/read"]');
        const fields = new URLSearchParams(new FormData(form));
        fields.set('inline', '1');
        fields.set('mark', read ? 'read' : 'unread');
        const response = await fetch(form.action, { method: 'POST', body: fields });
        if (response.status !== 204) throw new Error(await response.text() || 'Could not update this post.');
        readRevision++;
        if (!row.isConnected) return;
        row.dataset.read = read ? '1' : '0';
        const button = form.querySelector('button');
        button.value = read ? 'unread' : 'read';
        button.textContent = read ? 'Mark unread' : 'Mark read';
        for (const feed of document.querySelectorAll('.feed[data-feed]')) {
            if (feed.dataset.feed && feed.dataset.feed !== row.dataset.source) continue;
            // A post contributes once to the total and once to each folder containing its feed.
            if (!feed.dataset.feed && feed.dataset.group !== '0') {
                const members = [...feed.closest('.feed-group').querySelectorAll('.feed[data-feed]')];
                if (!members.some(member => member.dataset.feed === row.dataset.source)) continue;
            }
            const count = feed.querySelector('.count');
            count.textContent = Math.max(0, Number(count.textContent) + (read ? -1 : 1));
        }
        updateMoreFeeds();
    }

    document.addEventListener('keydown', async event => {
        if (event.defaultPrevented || event.repeat || event.isComposing || event.altKey || event.ctrlKey || event.metaKey
            || event.target.closest('input,textarea,select,[contenteditable=true],.feed-editor,.feed-menu')) return;
        const key = event.key.toLowerCase();
        if (!['j', 'k', 'v'].includes(key) || (key === 'v' && event.shiftKey)) return;
        event.preventDefault();
        if (busy || navigation) return;
        status.textContent = '';
        if (event.shiftKey) { moveFeed(key === 'j' ? 1 : -1); return; }

        const articles = rows();
        const current = selected && onScreen(selected) ? selected : null;
        // After a mouse scroll or on a fresh page, target a visible row without marking unseen rows.
        if (!current) select(articles.find(onScreen) || articles[0]);
        if (!selected) return;
        if (key === 'v') {
            const original = selected.querySelector('a.original');
            if (original) window.open(original.href, '_blank', 'noopener');
            return;
        }
        if (!current) return;
        const index = articles.indexOf(current);
        if (key === 'k') { select(articles[index - 1]); return; }

        busy = true;
        try {
            const more = document.querySelector('#more');
            if (!articles[index + 1] && more) await loadMore(more);
            if (!current.isConnected || navigation) return;
            await setRead(current, true);
            if (!current.isConnected || navigation) return;
            const next = rows()[index + 1];
            if (next) select(next);
            else {
                status.textContent = 'End of this feed.';
                alignArticle(current);
            }
        } catch (error) {
            status.textContent = error.message;
        } finally {
            busy = false;
        }
    });

    // Scroll to the selected feed only if it is visible in the sidebar.
    function revealCurrentFeed() {
        const currentFeed = nav.querySelector('.feed[aria-current=page]');
        if (!currentFeed || currentFeed.closest('[hidden]')) return;
        const row = currentFeed.getBoundingClientRect();
        const list = nav.getBoundingClientRect();
        if (row.bottom > list.bottom) nav.scrollTop += row.bottom - list.bottom;
        else if (row.top < list.top) nav.scrollTop -= list.top - row.top;
    }
    revealCurrentFeed();

    let seenInTab = false;
    try {
        seenInTab = sessionStorage.getItem('feed-inbox.visited') === '1';
        sessionStorage.setItem('feed-inbox.visited', '1');
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
