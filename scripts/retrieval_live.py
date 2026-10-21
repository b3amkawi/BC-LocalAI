"""Bounded loopback-only transport for exploratory synthetic AI-010 evaluation."""
import ipaddress
import copy
import json
import re
import socket
import urllib.error
import urllib.parse
import urllib.request

from retrieval_eval import ValidationError, strict_json

MAX_RESPONSE_BYTES = 1_048_576


def loopback_url(value):
    try:
        url = urllib.parse.urlsplit(value)
        host = url.hostname
        allowed = host == 'localhost' or ipaddress.ip_address(host).is_loopback
        port = url.port
    except (ValueError, TypeError):
        raise ValidationError('invalid_loopback_url') from None
    if (url.scheme != 'http' or not allowed or url.username is not None
            or url.password is not None or url.path not in ('', '/')
            or url.query or url.fragment or port is None):
        raise ValidationError('invalid_loopback_url')
    return value.rstrip('/')


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValidationError('provider_redirect_rejected')


def request_json(base_url, path, payload=None, timeout=90):
    base_url = loopback_url(base_url)
    if path not in ('/api/version', '/api/tags', '/api/generate'):
        raise ValidationError('invalid_provider_path')
    if not isinstance(timeout, (int, float)) or isinstance(timeout, bool) or not 0 < timeout <= 180:
        raise ValidationError('invalid_timeout')
    data = None if payload is None else json.dumps(payload, ensure_ascii=False, allow_nan=False).encode('utf-8')
    request = urllib.request.Request(base_url + path, data=data,
                                     headers={'Content-Type': 'application/json; charset=utf-8'})
    # Ignore environment proxy settings; reject redirects even to another loopback port.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        with opener.open(request, timeout=timeout) as response:
            if response.status != 200:
                raise ValidationError('provider_http_error')
            raw = response.read(MAX_RESPONSE_BYTES + 1)
            if len(raw) > MAX_RESPONSE_BYTES:
                raise ValidationError('provider_response_too_large')
            try:
                return strict_json(raw.decode('utf-8'))
            except UnicodeDecodeError:
                raise ValidationError('provider_invalid_utf8') from None
    except urllib.error.HTTPError:
        raise ValidationError('provider_http_error') from None
    except (TimeoutError, socket.timeout):
        raise ValidationError('provider_timeout') from None
    except urllib.error.URLError as error:
        code = 'provider_timeout' if isinstance(error.reason, (TimeoutError, socket.timeout)) else 'provider_unavailable'
        raise ValidationError(code) from None


def parse_generation(envelope):
    if not isinstance(envelope, dict) or envelope.get('model') != 'qwen3:4b':
        raise ValidationError('provider_model_mismatch')
    if envelope.get('done') is not True:
        raise ValidationError('provider_incomplete')
    if envelope.get('done_reason') == 'length':
        raise ValidationError('provider_truncated')
    if envelope.get('done_reason') != 'stop':
        raise ValidationError('provider_invalid_done_reason')
    if not isinstance(envelope.get('response'), str):
        raise ValidationError('provider_invalid_response')
    return strict_json(envelope['response'])


def prepare_live_request(proposal, *, constrained_sources=False, constrained_units=False):
    # Per-request setting only. No global/model configuration is changed.
    request = dict(proposal)
    request['options'] = {**proposal['options'], 'num_ctx': 4096}
    if constrained_sources or constrained_units:
        # Constrain to retrieved IDs, never to the expected answer or expected sources.
        # A wrong subset, unnecessary citation, or missing citation still fails grading.
        request['format'] = copy.deepcopy(proposal['format'])
        schema = request['format']
        fields = schema['properties']
        schema['properties'] = {name: fields[name] for name in ('answer', 'sources', 'status')}
        schema['required'] = ['answer', 'sources', 'status']
        fields['answer']['minLength'] = 1
        ids = [doc['id'] for doc in strict_json(proposal['prompt'])['documents']]
        if ids:
            fields['sources']['items']['enum'] = ids
        else:
            fields['sources']['maxItems'] = 0
        request['system'] += (
            ' Preserve the exact requested unit in numeric answers. '
            'Check whether the requested attribute exists: a numeric value is not a revision code. '
            'Write the supported answer, then sources, then set status to match the answer: '
            'conflict for conflicting evidence, not_found for absent facts, otherwise answered.'
        )
    if constrained_units:
        # Closed-format prototype only. Read the requested unit from the query,
        # never the dataset's expected answer. Every numeric value remains possible.
        query = strict_json(proposal['prompt'])['query']
        match = re.search(r'ตอบเฉพาะตัวเลขตามด้วยหน่วย (\S+)$', query)
        if match is None:
            match = re.search(r'Reply only with the (?:total )?number and ([A-Za-z ]+)\.$', query)
        if match:
            unit = match.group(1)
            fields['answer']['pattern'] = r'^[0-9]+(?:\.[0-9]+)? ' + re.escape(unit) + '$'
    # A deliberately conservative fixture preflight, NOT an approved tokenizer policy.
    # Count system + prompt + schema UTF-8 bytes and reserve 1024 output + 256 overhead.
    if constrained_units:
        # Count actual prompt/system UTF-8, not a JSON-escaped string of a string.
        # The schema still counts in full. This remains provisional, not tokenization.
        input_bytes = (len(request['system'].encode('utf-8')) + len(request['prompt'].encode('utf-8'))
                       + len(json.dumps(request['format'], ensure_ascii=False, separators=(',', ':')).encode('utf-8')))
    else:
        input_bytes = sum(len(json.dumps(request[key], ensure_ascii=False).encode('utf-8'))
                          for key in ('system', 'prompt', 'format'))
    if input_bytes + 1024 + 256 > 4096:
        raise ValidationError('provisional_input_budget_exceeded')
    return request, input_bytes
