# FE-003 — Real Login and Chat History UI

2026-10-21 · [Issue #24](https://github.com/b3amkawi/BC-LocalAI/issues/24)
· branch `watzapon_FE-003`, integrated main `d121f5014b600288fc3cb69f57c22fd8de21f627` (local merge `c0a1d5e`).

## Scope and architecture

This is the existing ASP.NET Core MVC/Razor + vanilla JavaScript application, not a new framework or authentication system.
When `Authentication:Enabled` is true, `/` is server-authorized with `BcPolicies.ChatUser` and anonymous users go to `/login`.
The real Login uses BE-005, the same secure HttpOnly cookie/server-side sessions from BE-006, and BE-007 history from main.
Authentication false retains the original FE-001/002 anonymous/non-persistent page. History false with Authentication true
shows authenticated chat with an explicit “not saved” notice, not a silent fallback following a history failure.

The offline `/LoginPreview` groundwork remains a separate explicit Development/loopback-only preview. Neither real page loads
`auth-mock.js` or `auth-preview.js`. The application flags stay off by default; no production settings or migrations were changed.

| Component | Responsibility |
|---|---|
| `LoginController`, `Views/Login/Index.cshtml`, `login.js` | Real Login, validation, generic failures, immediate password clearing, fixed local redirect |
| `HomeController`, `Views/Home/Authenticated.cshtml` | Server-protected chat shell; no profile/history SSR into cached HTML |
| `auth-core.js` | Reviewed public profile DTO, session states, Login/Logout, fresh CSRF, cancellation |
| `frontend-http.js` | HTTPS/same-origin cookies, no redirects/cache, bounded JSON/SSE operations and full-body deadlines |
| `history-core.js` | Whitelisted public DTOs, owner-free CRUD requests, forward opaque cursor paging and generation boundary |
| `authenticated-chat.js` | Profile/sidebar/dialogs/history, session clearing, streaming, abort/navigation guards |
| `chat-core.js`, `chat-stream.js` | Reused FE-001/002 keyboard/stream logic, reviewed auth/history errors, metadata observer and byte cap |
| `FrontendApiIntegration`, `FrontendPageSecurity` | Approved additive CSRF/identity integration guards, HTTPS, no-store and CSP |

## API integration and security

Get a fresh `GET /api/auth/csrf` token before each mutation and send it in `X-CSRF-TOKEN`. Login/Logout change principal;
do not reuse an anonymous token after Login. History create/rename/delete and chat/stream requests with `conversationId` now
require header CSRF. Existing BE-007 live-test callers were updated to get a signed-in token; clients must do the same.

The UI sends `X-BC-Expected-User` containing the profile ID it is currently displaying. This is only a precondition: the server
compares it with the authenticated principal **before** authorization/model binding/DB work and rejects a mismatch with
`401 AUTH_SESSION_CHANGED`. It never selects an owner, authenticates a caller or grants permission. Missing precondition headers
remain allowed for legacy clients; server ownership checks still apply. Case/trailing-slash route aliases are covered.
Protected API responses identify their actual request actor with `X-BC-User`; the real UI rejects missing/mismatched actor headers
before consuming private data. Public history DTOs remain owner-free, and neither these headers nor cookies are forwarded to Ollama.

On 401/403, streamed session termination, Logout, page hide, visibility hide or identity change: abort old work, invalidate its
generation, clear profile/history/draft/dialog content, then recheck before displaying private data. Cross-tab signals carry only
`{type: "auth-changed"}`, never a username, ID, password, token or conversation. Focus/pageshow checks also work without BroadcastChannel.
BFCache restoration rechecks identity and reconnects the signal. Idle clearing does **not** poll `/me` in the background, which
would otherwise keep idle server sessions alive. Server idle/absolute expiry and authorization remain authoritative.

No password/token/chat is saved in localStorage/sessionStorage/IndexedDB. Browser password-manager behaviour is separate from
application storage; this code does not save credentials. All untrusted display values use `textContent`, never HTML/Markdown execution.
Real pages have no-store, same-origin CSP, no inline scripts or CDN, `form-action 'none'`, frame protection and safe error allowlists.
No password, mutation or question is automatically retried. A failed/unknown mutation outcome requires checking history before retrying.

## Interaction and bounds

- Enter sends; Shift+Enter and IME composition do not. Escape/Stop aborts a stream; incomplete responses remain labelled.
- Create/rename use an accessible native dialog; Delete requires confirmation and initially focuses Cancel.
- History is read in ascending message order, 20 items/page. Previous/next maintain opaque cursor anchors. “Latest” follows
  at most 10 pages per click, avoiding an unbounded fetch loop. Sending from an older page is disabled until the latest page.
- Conversation sidebar is paginated, not an unbounded list. The UI sends only the selected conversation ID; the backend builds context.
- Requests accept a well-formed question up to 1 KiB UTF-8 in this UI. Automatic titles use up to 80 Unicode code points without
  splitting surrogate pairs. This is **not** a change to main's estimated-token context policy or an endorsement of the separate BE-007 WIP.
- JSON/SSE reads are bounded to 2 MiB (error JSON16 KiB); request deadlines cover headers and body. Model/DTO/status/cursor fields are validated.
- After explicit Stop, at most three delayed **read-only** history checks (150/300/600ms) reconcile the asynchronous saved cancellation.
  If still pending, the UI says confirmation is unavailable and blocks another turn until refresh; it does not invent a cancelled/completed state.
- Responsive layout keeps separate sidebar/message scrolling and the composer within the viewport; compact controls handle a short phone viewport.

## Main retention policy is preserved

Main `d121f50` soft-deletes conversations immediately from the API and has an operator-run purge after 30 days; closed-account
retention is 90 days. The dialog explains the 30-day/operator dependency. There is no claim of immediate physical erasure or an
automatic application purge job. The earlier hard-delete/separate-context BE-007 WIP stays on its own branch and was not merged here.

## Reproduce with synthetic data only (Windows)

Requirements: .NET10 SDK, native MySQL8 binaries in the standard vendor directory, PowerShell7, Ollama with `qwen3:4b`,
and a trusted localhost development certificate. This certificate was explicitly approved by the user; no browser warning was bypassed.

```powershell
dotnet build BC-LocalAI.Tests/BC-LocalAI.Tests.csproj -c Release
pwsh -NoProfile -File scripts/frontend/fe003-local.ps1 -Action Initialize
pwsh -NoProfile -File scripts/frontend/fe003-local.ps1 -Action Verify
pwsh -NoProfile -File scripts/frontend/fe003-local.ps1 -Action RunApp
# Another terminal, while the app runs:
pwsh -NoProfile -File scripts/frontend/verify-fe003.ps1
```

Initialize refuses an existing directory/occupied port. For an existing stopped fixture use `-Action Start`, not Initialize.
The fixture lives only under `.local/fe003-mysql`, binds MySQL to loopback3338 and the app to `https://localhost:5937`.
It uses separate synthetic auth/history schemas, separate least-privilege runtime accounts and an ACL-protected private directory.
Bootstrap credentials are removed after initialization. A second synthetic account is unlocked only in this new test fixture to
exercise cross-user behaviour; no employee account, company schema, production lockout rule or other MySQL instance is changed.
Private account/option/key files are ignored and must not be committed, pasted into chat or included in evidence.
The live verifier uses these files internally, enforces normal TLS and prints only check labels/counts. It creates its own two
conversations for its limit=1 pagination check, independent of browser walkthrough data, then soft-deletes its own test conversations.

Ctrl+C stops the test app. `-Action Stop` validates the exact fixture data directory before shutting down only its MySQL instance;
it does not delete the fixture or saved evidence. Do not point the verifier at another port/database.

## Release / recovery boundary

This is local development verification, not production deployment or proof that actual company credentials/schema are compatible.
Before production use: obtain the approved company account mapping/secret provider, TLS/host/reverse-proxy configuration, retention
operator procedure and team review. Do not disable Authentication, weaken TLS/CSRF or allow client-selected owners to make a release work.
Recovery is a reviewed UI rollback/maintenance page while keeping the approved backend access controls; no destructive migration
is part of this issue. No push/PR/merge/deploy is authorized in this run.

See [verification and acceptance traceability](FE-003-REAL-VERIFICATION.md).
