/* DOM shell for FE-003's clearly labelled, offline mock preview; never auto-wires a real auth transport. */
(function () {
    'use strict';
    const form = document.getElementById('login-form');
    if (!form || !window.AuthCore || !window.AuthMock) return;
    const core = window.AuthCore;
    const username = document.getElementById('login-username');
    const password = document.getElementById('login-password');
    const submit = document.getElementById('login-submit');
    const logout = document.getElementById('logout');
    const check = document.getElementById('check-session');
    const scenario = document.getElementById('scenario');
    const status = document.getElementById('auth-status');
    const error = document.getElementById('auth-error');
    const profile = document.getElementById('user-profile');
    const mock = window.AuthMock.createTransport();
    let displayedIdentity = false;
    const labels = { checking: 'กำลังตรวจสอบเซสชันทดสอบ…', authenticated: 'เข้าสู่ระบบตัวอย่างแล้ว (Mock)',
        unauthenticated: 'ยังไม่ได้เข้าสู่ระบบตัวอย่าง', expired: 'เซสชันทดสอบหมดอายุ', error: 'ระบบตัวอย่างเกิดข้อผิดพลาด' };
    const controller = core.createSessionController(core.createClient(mock.request), state => {
        if (displayedIdentity && !state.user) username.value = '';
        displayedIdentity = Boolean(state.user);
        form.setAttribute('aria-busy', String(state.busy));
        for (const input of [username, password, submit, logout, check, scenario]) input.disabled = state.busy;
        submit.textContent = state.busy ? 'กำลังดำเนินการ…' : 'เข้าสู่ระบบตัวอย่าง';
        status.textContent = labels[state.status];
        error.textContent = state.message;
        error.hidden = !state.message;
        profile.hidden = state.status !== 'authenticated';
        form.hidden = state.status === 'authenticated';
        // Clear old identity from the DOM before every check/failure/logout, not just hide it.
        document.getElementById('display-name').textContent = state.user?.displayName || '';
        document.getElementById('account-name').textContent = state.user?.username || '';
        document.getElementById('user-role').textContent = state.user?.roles.join(', ') || '';
        username.setAttribute('aria-invalid', String(state.field === 'username'));
        password.setAttribute('aria-invalid', String(state.field === 'password'));
        if (!state.busy && state.field) (state.field === 'username' ? username : password).focus();
        if (state.status === 'authenticated') logout.focus();
    });
    form.addEventListener('submit', event => {
        event.preventDefault();
        if (controller.snapshot().busy) return;
        const pending = controller.login(username.value, password.value);
        password.value = ''; // Never retain a submitted password in the field, state or storage.
        void pending;
    });
    logout.addEventListener('click', () => { password.value = ''; void controller.logout(); });
    check.addEventListener('click', () => { password.value = ''; void controller.checkSession(); });
    scenario.addEventListener('change', () => mock.setScenario(scenario.value));
    window.addEventListener('pagehide', () => { username.value = ''; password.value = ''; controller.clear(); });
    window.addEventListener('pageshow', event => { if (event.persisted) void controller.checkSession(); });
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') { password.value = ''; void controller.checkSession(); }
    });
    void controller.checkSession();
}());
