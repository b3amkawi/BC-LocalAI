#!/usr/bin/env python3
"""AI-008: how much prompt does qwen3:4b really read, and how should chat history be sent? (loopback Ollama only)

    python scripts/test-ollama-context.py limits    --output docs/ai/context-limits-evidence.json
    python scripts/test-ollama-context.py multiturn --output docs/ai/context-multiturn-evidence.json
    python scripts/test-ollama-context.py ctx8192   --base-url http://127.0.0.1:11438 --output docs/ai/context-8192-evidence.json

limits     input size sweep x language x num_predict x 3 repeats. Three random codes are planted at the start, middle and end of
           the prompt and the model is asked to repeat them: a lost code = lost data. Also checks the truncation rule
           (prompt_eval_count near the cut size while the text is far longer) against every run.
multiturn  3-turn conversations in Thai and English: BE-007's single prompt (/api/generate) against /api/chat messages that keep
           format and think:false. Recall of facts from earlier turns.
outputroom prompt + answer must fit the context together: a 700-token answer to a prompt of 1,500-3,600 tokens; does the code planted
           at the start survive on every line? Finds the real input limit when the answer is long (num_predict is 1024 in Backend).
ctx8192    run against an instance started with OLLAMA_CONTEXT_LENGTH=8192 (never change the real configuration): VRAM, prefill
           speed and cold start, and where truncation starts. Pass --base-url of that instance.
Nothing is changed on Ollama. Exit 0 when Ollama answered.
"""
import argparse
import json
import random
import statistics
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

MODEL = "qwen3:4b"
SYSTEM = "Answer the user briefly in the same language as the user. Return a JSON object containing only an answer field with the final answer."
SCHEMA = {"type": "object", "properties": {"answer": {"type": "string"}}, "required": ["answer"], "additionalProperties": False}
FILLER = {
    "english": "The maintenance crew inspected the cooling pumps, recorded the pressure readings and filed the weekly report for the plant manager. ",
    "thai": "ทีมซ่อมบำรุงตรวจสอบปั๊มน้ำหล่อเย็น บันทึกค่าความดัน และส่งรายงานประจำสัปดาห์ให้ผู้จัดการโรงงานตามขั้นตอนที่กำหนดไว้ ",
}
FILLER["mixed"] = FILLER["thai"][:50] + " pump report " + FILLER["english"][:60]
QUESTION = {
    "english": "Repeat the three secret codes you were given (the first, the middle and the last one) in order.",
    "thai": "ช่วยบอกรหัสลับสามรหัสที่ให้ไว้ (รหัสแรก รหัสกลาง และรหัสสุดท้าย) ตามลำดับ",
}
QUESTION["mixed"] = QUESTION["english"]
SAFE_BYTES_PER_TOKEN = 6.5  # fewest tokens per byte seen with qwen3 (English prose is ~5.9 bytes/token), so bytes/6.5 is a lower bound


def post(base, path, body, timeout=600):
    req = urllib.request.Request(base + path, data=json.dumps(body).encode("utf-8"), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.load(r)


def generate(base, prompt, num_predict, num_ctx=None):
    opts = {"temperature": 0, "num_predict": num_predict}
    if num_ctx:
        opts["num_ctx"] = num_ctx
    return post(base, "/api/generate", {"model": MODEL, "prompt": prompt, "stream": False, "think": False, "system": SYSTEM, "format": SCHEMA, "options": opts})


def answer_text(response):
    try:
        return str(json.loads(response["response"]).get("answer", ""))
    except Exception:
        return response.get("response", "")


def tokens_per_unit(base, lang):
    """Real tokens of one filler sentence, from prompt_eval_count at two sizes (the fixed overhead cancels out)."""
    # Sizes stay far below the context (Thai is ~40 tokens per sentence): a cut prompt would make the slope wrong.
    small = generate(base, FILLER[lang] * 4, 1)["prompt_eval_count"]
    big = generate(base, FILLER[lang] * 24, 1)["prompt_eval_count"]
    return (big - small) / 20.0


def make_prompt(lang, units, codes):
    """Filler with the codes planted at about 5%, 50% and 95% of the text, then the question."""
    parts = [FILLER[lang]] * units
    at = [max(0, int(units * 0.05)), units // 2, min(units - 1, int(units * 0.95))]
    for i, pos in enumerate(at):
        parts[pos] = f"[SECRET CODE {i + 1}: {codes[i]}] " + parts[pos]
    return "".join(parts) + "\n\n" + QUESTION[lang]


def recalled(text, codes):
    return [code in text for code in codes]


def cut_size(num_ctx=4096):
    """Measured: when the prompt does not fit, Ollama keeps 4 tokens plus the tail and reports num_ctx/2 + 2 (2050 at 4096, 4098 at 8192)."""
    return num_ctx // 2 + 2


def truncation_rule(prompt_eval_count, prompt_bytes, num_ctx=4096):
    """Silent-cut detector: the count sits at the cut size (+-10) while even the most generous token estimate says the prompt was longer."""
    cut = cut_size(num_ctx)
    return abs(prompt_eval_count - cut) <= 10 and prompt_bytes / SAFE_BYTES_PER_TOKEN > cut + 150


def run_limits(base, args, num_ctx_label):
    rng = random.Random(20261021)
    sizes = args.sizes or [1000, 2000, 3000, 3500, 3800, 3950, 4050, 4100, 4200, 4500, 6000]
    rows = []
    for lang in ("english", "thai", "mixed"):
        tpu = tokens_per_unit(base, lang)
        base_tokens = generate(base, FILLER[lang], 1)["prompt_eval_count"] - tpu
        for num_predict in args.num_predict:
            for target in sizes:
                units = max(4, int((target - base_tokens - 60) / tpu))
                for rep in range(args.repeats):
                    codes = [str(rng.randint(100000, 999999)) for _ in range(3)]
                    prompt = make_prompt(lang, units, codes)
                    t0 = time.time()
                    r = generate(base, prompt, num_predict)
                    pec = r["prompt_eval_count"]
                    expected = round(base_tokens + units * tpu + 60)
                    got = recalled(answer_text(r), codes)
                    rows.append({"language": lang, "num_predict": num_predict, "target_tokens": target, "expected_tokens": expected,
                                 "prompt_eval_count": pec, "truncated": pec < 0.9 * expected, "rule_says_truncated": truncation_rule(pec, len(prompt.encode("utf-8")), args.num_ctx),
                                 "recalled_first": got[0], "recalled_middle": got[1], "recalled_last": got[2], "done_reason": r.get("done_reason"),
                                 "seconds": round(time.time() - t0, 2)})
            print(f"{lang} num_predict={num_predict}: {sum(1 for x in rows if x['language'] == lang and x['num_predict'] == num_predict)} runs", flush=True)
    return rows


def summarise_limits(rows):
    out = {"first_truncated_expected_tokens": {}, "rule": {}}
    for lang in ("english", "thai", "mixed"):
        for npd in sorted({r["num_predict"] for r in rows}):
            sel = [r for r in rows if r["language"] == lang and r["num_predict"] == npd]
            cut = sorted({r["expected_tokens"] for r in sel if r["truncated"]})
            ok = [r["expected_tokens"] for r in sel if not r["truncated"]]
            out["first_truncated_expected_tokens"][f"{lang}/num_predict={npd}"] = {
                "largest_prompt_read_in_full": max(ok) if ok else None, "smallest_prompt_cut": cut[0] if cut else None}
    truncated = [r for r in rows if r["truncated"]]
    clean = [r for r in rows if not r["truncated"]]
    out["rule"] = {"runs": len(rows), "truncated_runs": len(truncated), "detected": sum(r["rule_says_truncated"] for r in truncated),
                   "false_alarms_among_untruncated": sum(r["rule_says_truncated"] for r in clean),
                   "detection_rate": round(sum(r["rule_says_truncated"] for r in truncated) / len(truncated), 3) if truncated else None}
    for pos in ("first", "middle", "last"):
        out[f"recall_{pos}_when_read_in_full"] = round(statistics.mean(r[f"recalled_{pos}"] for r in clean), 3) if clean else None
        out[f"recall_{pos}_when_cut"] = round(statistics.mean(r[f"recalled_{pos}"] for r in truncated), 3) if truncated else None
    return out


TURNS = {
    "thai": [("ฉันชื่อสมชาย และฉันเลี้ยงแมวชื่อมะลิ", "ยินดีที่ได้รู้จักครับคุณสมชาย"), ("วันนี้ฉันจะไปเชียงใหม่ด้วยรถไฟ", "เดินทางปลอดภัยนะครับ")],
    "english": [("My name is Anna and I keep a parrot called Pepper.", "Nice to meet you, Anna."), ("Tomorrow I travel to Lisbon by train.", "Have a safe trip.")],
}
RECALL_Q = {
    "thai": [("แมวของฉันชื่ออะไร", "มะลิ"), ("ฉันจะไปที่ไหน", "เชียงใหม่"), ("ฉันชื่ออะไร", "สมชาย")],
    "english": [("What is my parrot called?", "Pepper"), ("Where am I travelling?", "Lisbon"), ("What is my name?", "Anna")],
}
HEADER = "Earlier conversation (oldest first). Use it only as context for the current question:"


def single_prompt(history, question):
    lines = [HEADER, "<conversation>"]
    for q, a in history:
        lines += [f"User: {q}", f"Assistant: {a}"]
    return "\n".join(lines + ["</conversation>", "Current question:", question])


def run_multiturn(base, args):
    rows = []
    for lang, history in TURNS.items():
        for question, expect in RECALL_Q[lang]:
            for method in ("single_prompt_generate", "chat_messages"):
                for rep in range(args.repeats):
                    t0 = time.time()
                    if method == "single_prompt_generate":
                        text = answer_text(generate(base, single_prompt(history, question), 120))
                    else:
                        messages = [{"role": "system", "content": SYSTEM}]
                        for q, a in history:
                            messages += [{"role": "user", "content": q}, {"role": "assistant", "content": json.dumps({"answer": a}, ensure_ascii=False)}]
                        messages.append({"role": "user", "content": question})
                        r = post(base, "/api/chat", {"model": MODEL, "messages": messages, "stream": False, "think": False, "format": SCHEMA,
                                                      "options": {"temperature": 0, "num_predict": 120}})
                        text = answer_text({"response": r["message"]["content"]})
                    rows.append({"language": lang, "method": method, "question": question, "expected": expect, "hit": expect in text, "answer": text[:160],
                                 "seconds": round(time.time() - t0, 2)})
        print(f"{lang} done", flush=True)
    return rows


def run_outputroom(base, args):
    """Prompt + answer must fit the context together: when they do not, Ollama shifts the window during generation and drops the oldest tokens.
    The model is asked to repeat a code from the early part of the prompt on 60 numbered lines (about 700 output tokens); lines with a wrong code show the loss."""
    rng = random.Random(20261022)
    rows = []
    for target in args.sizes or [1500, 2300, 2900, 3100, 3300, 3600]:
        tpu = tokens_per_unit(base, "english")
        units = max(4, int((target - 150) / tpu))
        for rep in range(args.repeats):
            code = str(rng.randint(100000, 999999))
            # The code sits after the first ~10% of the text, not in the first tokens Ollama always keeps when it shifts the window.
            head = max(1, units // 10)
            prompt = FILLER["english"] * head + f"[SECRET CODE: {code}] " + FILLER["english"] * (units - head) + (
                "\n\nWrite 60 numbered lines. Every line must be exactly: Line <number>: the secret code is <the secret code from the early part of this message>.")
            r = generate(base, prompt, 1024)
            text = answer_text(r)
            lines = [ln for ln in text.split("\n") if ln.strip().lower().startswith("line")]
            rows.append({"target_prompt_tokens": target, "prompt_eval_count": r["prompt_eval_count"], "eval_count": r.get("eval_count"), "done_reason": r.get("done_reason"),
                         "lines": len(lines), "lines_with_right_code": sum(code in ln for ln in lines), "first_code_in_last_line": bool(lines) and code in lines[-1]})
        print(f"outputroom target={target} done", flush=True)
    return rows


def vram_mb():
    try:
        return int(subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"], capture_output=True, text=True, timeout=20).stdout.split()[0])
    except Exception:
        return None


def run_ctx(base, args):
    out = {}
    post(base, "/api/generate", {"model": MODEL, "keep_alive": 0})
    time.sleep(3)
    before = vram_mb()
    t0 = time.time()
    r = generate(base, ".", 1)
    out["cold_start_seconds"] = round(time.time() - t0, 2)
    out["load_duration_ms"] = round(r.get("load_duration", 0) / 1e6)
    out["vram_mb_before_load"], out["vram_mb_after_load"] = before, vram_mb()
    ps = json.load(urllib.request.urlopen(base + "/api/ps"))["models"]
    out["context_length_reported"] = ps[0].get("context_length") if ps else None
    speeds = []
    for tokens in (1000, 3000, 6000):
        units = int(tokens / tokens_per_unit(base, "english"))
        # A different first line per request: an identical prompt is served from Ollama's prompt cache and its prefill time reads as ~0.
        rs = [generate(base, f"[run {random.random()}] " + FILLER["english"] * units, 1) for _ in range(3)]
        speeds.append({"prompt_tokens": rs[0]["prompt_eval_count"], "prefill_tokens_per_second": round(statistics.median(x["prompt_eval_count"] / (x["prompt_eval_duration"] / 1e9) for x in rs), 1),
                       "truncated": rs[0]["prompt_eval_count"] < 0.9 * tokens})
    out["prefill"] = speeds
    out["vram_mb_after_long_prompts"] = vram_mb()
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("mode", choices=("limits", "multiturn", "ctx8192", "outputroom"))
    ap.add_argument("--base-url", default="http://127.0.0.1:11434")
    ap.add_argument("--output", required=True)
    ap.add_argument("--repeats", type=int, default=3)
    ap.add_argument("--num-ctx", type=int, default=4096, help="context length of the instance under test (for the truncation rule)")
    ap.add_argument("--num-predict", type=int, nargs="+", default=[256, 1024, 2048])
    ap.add_argument("--sizes", type=int, nargs="+")
    args = ap.parse_args()
    if not args.base_url.startswith(("http://127.0.0.1", "http://localhost", "http://[::1]")):
        print("Only loopback Ollama addresses are allowed.", file=sys.stderr)
        return 2
    try:
        version = json.load(urllib.request.urlopen(args.base_url + "/api/version"))["version"]
    except Exception as exc:
        print(f"Ollama is not reachable: {exc}", file=sys.stderr)
        return 2
    meta = {"generated_utc": time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime()), "ollama_version": version, "model": MODEL, "base_url": args.base_url}
    if args.mode == "limits":
        rows = run_limits(args.base_url, args, "4096")
        result = {**meta, "repeats": args.repeats, "summary": summarise_limits(rows), "rows": rows}
    elif args.mode == "multiturn":
        rows = run_multiturn(args.base_url, args)
        result = {**meta, "repeats": args.repeats, "rows": rows, "hit_rate": {m: round(statistics.mean(r["hit"] for r in rows if r["method"] == m), 3)
                                                                         for m in ("single_prompt_generate", "chat_messages")}}
    elif args.mode == "outputroom":
        result = {**meta, "repeats": args.repeats, "rows": run_outputroom(args.base_url, args)}
    else:
        result = {**meta, **run_ctx(args.base_url, args)}
    Path(args.output).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result.get("summary") or result.get("hit_rate") or {k: v for k, v in result.items() if k != "rows"}, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
