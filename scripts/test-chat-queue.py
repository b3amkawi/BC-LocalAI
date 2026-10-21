"""BE-008 live check of the chat queue against a running backend and a real Ollama.

Sends synthetic questions only. Needs Authentication:Enabled=false (no login is performed here) and, to see the
503 path rather than the 429 one, the backend started with ChatQueue__MaxPendingPerUser raised (with authentication
off every caller shares one quota). Example:

    $env:ChatQueue__MaxPendingPerUser = "16"; $env:ChatQueue__MaxQueueLength = "4"
    dotnet run --project BC-LocalAI --urls http://127.0.0.1:5290
    python -I -X utf8 scripts/test-chat-queue.py --base-url http://127.0.0.1:5290 --requests 12 --output results.json

It records, per request: endpoint, HTTP status, Retry-After, error code, queue wait as seen by the client (time to the
first byte of the answer for streams) and total time. Questions and answers are not written to the output file.
"""
import argparse
import http.client
import json
import threading
import time
import urllib.parse

QUESTIONS = ["1+1 เท่ากับเท่าไร ตอบสั้น ๆ", "What is 2+3? Answer with one number.", "ตอบคำเดียว: น้ำเดือดที่กี่องศาเซลเซียส"]


def one(base, index, stream, results, start_gate):
    url = urllib.parse.urlparse(base)
    path = "/api/chat/stream" if stream else "/api/chat"
    body = json.dumps({"message": QUESTIONS[index % len(QUESTIONS)]}).encode()
    start_gate.wait()
    began = time.perf_counter()
    entry = {"index": index, "endpoint": path}
    try:
        connection = http.client.HTTPConnection(url.hostname, url.port, timeout=240)
        connection.request("POST", path, body, {"Content-Type": "application/json"})
        response = connection.getresponse()
        entry["status"] = response.status
        entry["retryAfter"] = response.getheader("Retry-After")
        first = response.read(1)
        entry["firstByteSeconds"] = round(time.perf_counter() - began, 3)
        raw = first + response.read()
        text = raw.decode("utf-8", "replace")
        if response.status == 200 and stream:
            events = [block.split("\n", 1)[0].replace("event: ", "") for block in text.split("\n\n") if block.strip()]
            entry["events"] = events
            entry["terminal"] = events[-1] if events else None
            if "error" in events:
                entry["errorCode"] = json.loads(text.split("event: error\ndata: ", 1)[1].split("\n", 1)[0]).get("code")
        elif response.status != 200:
            entry["errorCode"] = json.loads(text).get("error", {}).get("code")
    except Exception as exception:  # a failed connection is a result too
        entry["status"] = None
        entry["failure"] = type(exception).__name__
    entry["totalSeconds"] = round(time.perf_counter() - began, 3)
    results.append(entry)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", default="http://127.0.0.1:5290")
    parser.add_argument("--requests", type=int, default=12)
    parser.add_argument("--stream-share", type=float, default=0.5, help="fraction of requests sent to /api/chat/stream")
    parser.add_argument("--output", default="chat-queue-results.json")
    args = parser.parse_args()

    results, gate, threads = [], threading.Event(), []
    for i in range(args.requests):
        stream = (i % 2 == 0) if args.stream_share == 0.5 else (i < args.requests * args.stream_share)
        thread = threading.Thread(target=one, args=(args.base_url, i, stream, results, gate))
        threads.append(thread)
        thread.start()
    gate.set()
    for thread in threads:
        thread.join()

    results.sort(key=lambda r: r["index"])
    summary = {}
    for r in results:
        key = f'{r["endpoint"]} -> {r.get("status")} {r.get("errorCode") or r.get("terminal") or ""}'.strip()
        summary[key] = summary.get(key, 0) + 1
    print(json.dumps(summary, indent=2, ensure_ascii=False))
    with open(args.output, "w", encoding="utf-8") as handle:
        json.dump({"summary": summary, "requests": results}, handle, indent=2, ensure_ascii=False)


if __name__ == "__main__":
    main()
