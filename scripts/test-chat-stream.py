"""Repeatable BE-004 live verification. Uses synthetic prompts and writes evidence only to --output.

Start the Production backend first; requires a local Ollama with qwen3:4b.
"""
import argparse
import concurrent.futures
import json
import time
import urllib.request
import urllib.parse
from pathlib import Path


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, new_url):
        return None


HTTP = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())


def stream_case(base_url, message, cancel=False, expected_error=None):
    request = urllib.request.Request(
        base_url + "/api/chat/stream",
        data=json.dumps({"message": message}, ensure_ascii=False).encode("utf-8"),
        headers={"Content-Type": "application/json", "Accept": "text/event-stream"},
    )
    started = time.perf_counter()
    events = []
    event_name = None
    with HTTP.open(request, timeout=190) as response:
        assert response.status == 200, response.status
        assert response.headers.get_content_type() == "text/event-stream"
        for raw in response:
            line = raw.decode("utf-8").rstrip("\r\n")
            if line.startswith("event: "):
                event_name = line[7:]
            elif line.startswith("data: "):
                data = json.loads(line[6:])
                events.append({"event": event_name, "data": data,
                               "elapsed_ms": round((time.perf_counter() - started) * 1000, 1)})
                if cancel and event_name == "delta":
                    break  # Closing the HTTP response must cancel upstream generation.
    assert events[0]["event"] == "started"
    assert events[0]["data"]["model"] == "qwen3:4b"
    terminal = [item for item in events if item["event"] in ("completed", "error", "cancelled")]
    text = "".join(item["data"]["text"] for item in events if item["event"] == "delta")
    if cancel:
        assert text and not terminal
    elif expected_error:
        assert len(terminal) == 1 and terminal[0]["event"] == "error", events
        assert terminal[0]["data"]["code"] == expected_error, terminal
    else:
        assert len(terminal) == 1 and terminal[0]["event"] == "completed", events
        assert terminal[0]["data"]["finishReason"] == "stop"
        assert text.strip() and not text.lstrip().startswith('{"answer"')
    assert "<think>" not in text.lower() and "</think>" not in text.lower()
    return {"message": message, "answer": text, "events": events, "cancelled_by_client": cancel}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", default="http://127.0.0.1:5290")
    parser.add_argument("--output", required=True)
    parser.add_argument("--expect-limit", action="store_true", help="Run against a separate instance with MaxOutputTokens=16")
    args = parser.parse_args()
    origin = urllib.parse.urlsplit(args.base_url)
    if (origin.scheme not in ("http", "https") or origin.hostname not in ("127.0.0.1", "localhost", "::1")
            or origin.path not in ("", "/") or origin.username or origin.password or origin.query or origin.fragment):
        parser.error("--base-url must be a loopback backend origin")
    args.base_url = args.base_url.rstrip("/")
    results = []
    if args.expect_limit:
        results.append(stream_case(args.base_url, "List the integers from 1 to 500, separated by commas.",
                                   expected_error="AI_RESPONSE_TOO_LARGE"))
    else:
        results.append(stream_case(args.base_url, "หนึ่งบวกหนึ่งเท่ากับเท่าไร ตอบสั้น ๆ เป็นภาษาไทย"))
        assert "2" in results[-1]["answer"] or "สอง" in results[-1]["answer"]
        results.append(stream_case(args.base_url, "What is the capital of Japan? Answer briefly in English."))
        assert "tokyo" in results[-1]["answer"].lower()
        results.append(stream_case(args.base_url, "สวัสดี 😀\nช่วยทักทายสั้น ๆ เป็นภาษาไทย"))
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            concurrent_results = list(pool.map(lambda prompt: stream_case(args.base_url, prompt),
                ["Compute 2 + 3. Answer only the number.", "Compute 4 + 5. Answer only the number."]))
        assert concurrent_results[0]["answer"].strip() == "5"
        assert concurrent_results[1]["answer"].strip() == "9"
        results.extend(concurrent_results)
        results.append(stream_case(args.base_url, "List the integers from 1 to 500, separated by commas.", cancel=True))
        results.append(stream_case(args.base_url, "Compute 3 + 4. Answer only the number."))
        assert results[-1]["answer"].strip() == "7"
        request = urllib.request.Request(args.base_url + "/api/chat",
            data=json.dumps({"message": "Compute 1 + 1. Answer only the number."}).encode(),
            headers={"Content-Type": "application/json"})
        with HTTP.open(request, timeout=190) as response:
            legacy = json.load(response)
            assert legacy == {"response": "2", "model": "qwen3:4b"}, legacy
        results.append({"non_streaming": legacy})
    evidence = {"base_url": args.base_url, "passed": True, "checks": len(results), "results": results}
    Path(args.output).write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"passed": True, "checks": len(results), "output": args.output}))


if __name__ == "__main__":
    main()
