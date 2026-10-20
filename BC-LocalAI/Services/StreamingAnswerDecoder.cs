using System.Globalization;
using System.Text;

namespace BC_LocalAI.Services;

/// <summary>
/// Incrementally extracts the single string value from {"answer":"..."}. It deliberately keeps the
/// JSON envelope and a short marker-sized tail out of downstream deltas.
/// </summary>
internal sealed class StreamingAnswerDecoder
{
    private const string ThinkOpen = "<think>";
    private const string ThinkClose = "</think>";
    private const int MarkerTailLength = 7;

    private readonly StringBuilder _key = new();
    private readonly StringBuilder _safeTail = new();
    private readonly StringBuilder _unicode = new();
    private DecoderState _state;
    private char? _pendingHighSurrogate;
    private bool _hasNonWhitespace;

    public IReadOnlyList<string> Push(string fragment)
    {
        var output = new List<string>();
        foreach (var character in fragment)
        {
            Process(character, output);
        }

        return output;
    }

    public IReadOnlyList<string> Complete()
    {
        if (_state != DecoderState.Done || _pendingHighSurrogate is not null || !_hasNonWhitespace)
        {
            throw Invalid();
        }

        var output = new List<string>();
        if (_safeTail.Length > 0)
        {
            output.Add(_safeTail.ToString());
            _safeTail.Clear();
        }

        return output;
    }

    private void Process(char character, List<string> output)
    {
        switch (_state)
        {
            case DecoderState.Leading:
                if (char.IsWhiteSpace(character)) return;
                Require(character == '{');
                _state = DecoderState.BeforeKey;
                return;
            case DecoderState.BeforeKey:
                if (char.IsWhiteSpace(character)) return;
                Require(character == '"');
                _state = DecoderState.Key;
                return;
            case DecoderState.Key:
                if (character == '"')
                {
                    Require(_key.ToString() == "answer");
                    _state = DecoderState.AfterKey;
                    return;
                }
                Require(character != '\\' && !char.IsControl(character));
                _key.Append(character);
                return;
            case DecoderState.AfterKey:
                if (char.IsWhiteSpace(character)) return;
                Require(character == ':');
                _state = DecoderState.BeforeValue;
                return;
            case DecoderState.BeforeValue:
                if (char.IsWhiteSpace(character)) return;
                Require(character == '"');
                _state = DecoderState.Value;
                return;
            case DecoderState.Value:
                if (character == '"')
                {
                    Require(_pendingHighSurrogate is null);
                    _state = DecoderState.AfterValue;
                    return;
                }
                if (character == '\\')
                {
                    _state = DecoderState.Escape;
                    return;
                }
                Require(!char.IsControl(character));
                AppendDecoded(character, output);
                return;
            case DecoderState.Escape:
                if (character == 'u')
                {
                    _unicode.Clear();
                    _state = DecoderState.Unicode;
                    return;
                }
                var decoded = character switch
                {
                    '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f',
                    'n' => '\n', 'r' => '\r', 't' => '\t', _ => throw Invalid()
                };
                AppendDecoded(decoded, output);
                _state = DecoderState.Value;
                return;
            case DecoderState.Unicode:
                Require(Uri.IsHexDigit(character));
                _unicode.Append(character);
                if (_unicode.Length == 4)
                {
                    var decodedUnicode = (char)int.Parse(_unicode.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    AppendDecoded(decodedUnicode, output);
                    _state = DecoderState.Value;
                }
                return;
            case DecoderState.AfterValue:
                if (char.IsWhiteSpace(character)) return;
                Require(character == '}');
                _state = DecoderState.Done;
                return;
            case DecoderState.Done:
                Require(char.IsWhiteSpace(character));
                return;
            default:
                throw Invalid();
        }
    }

    private void AppendDecoded(char character, List<string> output)
    {
        if (_pendingHighSurrogate is char high)
        {
            Require(char.IsLowSurrogate(character));
            AppendSafe(new string([high, character]), output);
            _pendingHighSurrogate = null;
            return;
        }

        if (char.IsHighSurrogate(character))
        {
            _pendingHighSurrogate = character;
            return;
        }

        Require(!char.IsLowSurrogate(character));
        AppendSafe(character.ToString(), output);
    }

    private void AppendSafe(string text, List<string> output)
    {
        _safeTail.Append(text);
        var tail = _safeTail.ToString();
        if (tail.Contains(ThinkOpen, StringComparison.OrdinalIgnoreCase)
            || tail.Contains(ThinkClose, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid();
        }

        while (_safeTail.Length > MarkerTailLength)
        {
            var emitLength = char.IsHighSurrogate(_safeTail[0]) ? 2 : 1;
            var emit = _safeTail.ToString(0, emitLength);
            _safeTail.Remove(0, emitLength);
            _hasNonWhitespace |= !string.IsNullOrWhiteSpace(emit);
            output.Add(emit);
        }

        _hasNonWhitespace |= !string.IsNullOrWhiteSpace(text);
    }

    private static void Require(bool condition)
    {
        if (!condition) throw Invalid();
    }

    private static AiServiceException Invalid() =>
        AiServiceException.InvalidResponse(InvalidResponseReasons.AnswerFieldInvalid);

    private enum DecoderState { Leading, BeforeKey, Key, AfterKey, BeforeValue, Value, Escape, Unicode, AfterValue, Done }
}
