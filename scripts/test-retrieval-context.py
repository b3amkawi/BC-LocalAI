#!/usr/bin/env python3
"""Explicit stress probe: compare identical synthetic prompts at 8192 and 4096.

This deliberately bypasses the normal request budget. Never use on real data or
as a production request path. No global Ollama configuration is changed.
"""
import argparse
from datetime import datetime, timezone
import importlib.util
from pathlib import Path
import time

from retrieval_eval import ValidationError, fingerprint
from retrieval_live import loopback_url, parse_generation, request_json

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('offline_context_cli', ROOT / 'scripts/test-retrieval.py')
offline = importlib.util.module_from_spec(spec)
spec.loader.exec_module(offline)


def build_probe(padding_words):
    if type(padding_words) is not int or not 0 <= padding_words <= 6000:
        raise ValidationError('invalid_probe_size')
    return {
        'model': 'qwen3:4b', 'stream': False, 'think': False,
        'system': 'Answer from the synthetic document only. Return JSON with first and last string fields.',
        'prompt': ('Synthetic document SYN-BOUNDARY. The first marker is ALPHA_47.\n'
                   + ' pad' * padding_words
                   + '\nThe last marker is OMEGA_83.\n'
                   'What are the first and last marker values? Copy them exactly.'),
        'format': {'type': 'object', 'properties': {'first': {'type': 'string'}, 'last': {'type': 'string'}},
                   'required': ['first', 'last'], 'additionalProperties': False},
        'options': {'temperature': 0, 'num_predict': 256},
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--allow-boundary-probe', action='store_true')
    parser.add_argument('--base-url', default='http://127.0.0.1:11434')
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--repeats', type=int, default=3, choices=range(1, 6))
    args = parser.parse_args(argv)
    try:
        if not args.allow_boundary_probe:
            raise ValidationError('boundary_probe_opt_in_required')
        base = loopback_url(args.base_url)
        if args.output.exists() or not args.output.parent.is_dir():
            raise ValidationError('new_output_in_existing_directory_required')
        report = {'schema_version': 1, 'scope': 'synthetic_context_stress_not_production',
                  'issue_complete': False, 'started_utc': datetime.now(timezone.utc).isoformat(),
                  'ollama_version': request_json(base, '/api/version'), 'rows': [], 'completed': False,
                  'method': 'Identical prompt, larger-context reference vs 4096; counts are observations, not a universal truncation detector'}
        offline.write_json(args.output, report)
        # Each ' pad' is expected to be a token but classification uses observed counts.
        for words in (500, 3700, 4500):
            for repeat in range(args.repeats):
                prompt = build_probe(words)
                pair = []
                for ctx in (8192, 4096):
                    request = {**prompt, 'options': {**prompt['options'], 'num_ctx': ctx}}
                    row = {'padding_words': words, 'repeat': repeat + 1, 'num_ctx': ctx,
                           'request_sha256': fingerprint(request), 'request': request, 'errors': []}
                    started = time.perf_counter()
                    try:
                        envelope = request_json(base, '/api/generate', request, timeout=90)
                        row['provider_envelope'] = envelope
                        parsed = parse_generation(envelope)
                        row['first_recalled'] = parsed.get('first') == 'ALPHA_47'
                        row['last_recalled'] = parsed.get('last') == 'OMEGA_83'
                        row['passed'] = (set(parsed) == {'first', 'last'} and row['first_recalled']
                                         and row['last_recalled'])
                    except (ValidationError, AttributeError) as error:
                        row['errors'] = [str(error) if isinstance(error, ValidationError) else 'invalid_probe_object']
                        row['passed'] = False
                    row['elapsed_seconds'] = round(time.perf_counter() - started, 4)
                    report['rows'].append(row)
                    pair.append(row)
                    offline.write_json(args.output, report)
                    print(f"padding={words} repeat={repeat + 1} ctx={ctx} recall={row['passed']}", flush=True)
                reference = pair[0].get('provider_envelope', {}).get('prompt_eval_count')
                actual = pair[1].get('provider_envelope', {}).get('prompt_eval_count')
                pair[1]['fewer_prompt_tokens_than_reference'] = (
                    actual < reference if type(reference) is int and type(actual) is int else None)
        report['completed'] = True
        report['finished_utc'] = datetime.now(timezone.utc).isoformat()
        offline.write_json(args.output, report)
        # Over-budget failures are measurements, never converted into a passing recall claim.
        return 0 if all(row['passed'] for row in report['rows']) else 1
    except (ValidationError, OSError) as error:
        print(str(error) if isinstance(error, ValidationError) else 'artifact_io_error', flush=True)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
