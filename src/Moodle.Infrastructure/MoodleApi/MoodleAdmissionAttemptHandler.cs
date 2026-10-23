using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;

namespace Moodle.Infrastructure.MoodleApi;

internal static class MoodleAdmissionHttpRequestOptions
{
    public static readonly HttpRequestOptionsKey<MoodleAdmissionContext> Context =
        new("Moodle.AdmissionContext");
}

/// <summary>
/// Acquires one coordinator lease per actual transport attempt. The response
/// content owns the lease until its body is read or disposed, so retry delay
/// and streaming do not hold a concurrency slot.
/// </summary>
internal sealed class MoodleAdmissionAttemptHandler : DelegatingHandler
{
    private readonly MoodleAdmissionMode _mode;
    private readonly IMoodleAdmissionCoordinator? _coordinator;
    private readonly MoodleAdmissionObserver? _observer;
    private readonly TimeProvider _timeProvider;

    public MoodleAdmissionAttemptHandler(
        IOptions<MoodleAdmissionOptions> options,
        TimeProvider timeProvider,
        IMoodleAdmissionCoordinator? coordinator = null,
        MoodleAdmissionObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _mode = options.Value.Mode;
        _coordinator = coordinator;
        _observer = observer;
        _timeProvider = timeProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(MoodleAdmissionHttpRequestOptions.Context, out var context))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (_mode == MoodleAdmissionMode.Legacy)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (_mode == MoodleAdmissionMode.Observe)
            return await SendObservedAsync(request, context, cancellationToken).ConfigureAwait(false);

        if (_coordinator is null)
            throw new InvalidOperationException("Moodle admission enforcement is enabled without a coordinator.");

        var lease = await _coordinator.AcquireAsync(context, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            lease.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (!lease.TryStartAttempt())
        {
            lease.Dispose();
            throw new MoodleAdmissionException(
                "moodle_attempt_budget_exhausted",
                "The Moodle operation has exhausted its HTTP attempt budget.");
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests &&
            TryGetRetryAfterHeader(response.Headers, _timeProvider.GetUtcNow(), out var retryAfter))
        {
            _coordinator.RecordRateLimit(context, retryAfter, MoodleCooldownScope.Connection);
        }

        response.Content = new MoodleAdmissionLeaseContent(response.Content, lease);
        return response;
    }

    private async Task<HttpResponseMessage> SendObservedAsync(
        HttpRequestMessage request,
        MoodleAdmissionContext context,
        CancellationToken cancellationToken)
    {
        var observation = _observer?.Begin(context) ?? EmptyDisposable.Instance;
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            observation.Dispose();
            throw;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests &&
            TryGetRetryAfterHeader(response.Headers, _timeProvider.GetUtcNow(), out var retryAfter))
        {
            _observer?.RecordRateLimit(context, retryAfter, MoodleCooldownScope.Connection);
        }

        response.Content = new MoodleAdmissionLeaseContent(response.Content, observation);
        return response;
    }

    private static bool TryGetRetryAfterHeader(
        HttpResponseHeaders headers,
        DateTimeOffset now,
        out TimeSpan retryAfter)
    {
        RetryConditionHeaderValue? parsed;
        try
        {
            parsed = headers.RetryAfter;
        }
        catch (FormatException)
        {
            // A malformed server header must not turn a received 429 into a
            // transport exception. The server can still retry via its policy.
            parsed = null;
        }

        return TryGetRetryAfter(parsed, now, out retryAfter);
    }

    private static bool TryGetRetryAfter(
        RetryConditionHeaderValue? retryAfterHeader,
        DateTimeOffset now,
        out TimeSpan retryAfter)
    {
        retryAfter = retryAfterHeader?.Delta ??
                     (retryAfterHeader?.Date is { } date ? date - now : TimeSpan.Zero);
        if (retryAfter <= TimeSpan.Zero)
        {
            retryAfter = TimeSpan.Zero;
            return false;
        }
        return true;
    }
}

internal sealed class EmptyDisposable : IDisposable
{
    public static EmptyDisposable Instance { get; } = new();
    public void Dispose() { }
}

internal sealed class MoodleAdmissionLeaseContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly IDisposable _lease;
    private int _released;

    public MoodleAdmissionLeaseContent(HttpContent inner, IDisposable lease)
    {
        _inner = inner;
        _lease = lease;
        foreach (var header in inner.Headers)
            Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        try { await _inner.CopyToAsync(stream, context).ConfigureAwait(false); }
        finally { ReleaseLease(); }
    }

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        System.Net.TransportContext? context,
        CancellationToken cancellationToken)
    {
        try { await _inner.CopyToAsync(stream, context, cancellationToken).ConfigureAwait(false); }
        finally { ReleaseLease(); }
    }

    protected override bool TryComputeLength(out long length)
    {
        if (_inner.Headers.ContentLength is { } contentLength)
        {
            length = contentLength;
            return true;
        }
        length = 0;
        return false;
    }

    protected override async Task<Stream> CreateContentReadStreamAsync() =>
        new MoodleAdmissionLeaseStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), ReleaseLease);

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        new MoodleAdmissionLeaseStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), ReleaseLease);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            ReleaseLease();
        }
        base.Dispose(disposing);
    }

    private void ReleaseLease()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _lease.Dispose();
    }
}

internal sealed class MoodleAdmissionLeaseStream(Stream inner, Action releaseLease) : Stream
{
    private int _released;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            var read = inner.Read(buffer, offset, count);
            if (read == 0) Release();
            return read;
        }
        catch { Release(); throw; }
    }

    public override int Read(Span<byte> buffer)
    {
        try
        {
            var read = inner.Read(buffer);
            if (read == 0) Release();
            return read;
        }
        catch { Release(); throw; }
    }

    public override int ReadByte()
    {
        try
        {
            var value = inner.ReadByte();
            if (value < 0) Release();
            return value;
        }
        catch { Release(); throw; }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        try
        {
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            if (read == 0) Release();
            return read;
        }
        catch { Release(); throw; }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) Release();
            return read;
        }
        catch { Release(); throw; }
    }

    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        try { await inner.CopyToAsync(destination, bufferSize, cancellationToken).ConfigureAwait(false); }
        finally { Release(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            Release();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        Release();
        GC.SuppressFinalize(this);
    }

    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) releaseLease();
    }
}
