/* FE-003 DOM shell. All untrusted text uses textContent; no HTML/Markdown execution, persistent storage or automatic retries. */
(function () {
    'use strict';
    const el = id => document.getElementById(id), historyEnabled = document.body.dataset.historyEnabled === 'true';
    const boundary = HistoryCore.createBoundary();
    let user = null, client = null, current = null, page = null, rows = [], listCursor = null;
    let anchors = [null], pageIndex = 0, listAnchors = [null], listIndex = 0, busy = false, activeStream = null;
    let idleTimer, channel, dialogMode = null, restoreFocus = null, leaving = false, logoutActor = null;
    const http = FrontendHttp.create(window.fetch.bind(window), { protocol: window.location.protocol, getActor: () => logoutActor || user?.id || null });
    const auth = AuthCore.createSessionController(AuthCore.createClient(http.authTransport), state => {
        if (leaving) return;
        clearPrivate();
        el('auth-status').textContent = state.status === 'checking' ? 'กำลังตรวจสอบเซสชัน…' :
            state.status === 'expired' ? 'เซสชันสิ้นสุดหรือบัญชีเปลี่ยนไป' : state.status === 'error' ? 'ตรวจสอบเซสชันไม่สำเร็จ' : 'กรุณาเข้าสู่ระบบ';
        el('auth-error').textContent = state.message; el('auth-error').hidden = !state.message;
        el('auth-retry').hidden = state.busy; el('login-link').hidden = state.busy;
        if (state.status !== 'authenticated') return;
        user = state.user; client = HistoryCore.createClient(http, user.id);
        el('display-name').textContent = user.displayName; el('account-name').textContent = user.username;
        el('user-role').textContent = user.roles.includes('Admin') ? 'ผู้ดูแลระบบ · แสดงเฉพาะประวัติของคุณ' : 'พนักงาน';
        el('session-boundary').hidden = true; el('protected-app').hidden = false;
        if (!historyEnabled) notice('ระบบไม่ได้เปิดการบันทึกประวัติ บทสนทนานี้จะไม่ถูกบันทึก');
        armIdle(); controls();
        if (historyEnabled) void work(cap => loadList(0, cap));
    });
    function clearPrivate() {
        boundary.reset(); activeStream?.abort(); activeStream = null; clearTimeout(idleTimer);
        user = client = current = page = null; rows = []; listCursor = null; busy = false;
        anchors = [null]; pageIndex = 0; listAnchors = [null]; listIndex = 0;
        el('messages').replaceChildren(); el('conversation-list').replaceChildren(); el('message').value = '';
        for (const id of ['display-name', 'account-name', 'user-role', 'history-error', 'history-notice', 'hint', 'message-page']) el(id).textContent = '';
        el('conversation-title').textContent = 'เริ่มบทสนทนาใหม่'; el('model-chip').textContent = 'โมเดล: —';
        el('protected-app').hidden = true; el('session-boundary').hidden = false;
        el('history-error').hidden = true; el('history-notice').hidden = true;
        el('history-loading').hidden = true;
        el('empty-state').hidden = false; el('message').disabled = true; el('send').disabled = true; el('stop').hidden = true;
        closeDialog();
    }
    function armIdle() {
        clearTimeout(idleTimer);
        const minutes = Number(document.body.dataset.idleMinutes);
        idleTimer = setTimeout(() => auth.invalidate('AUTH_REQUIRED'), Math.min(Math.max(minutes || 30, 1), 240) * 60000);
        // No background /me polling: it would keep an idle server session alive forever.
    }
    function notice(text) { el('history-notice').textContent = text; el('history-notice').hidden = !text; }
    function errorText(error) {
        return HistoryCore.MESSAGES[error?.code] || AuthCore.MESSAGES[error?.code] || ChatCore.describeFailure(error?.code, error?.retryAfterSeconds);
    }
    function handle(error) {
        if (error?.status === 401 || error?.status === 403 || /^AUTH_/.test(error?.code || '')) {
            auth.invalidate(error.status === 403 && error.code !== 'AUTH_CSRF_INVALID' ? 'AUTH_FORBIDDEN' : error.code || 'AUTH_REQUIRED'); return;
        }
        if (error?.code === 'CANCELLED') return;
        el('history-error').textContent = errorText(error); el('history-error').hidden = false;
    }
    async function identity(cap) {
        if (!cap.current() || !user) throw FrontendHttp.fault('CANCELLED');
        const expected = user.id;
        let data;
        try { data = await http.request('/api/auth/me', { actor: expected, signal: cap.signal }); }
        catch (error) {
            if (error.code === 'CANCELLED' || error.status === 401 || error.status === 403) throw error;
            throw FrontendHttp.fault('AUTH_UNAVAILABLE');
        }
        if (!cap.current()) throw FrontendHttp.fault('CANCELLED');
        const checked = AuthCore.safeUser(data?.user);
        if (!checked || checked.id !== expected) throw FrontendHttp.fault('AUTH_SESSION_CHANGED', 401);
        armIdle();
    }
    function controls() {
        const running = page?.items.some(item => item.status === 'pending' || item.status === 'streaming');
        const canSend = !!user && !busy && !page?.nextCursor && !running;
        el('message').disabled = !canSend; el('send').disabled = !canSend; el('stop').hidden = !activeStream;
        el('new-chat').disabled = busy || !historyEnabled; el('refresh-history').disabled = busy || !historyEnabled;
        for (const id of ['rename-chat', 'delete-chat']) el(id).disabled = busy || !current || !historyEnabled;
        el('previous-messages').disabled = busy || pageIndex === 0;
        el('next-messages').disabled = busy || !page?.nextCursor; el('latest-messages').disabled = busy || !page?.nextCursor;
        el('previous-conversations').hidden = listIndex === 0; el('more-conversations').hidden = !listCursor;
        el('previous-conversations').disabled = busy; el('more-conversations').disabled = busy;
        for (const button of el('conversation-list').querySelectorAll('button')) button.disabled = busy && !activeStream;
        el('composer').setAttribute('aria-busy', String(busy));
        el('history-loading').hidden = !busy || !!activeStream;
        el('dialog-confirm').textContent = busy && el('conversation-dialog').open ? 'กำลังดำเนินการ…' : 'ยืนยัน';
        el('hint').textContent = page?.nextCursor ? 'กำลังอ่านประวัติส่วนต้น ไปข้อความล่าสุดก่อนสนทนาต่อ' :
            running ? 'มีคำตอบที่กำลังสร้างอยู่ กรุณารอหรือรีเฟรชประวัติ' : '';
    }
    async function work(action) {
        if (!user || busy) return;
        busy = true; const cap = boundary.capture(); controls(); el('history-error').hidden = true;
        try { await identity(cap); await action(cap); }
        catch (error) { if (cap.current()) handle(error); }
        finally { if (cap.current()) { busy = false; controls(); } }
    }
    function renderList() {
        el('conversation-list').replaceChildren();
        for (const item of rows) {
            const li = document.createElement('li'), button = document.createElement('button');
            button.type = 'button'; button.textContent = item.title; button.setAttribute('aria-current', String(item.id === current?.id));
            button.addEventListener('click', () => void openConversation(item.id)); li.append(button); el('conversation-list').append(li);
        }
        controls();
    }
    async function loadList(index, cap) {
        const loaded = await client.list(listAnchors[index], cap.signal); await identity(cap);
        if (!cap.current()) return;
        rows = loaded.items; listCursor = loaded.nextCursor; listIndex = index;
        if (listCursor) listAnchors[index + 1] = listCursor;
        renderList();
        if (!rows.length && !current) notice('ยังไม่มีบทสนทนา เริ่มบทสนทนาใหม่ได้เลย');
    }
    function appendMessage(role, content, status = 'completed') {
        const row = document.createElement('li'), label = document.createElement('strong'), text = document.createElement('p'), state = document.createElement('p');
        row.className = 'message-row' + (role === 'user' ? ' message-row--user' : '');
        label.textContent = role === 'user' ? 'คุณ' : 'QWEN'; text.textContent = content;
        state.className = 'message-status'; state.textContent = HistoryCore.LABELS[status] || '';
        row.append(label, text, state); el('messages').append(row); el('empty-state').hidden = true;
        return { text, state };
    }
    function renderMessages() {
        el('messages').replaceChildren();
        for (const item of page?.items || []) appendMessage(item.role, item.content, item.status);
        el('empty-state').hidden = !!page?.items.length;
        el('message-page').textContent = current ? 'หน้าข้อความ ' + (pageIndex + 1) : '';
        el('conversation-title').textContent = current?.title || 'เริ่มบทสนทนาใหม่'; renderList();
    }
    async function messagePage(index, cap) {
        const loaded = await client.messages(current.id, anchors[index], cap.signal); await identity(cap);
        if (!cap.current()) return;
        page = loaded; pageIndex = index; anchors = anchors.slice(0, index + 1);
        if (page.nextCursor) anchors[index + 1] = page.nextCursor;
        renderMessages();
    }
    async function latest(cap) {
        const seen = new Set();
        for (let i = 0; page?.nextCursor && i < 10; i++) {
            if (seen.has(page.nextCursor)) throw FrontendHttp.fault('INVALID_RESPONSE');
            seen.add(page.nextCursor); await messagePage(pageIndex + 1, cap);
        }
        if (page?.nextCursor) notice('โหลดครั้งละไม่เกิน 10 หน้าข้อความ กดไปข้อความล่าสุดอีกครั้งเพื่ออ่านต่อ');
        else { notice(''); el('conversation').scrollTop = el('conversation').scrollHeight; }
    }
    async function openConversation(id) {
        if (activeStream) { boundary.reset(); activeStream.abort(); activeStream = null; busy = false; }
        if (busy || !user) return;
        boundary.reset();
        await work(async cap => {
            const loaded = await client.open(id, cap.signal); await identity(cap);
            if (!cap.current()) return;
            current = loaded; anchors = [null]; pageIndex = 0; page = null;
            el('messages').replaceChildren(); await messagePage(0, cap); await latest(cap);
        });
    }
    function openDialog(mode) {
        if (busy || !user) return;
        dialogMode = mode; restoreFocus = document.activeElement;
        el('dialog-title').textContent = mode === 'create' ? 'บทสนทนาใหม่' : mode === 'rename' ? 'เปลี่ยนชื่อบทสนทนา' : 'ยืนยันลบบทสนทนา';
        el('dialog-description').textContent = mode === 'delete' ? 'บทสนทนาจะหายจากรายการทันที ตามนโยบายปัจจุบันข้อมูลรอการล้างจากฐานข้อมูลหลัง 30 วัน การล้างต้องดำเนินการโดยผู้ดูแลระบบ' : 'ตั้งชื่อเพื่อค้นหาบทสนทนานี้ได้ง่ายขึ้น';
        el('conversation-name').value = mode === 'rename' ? current.title : '';
        el('conversation-name').hidden = el('title-label').hidden = mode === 'delete';
        el('dialog-error').hidden = true; el('conversation-dialog').showModal();
        (mode === 'delete' ? el('dialog-cancel') : el('conversation-name')).focus();
    }
    function closeDialog() {
        if (el('conversation-dialog').open) el('conversation-dialog').close();
        el('conversation-name').value = ''; dialogMode = null;
        el('dialog-confirm').disabled = false; el('dialog-cancel').disabled = false;
        if (user && restoreFocus?.isConnected) restoreFocus.focus(); restoreFocus = null;
    }
    el('dialog-form').addEventListener('submit', event => {
        event.preventDefault(); if (busy || !dialogMode) return;
        const mode = dialogMode, name = el('conversation-name').value;
        if (mode !== 'delete' && !HistoryCore.title(name)) {
            el('dialog-error').textContent = HistoryCore.MESSAGES.INVALID_TITLE; el('dialog-error').hidden = false; el('conversation-name').focus(); return;
        }
        void work(async cap => {
            el('dialog-confirm').disabled = true; el('dialog-cancel').disabled = true;
            try {
                const changed = mode === 'create' ? await client.create(name, cap.signal) :
                    mode === 'rename' ? await client.rename(current.id, name, cap.signal) : await client.remove(current.id, cap.signal);
                await identity(cap); if (!cap.current()) return;
                if (mode === 'delete') { current = page = null; anchors = [null]; pageIndex = 0; renderMessages(); }
                else { current = changed; if (mode === 'create') { page = null; anchors = [null]; pageIndex = 0; await messagePage(0, cap); } }
                closeDialog(); listAnchors = [null]; await loadList(0, cap); renderMessages();
            } catch (error) {
                if (cap.current()) { el('dialog-error').textContent = errorText(error); el('dialog-error').hidden = false; } throw error;
            } finally { if (cap.current()) { el('dialog-confirm').disabled = false; el('dialog-cancel').disabled = false; } }
        });
    });
    el('dialog-cancel').addEventListener('click', () => { if (!busy) closeDialog(); });
    el('conversation-dialog').addEventListener('cancel', event => { if (busy) event.preventDefault(); else closeDialog(); });
    el('new-chat').addEventListener('click', () => openDialog('create'));
    el('rename-chat').addEventListener('click', () => openDialog('rename'));
    el('delete-chat').addEventListener('click', () => openDialog('delete'));
    el('previous-conversations').addEventListener('click', () => void work(cap => loadList(listIndex - 1, cap)));
    el('more-conversations').addEventListener('click', () => void work(cap => loadList(listIndex + 1, cap)));
    el('previous-messages').addEventListener('click', () => void work(cap => messagePage(pageIndex - 1, cap)));
    el('next-messages').addEventListener('click', () => void work(cap => messagePage(pageIndex + 1, cap)));
    el('latest-messages').addEventListener('click', () => void work(latest));
    el('refresh-history').addEventListener('click', () => void work(async cap => {
        listAnchors = [null]; await loadList(0, cap); if (current) await messagePage(pageIndex, cap);
    }));
    async function send() {
        if (!user || busy || page?.nextCursor) return;
        const question = ChatCore.prepareMessage(el('message').value);
        if (!question) { el('hint').textContent = ChatCore.messageFor('INVALID_MESSAGE'); el('message').focus(); return; }
        if (question.isWellFormed?.() === false || new TextEncoder().encode(question).length > 1024) {
            el('hint').textContent = 'กรุณาถามให้สั้นลง (ไม่เกิน 1 KiB UTF-8)'; return;
        }
        const cap = boundary.capture(), controller = new AbortController(); activeStream = controller; busy = true; controls();
        const relay = () => controller.abort(); cap.signal.addEventListener('abort', relay, { once: true });
        let answer = null, outcome = null;
        try {
            await identity(cap);
            if (historyEnabled && !current) {
                const made = await client.create([...question].slice(0, 80).join(''), controller.signal); await identity(cap);
                if (!cap.current()) return;
                current = made; anchors = [null]; pageIndex = 0; page = null; renderMessages();
            }
            const conversationId = current?.id;
            el('message').value = ''; appendMessage('user', question); answer = appendMessage('assistant', '', 'pending');
            el('conversation').scrollTop = el('conversation').scrollHeight;
            const body = { message: question, ...(conversationId ? { conversationId } : {}) };
            outcome = await http.stream(body, { actor: user.id, signal: controller.signal,
                timeout: ChatCore.requestTimeoutMs(el('composer').dataset.timeoutSeconds),
                consume: (response, signal) => ChatStream.readAnswer(response, snapshot => {
                    if (!cap.current()) return;
                    if (['AUTH_REQUIRED', 'AUTH_SESSION_ENDED', 'AUTH_SESSION_CHANGED', 'AUTH_FORBIDDEN'].includes(snapshot.code)) {
                        auth.invalidate(snapshot.code); return;
                    }
                    const area = el('conversation'), follow = area.scrollHeight - area.clientHeight - area.scrollTop < 64;
                    answer.text.textContent = snapshot.text;
                    answer.state.textContent = snapshot.state === 'completed' ? 'กำลังตรวจสอบประวัติ…' :
                        snapshot.state === 'error' ? ChatCore.describeFailure(snapshot.code, snapshot.retryAfterSeconds) :
                        (ChatStream.STATUS_TEXT[snapshot.state] || '') +
                        (snapshot.state === 'queued' && Number.isInteger(snapshot.ahead) && snapshot.ahead > 0
                            ? ' (ตอนรับคำถามมี ' + snapshot.ahead + ' คำถามรออยู่ข้างหน้า)' : '');
                    if (snapshot.model) el('model-chip').textContent = 'โมเดล: ' + snapshot.model;
                    if (follow) area.scrollTop = area.scrollHeight;
                }, { isAborted: () => signal.aborted, onEvent: event => {
                    if (!cap.current()) throw FrontendHttp.fault('CANCELLED');
                    if (event.event === 'started') {
                        let data; try { data = JSON.parse(event.data); } catch { throw FrontendHttp.fault('AI_INVALID_RESPONSE'); }
                        if (typeof data.model !== 'string' || data.model.length > 200 ||
                            (conversationId && (data.conversationId !== conversationId || !HistoryCore.validId(data.messageId))))
                            throw FrontendHttp.fault('AI_INVALID_RESPONSE');
                    }
                } }) });
        } catch (error) {
            if (cap.current()) {
                if (answer) answer.state.textContent = controller.signal.aborted ? 'หยุดสร้างคำตอบแล้ว · อาจยังไม่ครบ' : errorText(error);
                handle(error);
            }
        } finally {
            cap.signal.removeEventListener('abort', relay);
            if (cap.current()) {
                try {
                    await identity(cap);
                    if (historyEnabled && current) {
                        await messagePage(pageIndex, cap); await latest(cap);
                        // Aborting fetch and committing the cancelled status are asynchronous. Reconcile reads only;
                        // never resend a question or retry a mutation whose outcome may already be committed.
                        if (controller.signal.aborted) for (const delay of [150, 300, 600]) {
                            if (!page?.items.some(item => item.status === 'pending' || item.status === 'streaming')) break;
                            await pause(delay, cap.signal); await messagePage(pageIndex, cap); await latest(cap);
                        }
                        listAnchors = [null]; await loadList(0, cap);
                        if (controller.signal.aborted && page?.items.some(item => item.status === 'pending' || item.status === 'streaming'))
                            notice('ส่งคำขอหยุดแล้ว แต่ยังยืนยันสถานะจากเซิร์ฟเวอร์ไม่ได้ กรุณารีเฟรชประวัติก่อนส่งคำถามใหม่');
                    }
                    else if (answer && outcome) answer.state.textContent = outcome.state === 'completed' ? '' :
                        outcome.state === 'error' ? ChatCore.describeFailure(outcome.code, outcome.retryAfterSeconds) : 'หยุดสร้างคำตอบแล้ว · อาจยังไม่ครบ';
                } catch (error) { if (cap.current()) handle(error); }
                if (cap.current()) { activeStream = null; busy = false; controls(); el('message').focus(); }
            }
        }
    }
    function pause(milliseconds, signal) {
        if (signal.aborted) return Promise.reject(FrontendHttp.fault('CANCELLED'));
        return new Promise((resolve, reject) => {
            const cancel = () => { clearTimeout(timer); signal.removeEventListener('abort', cancel); reject(FrontendHttp.fault('CANCELLED')); };
            const timer = setTimeout(() => { signal.removeEventListener('abort', cancel); resolve(); }, milliseconds);
            signal.addEventListener('abort', cancel, { once: true });
        });
    }
    el('composer').addEventListener('submit', event => { event.preventDefault(); void send(); });
    el('message').addEventListener('keydown', event => { if (ChatCore.isSendKey(event)) { event.preventDefault(); void send(); } });
    el('stop').addEventListener('click', () => activeStream?.abort());
    document.addEventListener('keydown', event => { if (event.key === 'Escape' && activeStream) activeStream.abort(); });
    el('logout').addEventListener('click', async () => {
        logoutActor = user?.id || null;
        let success;
        try { success = await auth.logout(); } finally { logoutActor = null; }
        try { channel?.postMessage({ type: 'auth-changed' }); } catch { /* No private payload. */ }
        if (success) window.location.replace('/login');
    });
    el('auth-retry').addEventListener('click', () => void auth.checkSession());
    function recheck() { if (!leaving && document.visibilityState !== 'hidden') void auth.checkSession(); }
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') auth.clear(); else recheck(); });
    window.addEventListener('focus', recheck); window.addEventListener('pageshow', event => {
        if (event.persisted) { leaving = false; connectChannel(); } recheck();
    });
    window.addEventListener('pagehide', () => { leaving = true; clearPrivate(); auth.clear(); channel?.close(); channel = null; });
    function connectChannel() {
        if (channel) return;
        try { channel = new BroadcastChannel('bc-auth-change'); channel.addEventListener('message', event => {
            if (event.data?.type === 'auth-changed') { auth.clear(); recheck(); }
        }); } catch { /* Focus/pageshow checks remain enabled. */ }
    }
    connectChannel();
    recheck();
}());
