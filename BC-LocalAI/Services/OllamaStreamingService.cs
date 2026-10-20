using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BC_LocalAI.Configuration;
using BC_LocalAI.Models.Ollama;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Services;

public sealed class OllamaStreamingService(
    HttpClient httpClient,
    IOptions<OllamaOptions> options,
    ILogger<OllamaStreamingService> logger) : IStreamingAIService
{
    private const string GeneratePath = "api/generate";
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly OllamaOptions _options = options.Value;

    public async Task<AiStreamCompletion> StreamResponseAsync(
        string message,
        Func<string, CancellationToken, Task> onDelta,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var responseStarted = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, GeneratePath)
            {
                Content = JsonContent.Create(new OllamaGenerateRequest
                {
                    Model = _options.Model,
                    Prompt = message,
                    Stream = true,
                    Options = new OllamaGenerateOptions { NumPredict = _options.MaxOutputTokens }
                }, options: RequestJson)
            };
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                OllamaLog.ProviderError(logger, (int)response.StatusCode, ElapsedMs(started));
                throw AiServiceException.ProviderError();
            }

            responseStarted = true;

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            await using var boundedStream = new BoundedReadStream(stream, _options.MaxResponseBytes);
            using var reader = new StreamReader(boundedStream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var decoder = new StreamingAnswerDecoder();
            var completed = false;
            int? evalCount = null;

            while (true)
            {
                var line = await reader.ReadLineAsync(deadline.Token);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (completed) throw AiServiceException.InvalidResponse(InvalidResponseReasons.UnexpectedDataAfterDone);

                OllamaStreamRecord record;
                try
                {
                    record = JsonSerializer.Deserialize<OllamaStreamRecord>(line)
                        ?? throw new JsonException("Null streaming record");
                }
                catch (JsonException exception)
                {
                    throw AiServiceException.InvalidResponse(InvalidResponseReasons.BodyNotJson, exception);
                }

                if (record.Error is not null) throw AiServiceException.ProviderError();
                if (record.Model != _options.Model || record.Done is null || record.Response is null)
                {
                    throw AiServiceException.InvalidResponse(InvalidResponseReasons.InvalidStreamingRecord);
                }

                foreach (var delta in decoder.Push(record.Response))
                {
                    await onDelta(delta, deadline.Token);
                }

                if (record.Done is false) continue;
                completed = true;
                evalCount = record.EvalCount;
                if (record.DoneReason == "length")
                {
                    throw AiServiceException.ResponseTooLarge(ResponseTooLargeReasons.OutputTokenLimit);
                }
                if (record.DoneReason != "stop")
                {
                    throw AiServiceException.InvalidResponse(InvalidResponseReasons.UnexpectedDoneReason);
                }
            }

            if (!completed) throw AiServiceException.InvalidResponse(InvalidResponseReasons.NotDone);
            foreach (var delta in decoder.Complete())
            {
                await onDelta(delta, deadline.Token);
            }
            cancellationToken.ThrowIfCancellationRequested();
            OllamaLog.StreamAnswered(logger, _options.Model, ElapsedMs(started), evalCount, boundedStream.BytesRead);
            return new AiStreamCompletion("stop", evalCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            OllamaLog.CancelledByCaller(logger, ElapsedMs(started));
            throw;
        }
        catch (OperationCanceledException exception)
        {
            OllamaLog.TimedOut(logger, _options.TimeoutSeconds, ElapsedMs(started));
            throw AiServiceException.Timeout(exception);
        }
        catch (AiServiceException)
        {
            throw;
        }
        catch (DecoderFallbackException exception)
        {
            throw AiServiceException.InvalidResponse(InvalidResponseReasons.InvalidUtf8, exception);
        }
        catch (IOException exception) when (responseStarted)
        {
            throw AiServiceException.InvalidResponse(InvalidResponseReasons.InterruptedStream, exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            OllamaLog.Unavailable(logger, exception, OllamaOptions.DescribeEndpoint(_options.BaseUrl), ElapsedMs(started));
            throw AiServiceException.Unavailable(exception);
        }
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
/// <summary>Stops a provider body before an unbounded NDJSON line can consume unbounded memory.</summary>
internal sealed class BoundedReadStream(Stream inner, int maxBytes) : Stream
{
    public int BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var remainingWithProbe = maxBytes - BytesRead + 1;
        if (remainingWithProbe <= 0) throw AiServiceException.ResponseTooLarge(ResponseTooLargeReasons.BodySizeLimit);
        var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, remainingWithProbe)], cancellationToken);
        BytesRead += read;
        if (BytesRead > maxBytes) throw AiServiceException.ResponseTooLarge(ResponseTooLargeReasons.BodySizeLimit);
        return read;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
