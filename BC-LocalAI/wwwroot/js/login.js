(function () {
    'use strict';
    const el = id => document.getElementById(id), form = el('login-form'), password = el('login-password');
    const http = FrontendHttp.create(window.fetch.bind(window), { protocol: window.location.protocol });
    let leaving = false;
    const controller = AuthCore.createSessionController(AuthCore.createClient(http.authTransport), state => {
        if (leaving) return;
        password.value = '';
        form.setAttribute('aria-busy', String(state.busy));
        for (const id of ['login-username', 'login-password', 'login-submit']) el(id).disabled = state.busy || state.status === 'authenticated';
        el('auth-status').textContent = state.busy ? 'กำลังตรวจสอบ…' : state.status === 'authenticated' ? 'เข้าสู่ระบบสำเร็จ กำลังเปิดบทสนทนา…' : 'กรุณาเข้าสู่ระบบ';
        el('auth-error').textContent = state.message; el('auth-error').hidden = !state.message;
        el('auth-retry').hidden = state.busy || state.status !== 'error';
        for (const id of ['login-username', 'login-password']) el(id).setAttribute('aria-invalid', String(state.field === id.replace('login-', '')));
        if (state.field) el('login-' + state.field).focus();
        if (state.status === 'authenticated') {
            try { const channel = new BroadcastChannel('bc-auth-change'); channel.postMessage({ type: 'auth-changed' }); channel.close(); } catch { /* Focus checks also work without channels. */ }
            leaving = true; window.location.replace('/'); // Fixed destination. Never accept an untrusted returnUrl.
        }
    });
    form.addEventListener('submit', event => {
        event.preventDefault();
        const submitted = password.value; password.value = '';
        void controller.login(el('login-username').value, submitted);
    });
    el('auth-retry').addEventListener('click', () => void controller.checkSession());
    window.addEventListener('pagehide', () => { leaving = true; password.value = ''; el('login-username').value = ''; controller.clear(); });
    window.addEventListener('pageshow', event => {
        if (event.persisted && window.location.protocol === 'https:') { leaving = false; void controller.checkSession(); }
    });
    if (window.location.protocol !== 'https:') {
        el('auth-status').textContent = 'ต้องเปิดหน้านี้ผ่าน HTTPS'; el('auth-error').textContent = 'ไม่ส่งข้อมูลเข้าสู่ระบบผ่าน HTTP'; el('auth-error').hidden = false;
    } else void controller.checkSession();
}());
