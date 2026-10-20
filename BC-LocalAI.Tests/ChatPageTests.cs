using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BC_LocalAI.Tests;

/// <summary>FE-001: the Chat UI page, its static files and the security rules of Issue #10.</summary>
public sealed class ChatPageTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClientWithMock();

    private async Task<string> GetTextAsync(string url)
    {
        var response = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // ---------------------------------------------------------------- the page

    [Fact]
    public async Task HomePage_IsTheChatUi_NotTheTemplateWelcomePage()
    {
        var response = await _client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<html lang=\"th\">", html);
        Assert.Contains("<title>Local AI Chat</title>", html);
        Check.NotLeaked(html, "Learn about");
    }

    [Fact]
    public async Task Page_HasInputSendButtonAndMessageList_WithAccessibleNames()
    {
        var html = await GetTextAsync("/");

        Assert.Matches("<textarea[^>]*id=\"message\"", html);
        Assert.Matches("<label for=\"message\"", html);                // the input has a label
        Assert.Matches("<button[^>]*id=\"send\"[^>]*type=\"submit\"", html);
        Assert.Matches("<ol[^>]*id=\"messages\"[^>]*role=\"log\"", html); // announced to screen readers
        Assert.Matches("<form[^>]*id=\"composer\"", html);
        Assert.Contains("Shift+Enter", html);                           // the key hint is visible
        Assert.Contains("<noscript>", html);
        // The message area scrolls: it must be reachable with Tab so keyboard users can read older messages.
        Assert.Matches("<main[^>]*id=\"conversation\"[^>]*tabindex=\"0\"[^>]*aria-label=\"", html);
    }

    [Fact]
    public async Task Page_IsMobileReady()
    {
        var html = await GetTextAsync("/");
        var css = await GetTextAsync("/css/chat.css");

        Assert.Contains("name=\"viewport\" content=\"width=device-width, initial-scale=1", html);
        Assert.Contains("@media (max-width", css);
        Assert.Contains("@media (min-width", css);
        Assert.Contains("100dvh", css);                 // follows the mobile browser bars / keyboard
        Assert.Contains("env(safe-area-inset-bottom)", css);
        Assert.Matches("composer__input\\s*\\{[^}]*font-size:\\s*1rem", css); // 16 px: no zoom on focus
        Assert.Matches("composer__send\\s*\\{[^}]*min-height:\\s*48px", css); // touch target
    }

    [Fact]
    public async Task Css_ShowsKeyboardFocus_AndRespectsReducedMotion()
    {
        var css = await GetTextAsync("/css/chat.css");

        Assert.Contains(":focus-visible", css);
        Assert.Contains("prefers-reduced-motion: reduce", css);
        Check.NotLeaked(css, "outline: none", "outline:none");
        Assert.Matches("\\.conversation:focus-visible\\s*\\{[^}]*outline:\\s*3px", css); // the scrollable area shows focus too
    }

    [Fact]
    public async Task Css_UsesTheBetterCodeThemeColors()
    {
        var css = (await GetTextAsync("/css/chat.css")).ToLowerInvariant();

        Assert.Contains("#16386a", css); // navy
        Assert.Contains("#f9cb00", css); // yellow
        Assert.Contains("#ed1c24", css); // red
    }

    [Fact]
    public async Task PrivacyPage_StillWorks()
    {
        var html = await GetTextAsync("/Home/Privacy");

        Assert.Contains("Privacy", html);
    }

    // ---------------------------------------------------------------- static files

    [Theory]
    [InlineData("/css/chat.css", "text/css")]
    [InlineData("/js/chat-core.js", "text/javascript")]
    [InlineData("/js/chat.js", "text/javascript")]
    public async Task StaticFiles_AreServedWithTheRightType(string url, string mediaType)
    {
        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Page_LoadsExactlyItsOwnScriptsAndStyles()
    {
        var html = await GetTextAsync("/");

        var scripts = Regex.Matches(html, "<script[^>]*src=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        var styles = Regex.Matches(html, "<link[^>]*rel=\"stylesheet\"[^>]*href=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(2, scripts.Count);
        Assert.StartsWith("/js/chat-core.js", scripts[0]); // loads before chat.js, which needs it
        Assert.StartsWith("/js/chat.js", scripts[1]);
        Assert.Single(styles);
        Assert.StartsWith("/css/chat.css", styles[0]);
    }

    // ---------------------------------------------------------------- security

    [Fact]
    public async Task Page_SendsContentSecurityPolicy_ThatKeepsBrowserOnItsOwnOrigin()
    {
        var response = await _client.GetAsync("/");
        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));

        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("connect-src 'self'", csp);   // the browser cannot call Ollama on :11434 directly
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Check.NotLeaked(csp, "unsafe-inline", "unsafe-eval", "*");
        Assert.Equal("nosniff", string.Join("", response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Page_HasNoInlineScriptsEventHandlersOrInlineStyles()
    {
        var html = await GetTextAsync("/");

        // Every <script> must load a file.
        foreach (Match script in Regex.Matches(html, "<script\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            Assert.Contains("src=", script.Value);
        }

        Assert.DoesNotMatch("\\son[a-z]+\\s*=", html);            // onclick=, onerror=, ...
        Assert.DoesNotMatch("\\sstyle\\s*=", html);                 // CSP style-src 'self'
        Check.NotLeaked(html.ToLowerInvariant(), "javascript:");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/css/chat.css")]
    [InlineData("/js/chat-core.js")]
    [InlineData("/js/chat.js")]
    public async Task Page_UsesNoCdnOrExternalHost_AndNeverNamesOllama(string url)
    {
        var text = await GetTextAsync(url);

        Assert.DoesNotMatch("(https?:)?//[a-z0-9.-]+\\.[a-z]{2,}", Regex.Replace(text, "http://www\\.w3\\.org/\\d+/\\w+", ""));
        Check.NotLeaked(text, "11434", "127.0.0.1", "localhost", "cdn", "googleapis", "jsdelivr", "unpkg", "api/generate");
    }

    [Theory]
    [InlineData("/js/chat.js")]
    [InlineData("/js/chat-core.js")]
    public async Task Scripts_NeverInsertMarkupOrStoreOrExecuteText(string url)
    {
        var js = await GetTextAsync(url);

        // Issue #10 section 6: answers are shown with textContent only; nothing is persisted.
        Check.NotLeaked(
            js,
            "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "createContextualFragment", "DOMParser",
            "eval(", "new Function", "setTimeout(\"", "setInterval(\"", "srcdoc",
            "localStorage", "sessionStorage", "indexedDB", "document.cookie", "navigator.sendBeacon", "WebSocket", "EventSource");
    }

    [Fact]
    public async Task ChatScript_WritesTextWithTextContent_AndCallsOnlyTheChatApi()
    {
        var js = await GetTextAsync("/js/chat.js");

        Assert.Contains(".textContent = text", js);
        Assert.Contains(".textContent = label", js.Replace("meta.textContent = label", ".textContent = label"));

        var fetches = Regex.Matches(js, "\\bfetch\\(").Count;
        Assert.Equal(1, fetches);
        Assert.Contains("var CHAT_URL = '/api/chat';", js);
        Assert.Contains("fetch(CHAT_URL", js);
        Assert.Contains("method: 'POST'", js);
        Assert.Contains("'Content-Type': 'application/json'", js);
        Check.NotLeaked(js, "XMLHttpRequest");
    }

    [Fact]
    public async Task ChatScript_HasADoubleSubmitGuard_AndReenablesTheButtonInFinally()
    {
        var js = await GetTextAsync("/js/chat.js");

        Assert.Matches("if \\(sending\\) \\{\\s*return;", js);
        Assert.Contains("sendButton.disabled = value", js);
        // The button must come back even if rendering throws: setSending(false) lives in the last finally block.
        var lastFinally = js[js.LastIndexOf("finally {", StringComparison.Ordinal)..];
        Assert.Contains("setSending(false)", lastFinally);
    }

    [Fact]
    public async Task Page_RendersTheServerTimeoutPlusMargin_ForTheBrowserWaitLimit()
    {
        // The browser waits a little longer than the server's own deadline, so the server's AI_TIMEOUT arrives first.
        var html = await GetTextAsync("/");

        Assert.Contains("data-timeout-seconds=\"200\"", html); // default Ollama:TimeoutSeconds 180 + 20
    }

    [Theory]
    [InlineData("30", "50")]
    [InlineData("1", "21")]
    [InlineData("600", "620")]
    public async Task Page_FollowsTheConfiguredServerTimeout(string configured, string expectedOnPage)
    {
        // The page does not call the AI, so no service override is needed; only the pinned configuration matters.
        var client = factory.WithWebHostBuilder(builder => builder.PinOllama(timeoutSeconds: configured)).CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains($"data-timeout-seconds=\"{expectedOnPage}\"", html);
    }

    [Fact]
    public async Task ChatScript_TakesItsWaitLimitFromThePage_NotFromAFixedNumber()
    {
        var js = await GetTextAsync("/js/chat.js");

        Assert.Contains("core.requestTimeoutMs(form.getAttribute('data-timeout-seconds'))", js);
        Check.NotLeaked(js, "= 200000", "=200000");
    }

    [Fact]
    public async Task ChatCore_RequiresBothResponseAndModelToBeNonEmptyStrings()
    {
        // API contract: { "response": string, "model": string }. A missing or empty model is an incomplete
        // answer (AI_INVALID_RESPONSE). The Node tests (tests/ui) cover the behavior; this keeps the rule
        // from being dropped when only `dotnet test` is run.
        var core = await GetTextAsync("/js/chat-core.js");
        var page = await GetTextAsync("/js/chat.js");

        Assert.Contains("isNonEmptyString(body.response) && isNonEmptyString(body.model)", core);
        Assert.Contains("value.trim() !== ''", core);
        Check.NotLeaked(page, "result.model ?", "|| 'AI'"); // no fallback label for a missing model
    }

    [Fact]
    public async Task ChatCore_ContainsEveryRequiredErrorText()
    {
        var js = await GetTextAsync("/js/chat-core.js");

        foreach (var text in new[]
        {
            "กรุณาพิมพ์ข้อความก่อนส่ง",
            "ไม่สามารถเชื่อมต่อ AI ได้ กรุณาตรวจว่า Ollama เปิดอยู่",
            "AI ใช้เวลาตอบนานเกินไป กรุณาลองใหม่",
            "AI เกิดข้อผิดพลาด กรุณาลองใหม่",
            "ได้รับคำตอบที่ไม่สมบูรณ์ กรุณาลองใหม่",
            "คำตอบยาวเกินกว่าที่ระบบรองรับ กรุณาถามให้สั้นลงหรือแบ่งเป็นหลายคำถาม", // BE-003: AI_RESPONSE_TOO_LARGE
            "ระบบเกิดข้อผิดพลาด กรุณาลองใหม่"
        })
        {
            Assert.Contains(text, js);
        }
    }
}
