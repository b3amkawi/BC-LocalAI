# FE-002 - Review checklist

Issue: #18 · Branch: `watzapon_FE-002` · Details: [FE-002-STREAMING-UI.md](FE-002-STREAMING-UI.md), [FE-002-VERIFICATION.md](FE-002-VERIFICATION.md)

## 1. Automated checks

```powershell
dotnet test .\BC-LocalAI.Tests\BC-LocalAI.Tests.csproj     # expect 428 passed
node --test "tests/ui/*.test.mjs"                          # expect 61 passed
```

## 2. Try it (Ollama running with qwen3:4b)

```powershell
dotnet run --project .\BC-LocalAI\BC-LocalAI.csproj --launch-profile http
```

Open <http://localhost:5290>.

| # | Do | Expect | OK |
|---|----|--------|----|
| 1 | Ask "อธิบาย Local AI สั้นๆ" | text appears little by little in one bubble; "AI กำลังพิมพ์คำตอบ…" and a blinking bar; **ส่ง** becomes **หยุดสร้างคำตอบ**; at the end the status disappears and **ส่ง** is back | [ ] |
| 2 | Ask "Write the numbers from 1 to 250 separated by commas" and press **หยุดสร้างคำตอบ** after 2 s | text so far stays, dashed border, "หยุดการสร้างคำตอบแล้ว"; no red error | [ ] |
| 3 | Same as 2 but press **Esc** | same as 2 (or, if no text yet, a small note "หยุดการสร้างคำตอบแล้ว") | [ ] |
| 4 | Ask "เขียนรายชื่อจังหวัดทั้ง 77 จังหวัดของประเทศไทย" | text appears, then "คำตอบไม่สมบูรณ์" and the red text "คำตอบยาวเกินกว่าที่ระบบรองรับ …"; the question is back in the input | [ ] |
| 5 | Ask something long, scroll up while it writes | the page does not jump back down while you read | [ ] |
| 6 | Press Enter with an empty input | "กรุณาพิมพ์ข้อความก่อนส่ง", nothing sent | [ ] |
| 7 | Stop the app and send a question | "ไม่สามารถเชื่อมต่อเซิร์ฟเวอร์ได้ …"; the question is back | [ ] |
| 8 | Narrow the window to phone width while an answer is written | Stop button visible, no sideways scrolling | [ ] |
| 9 | Ask the model to answer with `<b>bold</b>` | the tags are shown as text, not as bold | [ ] |

## 3. Code review points

- [ ] `chat.js` calls only `/api/chat/stream`, one `fetch`, with an `AbortController` signal; no `EventSource`, `XMLHttpRequest`, `innerHTML`.
- [ ] `chat-stream.js` has no DOM access; contract rules match `docs/BE-004-SSE-CONTRACT.md`.
- [ ] An answer is never shown as complete unless `completed` arrived after `started` and a non-blank text.
- [ ] The page always returns to idle (`finally`), one answer at a time.
- [ ] Out of scope kept: no backend change, no chat history, no authentication, no new dependency.

## 4. Points for the owner

- Ollama hung once after a cancellation (verification section 4); not reproducible, recovered with `ollama stop qwen3:4b`. A first-record/idle
  timeout for streaming (AI-005 proposal) would be a backend decision.
- Only Chromium was tested; say if Firefox / Safari / Edge must be checked as well.
