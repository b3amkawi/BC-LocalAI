/*
 * Chat UI wiring (FE-001). Calls only POST /api/chat on this site.
 * Every piece of text from the user, the model or the server is written with textContent,
 * never as markup, so HTML or script in an answer is shown as plain text.
 * The conversation lives only in memory: nothing is stored in the browser.
 */
(function () {
    'use strict';

    var core = window.ChatCore;
    var form = document.getElementById('composer');
    var input = document.getElementById('message');
    var sendButton = document.getElementById('send');
    var list = document.getElementById('messages');
    var scroller = document.getElementById('conversation');
    var hint = document.getElementById('hint');
    var modelChip = document.getElementById('model-chip');
    var emptyState = document.getElementById('empty-state');

    if (!core || !form || !input || !sendButton || !list || !scroller || !hint || !modelChip) {
        return;
    }

    var CHAT_URL = '/api/chat';
    // The server renders its own deadline plus a margin into the form, so its AI_TIMEOUT arrives first;
    // this only guards a request that never ends.
    var REQUEST_TIMEOUT_MS = core.requestTimeoutMs(form.getAttribute('data-timeout-seconds'));
    var MAX_INPUT_HEIGHT_PX = 200;
    var sending = false;

    function scrollToEnd() {
        scroller.scrollTop = scroller.scrollHeight;
    }

    function resizeInput() {
        // An empty field uses the height from the stylesheet (one line); measuring it can include the placeholder.
        if (input.value === '') {
            input.style.height = '';
            return;
        }

        // The field is border-box: add the border so the text fits without an inner scrollbar.
        input.style.height = 'auto';
        var borders = input.offsetHeight - input.clientHeight;
        input.style.height = Math.min(input.scrollHeight + borders, MAX_INPUT_HEIGHT_PX) + 'px';
    }

    function showHint(text) {
        hint.textContent = text;
    }

    function addMessage(kind, text, label) {
        if (emptyState && emptyState.parentNode) {
            emptyState.parentNode.removeChild(emptyState);
        }

        var item = document.createElement('li');
        item.className = 'message message--' + kind;

        var meta = document.createElement('div');
        meta.className = 'message__meta';
        meta.textContent = label;

        var body = document.createElement('div');
        body.className = 'message__text';
        body.textContent = text;

        item.appendChild(meta);
        item.appendChild(body);
        list.appendChild(item);
        scrollToEnd();
        return item;
    }

    function addPending() {
        var item = document.createElement('li');
        item.className = 'message message--ai message--pending';
        item.setAttribute('role', 'status');

        var text = document.createElement('span');
        text.textContent = 'AI กำลังประมวลผล';
        item.appendChild(text);

        var dots = document.createElement('span');
        dots.className = 'dots';
        dots.setAttribute('aria-hidden', 'true');
        for (var i = 0; i < 3; i += 1) {
            dots.appendChild(document.createElement('i'));
        }
        item.appendChild(dots);

        list.appendChild(item);
        scrollToEnd();
        return item;
    }

    function setSending(value) {
        sending = value;
        sendButton.disabled = value;
        form.setAttribute('aria-busy', value ? 'true' : 'false');
    }

    async function requestAnswer(text) {
        var controller = new AbortController();
        var timer = setTimeout(function () { controller.abort(); }, REQUEST_TIMEOUT_MS);
        try {
            var response = await fetch(CHAT_URL, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ message: text }),
                signal: controller.signal
            });
            var bodyText = await response.text();
            return core.classifyResponse(response.status, bodyText);
        } catch (error) {
            return core.failure(error && error.name === 'AbortError' ? 'AI_TIMEOUT' : 'NETWORK_ERROR');
        } finally {
            clearTimeout(timer);
        }
    }

    async function onSubmit(event) {
        event.preventDefault();

        // Guards the Enter key and double clicks as well as the disabled button.
        if (sending) {
            return;
        }

        var text = core.prepareMessage(input.value);
        if (!text) {
            input.value = '';
            resizeInput();
            showHint(core.messageFor('INVALID_MESSAGE'));
            input.focus();
            return;
        }

        showHint('');
        setSending(true);
        var pending = null;
        try {
            addMessage('user', text, 'คุณ');
            input.value = '';
            resizeInput();
            pending = addPending();

            var result = await requestAnswer(text);

            if (result.ok) {
                // classifyResponse guarantees both values are non-empty strings.
                addMessage('ai', result.response, 'AI · ' + result.model);
                modelChip.textContent = 'โมเดล: ' + result.model;
            } else {
                addMessage('error', result.message, 'ข้อผิดพลาด');
                // Give the question back so it can be sent again, unless a new one is already being typed.
                if (input.value === '') {
                    input.value = text;
                    resizeInput();
                }
            }
        } finally {
            if (pending && pending.parentNode) {
                pending.parentNode.removeChild(pending);
            }
            setSending(false);
            input.focus();
            scrollToEnd();
        }
    }

    form.addEventListener('submit', onSubmit);

    input.addEventListener('keydown', function (event) {
        if (core.isSendKey(event)) {
            event.preventDefault();
            form.requestSubmit();
        }
    });

    input.addEventListener('input', function () {
        resizeInput();
        if (hint.textContent !== '') {
            showHint('');
        }
    });

    // A different width wraps the text differently.
    window.addEventListener('resize', resizeInput);

    resizeInput();
    input.focus();
}());
