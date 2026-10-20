# FE-001 Reviewer Guide (Chat web page)

Issue: #10 · Branch: `watzapon03_Backend` · Target: `main` (**do not merge until review is done**)

Requirements: .NET 10 SDK; Node 20+ only for the optional logic tests. Ollama with `qwen3:4b` for the live steps.

## 1. Build and automated tests (no Ollama needed)

Stop any running copy of the app first (Ctrl+C), otherwise the build can fail with `MSB3021` / `MSB3027`.

```powershell
git fetch origin
git checkout watzapon03_Backend
dotnet build .\BC-LocalAI.slnx
dotnet test .\BC-LocalAI.slnx
node --test "tests/ui/*.test.mjs"
```

Expected: 0 warnings / 0 errors, `Failed: 0` (157 tests), Node `pass 18, fail 0`.

## 2. Try the page

```powershell
dotnet run --project .\BC-LocalAI\BC-LocalAI.csproj --launch-profile http
```

Open <http://localhost:5290>. Try (about 5 minutes):

1. Type `หนึ่งบวกหนึ่งได้เท่าไร ตอบสั้น ๆ` and press Enter: the answer `2` and `AI · qwen3:4b` appear.
2. Press Enter on an empty box: `กรุณาพิมพ์ข้อความก่อนส่ง`, nothing is sent.
3. Shift+Enter: new line, nothing sent. While an answer is pending the button is disabled and Enter does not send again.
4. Type `<img src=x onerror=alert(1)>` as the question: it is shown as text, no alert.
5. Narrow the window to phone width (or use the browser device mode): no horizontal scroll, button reachable.
6. Keyboard only: Tab goes to the message area (focus ring; arrow keys scroll), the input, the button.
7. Stop Ollama (or set `$env:Ollama__BaseUrl = "http://127.0.0.1:59999"` before starting) and send: `ไม่สามารถเชื่อมต่อ AI ได้ กรุณาตรวจว่า Ollama เปิดอยู่`.
8. In the browser console run `fetch("http://127.0.0.1:11434/api/tags")`: it must be blocked (CSP `connect-src`).

## 3. Acceptance criteria (Issue #10)

| # | Criterion | How to check | OK? |
|---|-----------|--------------|-----|
| 1 | User can type and send | step 2.1 | [ ] |
| 2 | UI calls `POST /api/chat` | browser network tab; one `fetch` in `chat.js` | [ ] |
| 3 | Answer and model are shown | step 2.1 | [ ] |
| 4 | Loading state | step 2.3 | [ ] |
| 5 | No double submit | step 2.3 | [ ] |
| 6 | Every error code has the required text | `tests/ui`, `ChatPageTests`, step 2.7 | [ ] |
| 7 | No XSS from text or AI answers | step 2.4, `ChatPageTests` (no `innerHTML`), CSP | [ ] |
| 8 | Works on mobile | step 2.5 | [ ] |
| 9 | Works with the keyboard | step 2.6 | [ ] |
| 10 | Ollama is not called from the browser | step 2.8, no `11434` in page files | [ ] |
| 11 | `dotnet build` and `dotnet test` pass | step 1 | [ ] |
| 12 | README explains usage | `README.md` | [ ] |
| 13 | Screenshots attached | `docs/fe-001/` and the pull request | [ ] |

## 4. Code review points

| Area | File | Look for |
|------|------|----------|
| Page | `BC-LocalAI/Views/Home/Index.cshtml` | labelled input, `role="log"`, no inline script/style/handlers, `tabindex="0"` message area |
| Logic | `BC-LocalAI/wwwroot/js/chat-core.js` | exact error texts, unknown codes -> generic text, no server text shown, Enter / Shift+Enter / IME |
| Wiring | `BC-LocalAI/wwwroot/js/chat.js` | `textContent` only, one `fetch`, double-submit guard, `finally` re-enables the button, nothing stored |
| Security headers | `BC-LocalAI/Controllers/HomeController.cs` | CSP (`script-src 'self'`, `connect-src 'self'`), nosniff |
| Styles | `BC-LocalAI/wwwroot/css/chat.css` | mobile breakpoints, 16 px input, focus rings, reduced motion |
| Tests | `BC-LocalAI.Tests/ChatPageTests.cs`, `tests/ui/` | the security rules of the issue are checked by tests |

## 5. Known limits (see `docs/FE-001-evidence.md` section 5 and `docs/KNOWN-LIMITATIONS.md`)

- Tested in Chromium only and with emulated phone sizes; no screen reader run.
- No history, streaming, Markdown, login or database (out of scope). The conversation is lost on reload.
- BE-002 limits apply: an answer over the output limit gives an error (`AI_RESPONSE_TOO_LARGE` since BE-003, limit 1024 tokens; it was 256 and `AI_INVALID_RESPONSE`); many simultaneous users queue.

## 6. Reviewer sign-off

- [ ] Code review approved
- [ ] Screenshots / demo accepted
- [ ] Merge into `main` (owner only)
- [ ] `/` and `/api/chat` checked after the merge
