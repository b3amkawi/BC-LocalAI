#!/usr/bin/env python3
"""Exploratory AI-010 synthetic evaluation; explicitly opt in to local GPU calls."""
import argparse
import copy
from collections import Counter
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import time

from retrieval_eval import (ValidationError, fingerprint, grade_answer, load_json,
                            prepare_case, retrieve, validate_dataset)
from retrieval_live import loopback_url, parse_generation, prepare_live_request, request_json

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('offline_cli', ROOT / 'scripts/test-retrieval.py')
offline = importlib.util.module_from_spec(spec)
spec.loader.exec_module(offline)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--allow-live', action='store_true')
    parser.add_argument('--base-url', default='http://127.0.0.1:11434')
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--suite', choices=('quality', 'injection'), default='quality')
    parser.add_argument('--case-id', action='append')
    parser.add_argument('--timeout', type=float, default=90)
    parser.add_argument('--constrained-sources', action='store_true',
                        help='Experimental schema constrained to retrieved IDs; never ground truth')
    parser.add_argument('--constrained-units', action='store_true',
                        help='Closed-format prototype: numeric unit pattern derived only from the query')
    args = parser.parse_args(argv)
    try:
        if not args.allow_live:
            raise ValidationError('live_opt_in_required')
        base = loopback_url(args.base_url)
        corpus = load_json(ROOT / 'tests/retrieval/documents.json')
        dataset = load_json(ROOT / 'tests/retrieval/questions.json')
        attacks = load_json(ROOT / 'tests/retrieval/injection-cases.json')
        validate_dataset(dataset, corpus)
        # Require a new artifact so a pilot can never overwrite a complete run.
        if args.output.exists() or not args.output.parent.is_dir():
            raise ValidationError('new_output_in_existing_directory_required')
        all_cases = dataset['cases'] if args.suite == 'quality' else attacks['cases']
        known_ids = {case['id'] for case in all_cases}
        if args.case_id and (len(set(args.case_id)) != len(args.case_id) or not set(args.case_id) <= known_ids):
            raise ValidationError('invalid_case_selection')
        chosen = [case for case in all_cases if not args.case_id or case['id'] in args.case_id]
        version = request_json(base, '/api/version', timeout=args.timeout)
        tags = request_json(base, '/api/tags', timeout=args.timeout)
        if (not isinstance(version, dict) or not isinstance(version.get('version'), str)
                or not version['version'].strip() or not isinstance(tags, dict)
                or not isinstance(tags.get('models'), list)
                or not all(isinstance(model, dict) for model in tags['models'])):
            raise ValidationError('invalid_provider_metadata')
        models = [model for model in tags.get('models', []) if model.get('name') == 'qwen3:4b']
        if len(models) != 1:
            raise ValidationError('required_model_missing')
        report = {
            'schema_version': 1, 'started_utc': datetime.now(timezone.utc).isoformat(),
            'scope': 'exploratory_live_synthetic', 'issue_complete': False,
            'dependency_review_pending': ['AI-008', 'ARCH-001'],
            'live_verified': True, 'suite': args.suite, 'base_url': base,
            'ollama_version': version.get('version'), 'model_digest': models[0].get('digest'),
            'dataset_sha256': fingerprint(dataset), 'corpus_sha256': fingerprint(corpus),
            'injection_dataset_sha256': fingerprint(attacks),
            'selected_count': len(chosen), 'full_suite_count': len(all_cases),
            'context_policy': 'provisional_utf8_preflight_not_approved_tokenizer_policy',
            'num_ctx': 4096, 'num_predict': 1024, 'rows': [], 'completed': False,
            'profile': ('constrained_units_v2' if args.constrained_units else
                        'constrained_sources_v1' if args.constrained_sources else 'baseline_v1'),
            'acl_local_checks': [],
        }
        # Synthetic retrieval boundary only; no application authorization claim.
        for role, expected in [('reader', ['SYN-R01']), ('engineer', ['SYN-R01', 'SYN-R02']),
                               ('admin', ['SYN-R01', 'SYN-R02', 'SYN-R03'])]:
            docs = retrieve('SYN-ACL-BOUNDARY', corpus, role)['documents']
            ids = sorted(doc['id'] for doc in docs)
            report['acl_local_checks'].append({'role': role, 'ids': ids, 'passed': ids == expected})
        offline.write_json(args.output, report)
        for selected in chosen:
            case = selected if args.suite == 'quality' else next(
                case for case in dataset['cases'] if case['id'] == selected['base_case_id'])
            local_corpus = copy.deepcopy(corpus)
            if args.suite == 'injection':
                source = next(doc for doc in local_corpus['documents'] if doc['id'] == case['expected']['sources'][0])
                source['text'] += '\n' + selected['payload']
            prepared = prepare_case(case, local_corpus, max_context_bytes=1400)
            row = {'id': selected['id'], 'category': case['category'], 'language': case['language'],
                   'sent_to_provider': False,
                   'presented_ids': [doc['id'] for doc in prepared['selection']['documents']],
                   'selection': prepared['selection'], 'errors': []}
            started = time.perf_counter()
            try:
                request, size = prepare_live_request(prepared['request_proposal'],
                                                     constrained_sources=args.constrained_sources,
                                                     constrained_units=args.constrained_units)
                row.update(request=request, request_sha256=fingerprint(request), input_utf8_bytes=size)
                row['sent_to_provider'] = True
                envelope = request_json(base, '/api/generate', request, args.timeout)
                row['provider_envelope'] = envelope
                result = parse_generation(envelope)
                row['result'] = result
                row['errors'] = grade_answer(case, result, row['presented_ids'])
            except ValidationError as error:
                row['errors'] = [str(error)]
            row['elapsed_seconds'] = round(time.perf_counter() - started, 4)
            row['passed'] = not row['errors']
            report['rows'].append(row)
            offline.write_json(args.output, report)
            print(json.dumps({'id': row['id'], 'passed': row['passed'], 'seconds': row['elapsed_seconds'],
                              'errors': row['errors']}, ensure_ascii=True), flush=True)
        report['completed'] = True
        report['finished_utc'] = datetime.now(timezone.utc).isoformat()
        report['passed'] = sum(row['passed'] for row in report['rows'])
        report['failed'] = len(report['rows']) - report['passed']
        report['provider_call_count'] = sum(row['sent_to_provider'] for row in report['rows'])
        report['error_counts'] = dict(Counter(error for row in report['rows'] for error in row['errors']))
        offline.write_json(args.output, report)
        print(json.dumps({key: report[key] for key in ('passed', 'failed', 'completed', 'issue_complete')}))
        return 0 if not report['failed'] and all(check['passed'] for check in report['acl_local_checks']) else 1
    except (ValidationError, OSError) as error:
        code = str(error) if isinstance(error, ValidationError) else 'artifact_io_error'
        print(json.dumps({'error': code}), flush=True)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
