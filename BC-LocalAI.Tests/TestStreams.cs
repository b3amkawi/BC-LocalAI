using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace BC_LocalAI.Tests;

/// <summary>HTTP content that remembers whether it was disposed (proves the response was released).</summary>
internal sealed class TrackedContent : StreamContent
{
    public TrackedContent(Stream stream, string mediaType = "application/json")
        : base(stream)
    {
        Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
    }

    public bool Disposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>A readable stream that cannot seek, so the content length is unknown (chunked).</summary>
internal class UnknownLengthStream(byte[] data) : Stream
{
    private int _position;

    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = Math.Min(count, data.Length - _position);
        Array.Copy(data, _position, buffer, offset, n);
        _position += n;
        BytesRead += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = Math.Min(buffer.Length, data.Length - _position);
        data.AsMemory(_position, n).CopyTo(buffer);
        _position += n;
        BytesRead += n;
        return ValueTask.FromResult(n);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A seekable stream (so the content length is known) that counts how many bytes were read from it.</summary>
internal sealed class CountingMemoryStream(byte[] data) : MemoryStream(data)
{
    public long BytesRead { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = base.Read(buffer, offset, count);
        BytesRead += n;
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = base.Read(buffer);
        BytesRead += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = base.Read(buffer.Span);
        BytesRead += n;
        return ValueTask.FromResult(n);
    }
}

/// <summary>A body that never ends (spaces), like a misbehaving provider. Counts what was pulled from it.</summary>
internal sealed class EndlessStream : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Array.Fill(buffer, (byte)' ', offset, count);
        BytesRead += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        buffer.Span.Fill((byte)' ');
        BytesRead += buffer.Length;
        return ValueTask.FromResult(buffer.Length);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Sends the first bytes and then waits until the token is cancelled (a stalled provider).</summary>
internal sealed class StallingStream(byte[] head) : Stream
{
    private int _position;

    public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource TokenCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < head.Length)
        {
            var n = Math.Min(buffer.Length, head.Length - _position);
            head.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        cancellationToken.Register(() => TokenCancelled.TrySetResult());
        Stalled.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Delivers the data, and cancels the caller exactly at the end of the body (the last read, which returns 0 bytes) without
/// failing that read: the body is complete, the caller has just gone away.
/// </summary>
internal sealed class CancelAtEndStream(byte[] data, CancellationTokenSource caller) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= data.Length)
        {
            caller.Cancel();
            return ValueTask.FromResult(0);
        }

        var n = Math.Min(buffer.Length, data.Length - _position);
        data.AsMemory(_position, n).CopyTo(buffer);
        _position += n;
        return ValueTask.FromResult(n);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Delivers one byte every <paramref name="delay"/>: no single read is slow, but the whole body takes too long.</summary>
internal sealed class DripStream(byte[] data, TimeSpan delay) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= data.Length)
        {
            return 0;
        }

        await Task.Delay(delay, cancellationToken);
        buffer.Span[0] = data[_position++];
        return 1;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Returns predetermined byte counts so UTF-8 code points and NDJSON lines can cross network reads.</summary>
internal sealed class ChunkedReadStream(byte[] data, params int[] chunkSizes) : Stream
{
    private int _position;
    private int _chunk;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_position >= data.Length) return ValueTask.FromResult(0);
        var requested = chunkSizes.Length == 0 ? 1 : chunkSizes[_chunk++ % chunkSizes.Length];
        var count = Math.Min(Math.Min(requested, buffer.Length), data.Length - _position);
        data.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return ValueTask.FromResult(count);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class OllamaBodies
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The payload Ollama puts in "response": {"answer": text}.</summary>
    public static string Answer(string text, bool relaxedUnicode = false) =>
        JsonSerializer.Serialize(new { answer = text }, relaxedUnicode ? Relaxed : null);

    /// <summary>A full Ollama /api/generate body as UTF-8 bytes. Thai stays readable when relaxedUnicode is true.</summary>
    public static byte[] Bytes(
        string answerPayload,
        string model = "qwen3:4b",
        string done = "true",
        string? doneReason = "stop",
        int? evalCount = 100,
        bool relaxedUnicode = false)
    {
        var options = relaxedUnicode ? Relaxed : null;
        var reason = doneReason is null ? "null" : JsonSerializer.Serialize(doneReason, options);
        var eval = evalCount is null ? "null" : evalCount.Value.ToString();
        var json = $$"""{"model":{{JsonSerializer.Serialize(model, options)}},"response":{{JsonSerializer.Serialize(answerPayload, options)}},"done":{{done}},"done_reason":{{reason}},"eval_count":{{eval}}}""";
        return Encoding.UTF8.GetBytes(json);
    }

    public static (HttpResponseMessage Response, TrackedContent Content) Response(
        byte[] body,
        HttpStatusCode status = HttpStatusCode.OK,
        bool seekable = true)
    {
        Stream stream = seekable ? new MemoryStream(body) : new UnknownLengthStream(body);
        var content = new TrackedContent(stream);
        return (new HttpResponseMessage(status) { Content = content }, content);
    }
}
