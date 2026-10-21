/* FE-003: public history DTOs and bounded cursor navigation. The server, never this module, chooses the owner. */
(function (root, factory) {
    'use strict';
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.HistoryCore = factory();
}(typeof self !== 'undefined' ? self : this, function () {
    'use strict';
    const idPattern = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
    const statuses = new Set(['pending', 'streaming', 'completed', 'failed', 'cancelled']);
    const LABELS = Object.freeze({ pending: 'รอสร้างคำตอบ', streaming: 'กำลังสร้างคำตอบ · อาจยังไม่ครบ', completed: '',
        failed: 'คำตอบไม่สมบูรณ์', cancelled: 'หยุดสร้างคำตอบแล้ว · อาจยังไม่ครบ' });
    const MESSAGES = Object.freeze({ INVALID_TITLE: 'กรุณาระบุชื่อบทสนทนา 1–200 ตัวอักษร', INVALID_RESPONSE: 'ได้รับประวัติที่ไม่สมบูรณ์ กรุณารีเฟรช',
        HISTORY_UNAVAILABLE: 'ไม่สามารถโหลดหรือบันทึกประวัติได้ ไม่ส่งคำถามซ้ำอัตโนมัติ กรุณาตรวจสอบประวัติก่อนลองใหม่',
        HISTORY_CONFLICT: 'มีคำตอบที่กำลังสร้างอยู่ กรุณารอหรือรีเฟรชประวัติ',
        CONVERSATION_NOT_FOUND: 'ไม่พบบทสนทนานี้ หรือคุณไม่มีสิทธิ์เข้าถึง',
        NETWORK_ERROR: 'เชื่อมต่อระบบไม่ได้ กรุณาตรวจสอบประวัติก่อนลองใหม่', REQUEST_TIMEOUT: 'หมดเวลารอ กรุณาตรวจสอบประวัติก่อนลองใหม่',
        CANCELLED: 'ยกเลิกคำขอแล้ว', QUESTION_TOO_LONG: 'กรุณาถามให้สั้นลง', INVALID_REQUEST: 'คำขอไม่ถูกต้อง กรุณารีเฟรช' });
    function bad() { const e = new Error('INVALID_RESPONSE'); e.code = 'INVALID_RESPONSE'; throw e; }
    function validId(id) { return typeof id === 'string' && idPattern.test(id); }
    function date(value) { return typeof value === 'string' && value.length <= 40 && Number.isFinite(Date.parse(value)); }
    function title(value) { return typeof value === 'string' && value.trim().length > 0 && value.length <= 200 ? value.trim() : null; }
    function conversation(value) {
        if (!value || !validId(value.id) || !title(value.title) || !date(value.createdAt) || !date(value.updatedAt)) bad();
        return Object.freeze({ id: value.id, title: value.title, createdAt: value.createdAt, updatedAt: value.updatedAt });
    }
    function message(value, conversationId) {
        if (!value || !validId(value.id) || value.conversationId !== conversationId || !validId(conversationId) ||
            !Number.isSafeInteger(value.sequence) || value.sequence < 1 || !['user', 'assistant'].includes(value.role) ||
            typeof value.content !== 'string' || value.content.length > 1048576 || (value.status === 'completed' && !value.content.trim()) || !statuses.has(value.status) ||
            !date(value.createdAt) || !date(value.updatedAt)) bad();
        return Object.freeze({ id: value.id, conversationId, sequence: value.sequence, role: value.role,
            content: value.content, status: value.status, createdAt: value.createdAt, updatedAt: value.updatedAt });
    }
    function page(value, parse) {
        if (!value || !Array.isArray(value.items) || value.items.length > 100 ||
            !(value.nextCursor === null || (typeof value.nextCursor === 'string' && /^[A-Za-z0-9_-]{1,256}$/.test(value.nextCursor)))) bad();
        const items = value.items.map(parse);
        if (new Set(items.map(item => item.id)).size !== items.length) bad();
        return Object.freeze({ items: Object.freeze(items), nextCursor: value.nextCursor });
    }
    function createClient(http, actor) {
        if (!validId(actor)) bad();
        const route = id => { if (!validId(id)) bad(); return '/api/conversations/' + id; };
        const query = cursor => {
            if (cursor != null && !/^[A-Za-z0-9_-]{1,256}$/.test(cursor)) bad();
            return '?limit=20' + (cursor ? '&cursor=' + encodeURIComponent(cursor) : '');
        };
        const send = (path, options = {}) => http.request(path, { ...options, actor });
        const write = (path, method, value, signal) => {
            const clean = title(value); if (!clean) { const e = new Error('INVALID_TITLE'); e.code = 'INVALID_TITLE'; throw e; }
            return send(path, { method, body: { title: clean }, signal }).then(conversation);
        };
        return Object.freeze({
            list: (cursor, signal) => send('/api/conversations' + query(cursor), { signal }).then(value => {
                const result = page(value, conversation); if (cursor && result.nextCursor === cursor) bad(); return result;
            }),
            open: (id, signal) => send(route(id), { signal }).then(conversation),
            create: (value, signal) => write('/api/conversations', 'POST', value, signal),
            rename: (id, value, signal) => write(route(id), 'PATCH', value, signal),
            remove: (id, signal) => send(route(id), { method: 'DELETE', signal }),
            messages: (id, cursor, signal) => send(route(id) + '/messages' + query(cursor), { signal }).then(value => {
                const result = page(value, item => message(item, id));
                for (let i = 1; i < result.items.length; i++) if (result.items[i].sequence <= result.items[i - 1].sequence) bad();
                if (cursor && result.nextCursor === cursor) bad();
                return result;
            })
        });
    }
    // An epoch is invalidated before aborting: even transports which ignore AbortSignal cannot publish stale data.
    function createBoundary() {
        let epoch = 0, active = new AbortController();
        return Object.freeze({
            reset: () => { epoch++; active.abort(); active = new AbortController(); },
            capture: () => { const captured = epoch, signal = active.signal; return Object.freeze({ signal, current: () => captured === epoch && !signal.aborted }); }
        });
    }
    return Object.freeze({ LABELS, MESSAGES, validId, title, conversation, message, page, createClient, createBoundary });
}));
