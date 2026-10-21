#!/usr/bin/env python3
"""AI-008 / BE-007 review: how many tokens does qwen3:4b really use for a text, compared with the Backend's estimate?

BE-007's Utf8TokenEstimator says: tokens = 4 + ceil((UTF-8 bytes + 2) / 3). A history budget made of such estimates is only safe
when the real count is not higher. This script asks Ollama (loopback, num_predict=1, nothing is generated) for `prompt_eval_count`
of texts of different kinds and sizes and subtracts the count of an empty prompt, so only the text itself is counted.

    python scripts/measure-prompt-tokens.py --output docs/ai/token-estimate-evidence.json
Exit code 0 always when Ollama answered; the findings are in the output file and the printed table.
"""
import argparse
import json
import math
import sys
import time
import urllib.request
from pathlib import Path

MODEL = "qwen3:4b"
SYSTEM = "Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer."
SCHEMA = {"type": "object", "properties": {"answer": {"type": "string"}}, "required": ["answer"], "additionalProperties": False}


def estimate(text: str) -> int:
    """The BE-007 estimator (ChatHistory/ConversationContextBuilder.cs)."""
    return 4 + (len(text.encode("utf-8")) + 2) // 3


def prompt_tokens(base_url, prompt):
    body = {"model": MODEL, "prompt": prompt, "stream": False, "think": False, "system": SYSTEM, "format": SCHEMA,
            "options": {"temperature": 0, "num_predict": 1}}
    req = urllib.request.Request(base_url + "/api/generate", data=json.dumps(body).encode("utf-8"), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.load(r)["prompt_eval_count"]


THAI = "ประเทศไทยตั้งอยู่ในเอเชียตะวันออกเฉียงใต้ มีกรุงเทพมหานครเป็นเมืองหลวง ผู้คนส่วนใหญ่พูดภาษาไทยและนับถือศาสนาพุทธ อาหารไทยเป็นที่นิยมไปทั่วโลก เช่น ต้มยำกุ้ง ผัดไทย และแกงเขียวหวาน "
EN = "The quick brown fox jumps over the lazy dog while the engineers review the quarterly infrastructure budget and plan the migration of the internal chat service. "
KINDS = {
    "english": EN,
    "thai": THAI,
    "mixed_thai_english": THAI[:60] + " API server " + EN[:50] + " ระบบ AI ภายในบริษัท ",
    "digits": "0123456789 3141592653 2718281828 1618033988 ",
    "punctuation_symbols": "{}[]()<>;:,.!?@#$%^&*-_=+/\\|~` \"' ",
    "emoji": "\U0001F600\U0001F680\U0001F4A1\U0001F389\U0001F44D\U0001F525✅❌ ",
    "json_code": '{"id": 12345, "name": "item_a", "tags": ["x", "y"], "ok": true, "ratio": 0.75}\n',
    "url_hex_ids": "https://example.com/a/b?id=550e8400-e29b-41d4-a716-446655440000&t=9f86d081884c7d659a2feaa0c55ad015 ",
    "chinese": "今天天气很好我们一起去公园散步然后在咖啡店喝一杯热茶聊聊最近的工作和生活",
    "thai_digits_mixed": "ราคา 1,250 บาท จำนวน 37 ชิ้น รหัส A12B-3456 วันที่ 21/10/2569 โทร 081-234-5678 ",
}
SIZES = (100, 400, 1500)   # characters


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base-url", default="http://127.0.0.1:11434")
    ap.add_argument("--output", required=True)
    args = ap.parse_args()
    if not args.base_url.startswith(("http://127.0.0.1", "http://localhost", "http://[::1]")):
        print("Only loopback Ollama addresses are allowed.", file=sys.stderr)
        return 2
    try:
        baseline = prompt_tokens(args.base_url, ".")  # one token of text plus system prompt and chat template
        one = prompt_tokens(args.base_url, ". .")
    except Exception as exc:
        print(f"Ollama is not reachable: {exc}", file=sys.stderr)
        return 2
    overhead = baseline - 1 if baseline > 1 else baseline
    rows = []
    for kind, unit in KINDS.items():
        for size in SIZES:
            text = (unit * (size // max(1, len(unit)) + 1))[:size]
            real = max(0, prompt_tokens(args.base_url, text) - overhead)
            est = estimate(text)
            rows.append({"kind": kind, "chars": len(text), "bytes": len(text.encode("utf-8")), "real_tokens": real, "estimated_tokens": est,
                         "real_over_estimate": round(real / est, 2), "chars_per_real_token": round(len(text) / real, 2) if real else None})
    worst = max(rows, key=lambda r: r["real_over_estimate"])
    print(f"{'kind':22} {'chars':>6} {'bytes':>6} {'real':>6} {'est':>6} {'real/est':>8}")
    for r in rows:
        flag = "  <-- estimate too LOW" if r["real_over_estimate"] > 1 else ""
        print(f"{r['kind']:22} {r['chars']:>6} {r['bytes']:>6} {r['real_tokens']:>6} {r['estimated_tokens']:>6} {r['real_over_estimate']:>8}{flag}")
    out = {"generated_utc": time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime()), "model": MODEL, "template_and_system_overhead_tokens": overhead,
           "estimator": "4 + ceil((utf8_bytes + 2) / 3)", "rows": rows, "worst": worst,
           "underestimated_kinds": sorted({r["kind"] for r in rows if r["real_over_estimate"] > 1})}
    Path(args.output).write_text(json.dumps(out, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\nTemplate + system prompt overhead: {overhead} tokens. Underestimated kinds: {out['underestimated_kinds'] or 'none'}. Worst: {worst['kind']} x{worst['real_over_estimate']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
