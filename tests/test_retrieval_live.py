"""Transport/contract regressions. No model or GPU required."""
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import urllib.error

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts'))
import retrieval_live as live
from retrieval_eval import ValidationError, load_json, prepare_case


class Response(io.BytesIO):
    status = 200


class LiveContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('live_cli_under_test', ROOT / 'scripts/test-retrieval-live.py')
        cls.cli = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.cli)
        spec = importlib.util.spec_from_file_location('context_cli_under_test', ROOT / 'scripts/test-retrieval-context.py')
        cls.context = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.context)

    def envelope(self, **changes):
        return {'model': 'qwen3:4b', 'done': True, 'done_reason': 'stop',
                'response': '{"status":"answered","answer":"2","sources":[]}', **changes}

    def test_loopback_variants(self):
        for url in ('http://127.0.0.1:11434', 'http://localhost:11434', 'http://[::1]:11434'):
            self.assertEqual(live.loopback_url(url), url)

    def test_reject_non_loopback_and_ambiguous_urls(self):
        for url in ('http://192.168.1.1:11434', 'https://ollama.internal:11434',
                    'http://localhost:11434@evil.test', 'http://user@localhost:11434',
                    'http://localhost:11434/path', 'http://localhost:11434?x=1',
                    'http://localhost:11434#x', 'http://localhost', 'http://localhost:99999',
                    'http://127.0.0.1.evil.test:11434'):
            with self.subTest(url=url), self.assertRaises(ValidationError):
                live.loopback_url(url)

    def test_success_inner_json(self):
        self.assertEqual(live.parse_generation(self.envelope())['answer'], '2')

    def test_done_must_be_boolean_true(self):
        for done in ('true', 1, False, None):
            with self.subTest(done=done), self.assertRaisesRegex(ValidationError, 'incomplete'):
                live.parse_generation(self.envelope(done=done))

    def test_truncation_and_wrong_model(self):
        for value, code in ((self.envelope(done_reason='length'), 'truncated'),
                            (self.envelope(model='qwen3:8b'), 'model_mismatch'),
                            (self.envelope(done_reason=None), 'invalid_done_reason')):
            with self.assertRaisesRegex(ValidationError, code):
                live.parse_generation(value)

    def test_duplicate_inner_keys_rejected(self):
        with self.assertRaises(ValidationError):
            live.parse_generation(self.envelope(response='{"answer":"2","answer":"999"}'))

    def test_redirect_never_followed(self):
        with self.assertRaisesRegex(ValidationError, 'redirect_rejected'):
            live.NoRedirect().redirect_request(None, None, 302, 'redirect', {}, 'https://evil.test')

    def test_request_bypasses_proxy_and_bounds_read(self):
        with patch.object(live.urllib.request, 'build_opener') as build:
            build.return_value.open.return_value = Response(b'{"version":"test"}')
            self.assertEqual(live.request_json('http://localhost:11434', '/api/version'), {'version': 'test'})
            self.assertEqual(build.call_args.args[0].proxies, {})
            self.assertEqual(build.return_value.open.call_args.kwargs['timeout'], 90)

    def test_response_size_limit(self):
        with patch.object(live.urllib.request, 'build_opener') as build:
            build.return_value.open.return_value = Response(b' ' * (live.MAX_RESPONSE_BYTES + 1))
            with self.assertRaisesRegex(ValidationError, 'too_large'):
                live.request_json('http://localhost:11434', '/api/version')

    def test_timeout_unavailable_http_classifications(self):
        failures = [(TimeoutError(), 'provider_timeout'),
                    (urllib.error.URLError(TimeoutError()), 'provider_timeout'),
                    (urllib.error.URLError(ConnectionRefusedError()), 'provider_unavailable'),
                    (urllib.error.HTTPError('x', 500, 'secret detail', {}, None), 'provider_http_error')]
        for error, code in failures:
            with patch.object(live.urllib.request, 'build_opener') as build:
                build.return_value.open.side_effect = error
                with self.assertRaisesRegex(ValidationError, '^' + code + '$'):
                    live.request_json('http://localhost:11434', '/api/version')

    def test_invalid_timeout_and_path_before_network(self):
        for timeout in (0, -1, 181, True, float('nan')):
            with self.assertRaisesRegex(ValidationError, 'invalid_timeout'):
                live.request_json('http://localhost:11434', '/api/version', timeout=timeout)
        with self.assertRaisesRegex(ValidationError, 'invalid_provider_path'):
            live.request_json('http://localhost:11434', '/admin')

    def test_all_quality_requests_fit_provisional_preflight(self):
        corpus = load_json(ROOT / 'tests/retrieval/documents.json')
        dataset = load_json(ROOT / 'tests/retrieval/questions.json')
        for case in dataset['cases']:
            prepared = prepare_case(case, corpus, max_context_bytes=1400)
            self.assertTrue(set(case['expected']['sources']) <=
                            {doc['id'] for doc in prepared['selection']['documents']})
            proposal = prepared['request_proposal']
            original = dict(proposal['options'])
            request, size = live.prepare_live_request(proposal)
            self.assertLessEqual(size + 1280, 4096)
            self.assertEqual(request['options']['num_ctx'], 4096)
            self.assertEqual(proposal['options'], original)

    def test_oversized_input_rejected(self):
        proposal = {'system': 'x' * 5000, 'prompt': 'x', 'format': {}, 'options': {}}
        with self.assertRaisesRegex(ValidationError, 'input_budget_exceeded'):
            live.prepare_live_request(proposal)

    def test_constrained_profile_uses_presented_ids_not_ground_truth(self):
        corpus = load_json(ROOT / 'tests/retrieval/documents.json')
        dataset = load_json(ROOT / 'tests/retrieval/questions.json')
        case = dataset['cases'][1]  # Missing information: expected citations are empty.
        proposal = prepare_case(case, corpus, max_context_bytes=1400)['request_proposal']
        request, size = live.prepare_live_request(proposal, constrained_sources=True)
        self.assertEqual(request['format']['properties']['sources']['items']['enum'], ['SYN-S01'])
        self.assertNotIn('enum', proposal['format']['properties']['sources']['items'])
        self.assertNotIn('enum', request['format']['properties']['answer'])
        self.assertEqual(list(request['format']['properties']), ['answer', 'sources', 'status'])
        self.assertLessEqual(size + 1280, 4096)

    def test_empty_retrieval_does_not_allow_fabricated_citations(self):
        proposal = {'system': 'safe', 'prompt': '{"documents":[]}', 'options': {},
                    'format': {'properties': {'answer': {'type': 'string'},
                                             'sources': {'type': 'array', 'items': {'type': 'string'}},
                                             'status': {'type': 'string'}}}}
        request, _ = live.prepare_live_request(proposal, constrained_sources=True)
        self.assertEqual(request['format']['properties']['sources']['maxItems'], 0)

    def test_explicit_live_opt_in_required_without_network(self):
        with patch.object(self.cli, 'request_json') as network:
            self.assertEqual(self.cli.main(['--output', 'unused.json']), 2)
            network.assert_not_called()

    def test_live_cli_propagates_grading_and_transport_failures(self):
        for inner, expected_code in (({'answer': '11 ชิ้น', 'status': 'answered', 'sources': ['SYN-S01']}, 0),
                                     ({'answer': '999 ชิ้น', 'status': 'answered', 'sources': ['SYN-S01']}, 1),
                                     (ValidationError('provider_timeout'), 1)):
            with tempfile.TemporaryDirectory(dir=ROOT) as directory:
                output = Path(directory) / 'new.json'
                replies = [{'version': 'fixture'}, {'models': [{'name': 'qwen3:4b', 'digest': 'fixture'}]},
                           inner if isinstance(inner, Exception) else self.envelope(response=json.dumps(inner))]
                with patch.object(self.cli, 'request_json', side_effect=replies):
                    code = self.cli.main(['--allow-live', '--case-id', 'single-01', '--output', str(output)])
                self.assertEqual(code, expected_code)
                report = json.loads(output.read_text(encoding='utf-8'))
                self.assertTrue(report['completed'])
                self.assertFalse(report['issue_complete'])
                self.assertEqual(report['provider_call_count'], 1)
                self.assertEqual(report['selected_count'], 1)
                self.assertEqual(report['full_suite_count'], 60)

    def test_existing_evidence_is_never_overwritten(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as directory:
            output = Path(directory) / 'existing.json'
            output.write_text('keep me', encoding='utf-8')
            with patch.object(self.cli, 'request_json') as network:
                self.assertEqual(self.cli.main(['--allow-live', '--output', str(output)]), 2)
                network.assert_not_called()
            self.assertEqual(output.read_text(encoding='utf-8'), 'keep me')

    def test_context_probe_requires_opt_in(self):
        with patch.object(self.context, 'request_json') as network:
            self.assertEqual(self.context.main(['--output', 'unused.json']), 2)
            network.assert_not_called()

    def test_context_probe_keeps_markers_and_rejects_unbounded_input(self):
        request = self.context.build_probe(4500)
        self.assertIn('ALPHA_47', request['prompt'])
        self.assertIn('OMEGA_83', request['prompt'])
        self.assertEqual(request['prompt'].count(' pad'), 4500)
        for invalid in (-1, 6001, True, '500'):
            with self.assertRaises(ValidationError):
                self.context.build_probe(invalid)

    def test_malformed_provider_metadata_is_safe_and_sends_no_generation(self):
        for version, tags in (([], {}), ({'version': ''}, {'models': []}),
                              ({'version': 'test'}, []), ({'version': 'test'}, {'models': [None]})):
            with tempfile.TemporaryDirectory(dir=ROOT) as directory:
                output = Path(directory) / 'new.json'
                with patch.object(self.cli, 'request_json', side_effect=[version, tags]) as network:
                    self.assertEqual(self.cli.main(['--allow-live', '--output', str(output)]), 2)
                    self.assertEqual(network.call_count, 2)
                    self.assertFalse(output.exists())

    def test_wrong_installed_model_is_rejected_before_generation(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as directory:
            output = Path(directory) / 'new.json'
            with patch.object(self.cli, 'request_json', side_effect=[{'version': 'test'},
                              {'models': [{'name': 'qwen3:8b'}]}]) as network:
                self.assertEqual(self.cli.main(['--allow-live', '--output', str(output)]), 2)
                self.assertEqual(network.call_count, 2)
                self.assertFalse(output.exists())

    def test_unit_pattern_uses_query_unit_without_fixing_numeric_value(self):
        import re
        from retrieval_eval import grade_answer
        corpus = load_json(ROOT / 'tests/retrieval/documents.json')
        case = next(c for c in load_json(ROOT / 'tests/retrieval/questions.json')['cases'] if c['id'] == 'single-09')
        request, _ = live.prepare_live_request(prepare_case(case, corpus)['request_proposal'], constrained_units=True)
        pattern = request['format']['properties']['answer']['pattern']
        self.assertIsNotNone(re.fullmatch(pattern, '999 minutes'))
        self.assertIsNone(re.fullmatch(pattern, '19'))
        self.assertIsNone(re.fullmatch(pattern, '19 seconds'))
        self.assertIn('answer_not_accepted', grade_answer(case,
                      {'answer': '999 minutes', 'status': 'answered', 'sources': ['SYN-S09']}, ['SYN-S09']))

    def test_unit_profile_fits_every_closed_fixture_without_dropping_sources(self):
        corpus = load_json(ROOT / 'tests/retrieval/documents.json')
        cases = load_json(ROOT / 'tests/retrieval/questions.json')['cases']
        for case in cases:
            prepared = prepare_case(case, corpus, max_context_bytes=1400)
            request, size = live.prepare_live_request(prepared['request_proposal'], constrained_units=True)
            self.assertLessEqual(size + 1280, 4096)
            self.assertTrue(set(case['expected']['sources']) <= {d['id'] for d in prepared['selection']['documents']})
            self.assertEqual('pattern' in request['format']['properties']['answer'], case['category'] in ('single','multi'))

    def test_unit_profile_does_not_mutate_unrestricted_baseline(self):
        import copy
        corpus = load_json(ROOT / 'tests/retrieval/documents.json')
        case = load_json(ROOT / 'tests/retrieval/questions.json')['cases'][0]
        proposal = prepare_case(case, corpus)['request_proposal']
        before = copy.deepcopy(proposal)
        request, size = live.prepare_live_request(proposal, constrained_units=True)
        self.assertEqual(proposal, before)
        self.assertEqual(size, len(request['system'].encode('utf-8')) + len(request['prompt'].encode('utf-8'))
                         + len(json.dumps(request['format'], ensure_ascii=False,separators=(',',':')).encode('utf-8')))

    def test_unknown_unit_question_is_not_guessed_from_facts(self):
        corpus = load_json(ROOT / 'tests/retrieval/documents.json')
        case = load_json(ROOT / 'tests/retrieval/questions.json')['cases'][0]
        proposal = prepare_case(case, corpus)['request_proposal']
        payload = json.loads(proposal['prompt'])
        payload['query'] = 'What does this document say?'
        proposal['prompt'] = json.dumps(payload,ensure_ascii=False)
        request, _ = live.prepare_live_request(proposal, constrained_units=True)
        self.assertNotIn('pattern',request['format']['properties']['answer'])


if __name__ == '__main__':
    unittest.main()
