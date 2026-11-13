using System.Net;
using System.Net.Http.Headers;

namespace SdkCollector.Services;

internal sealed class RangeNotSupportedException : Exception
{
    public RangeNotSupportedException(string message) : base(message) { }
    public RangeNotSupportedException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed class BufferedHttpRangeStream : Stream
{
    private const int DefaultChunkSize = 2 * 1024 * 1024;
    private const int MaxCachedBlocks = 10;

    private readonly HttpClient _httpClient;
    private readonly string _url;
    private readonly long _length;
    private readonly List<CachedBlock> _blocks = [];
    private long _position;

    internal int RangeRequestCount { get; private set; }

    private BufferedHttpRangeStream(HttpClient httpClient, string url, long length, List<CachedBlock> initialBlocks)
    {
        _httpClient = httpClient;
        _url = url;
        _length = length;
        _blocks.AddRange(initialBlocks);
    }

    public static async Task<BufferedHttpRangeStream> CreateAsync(HttpClient httpClient, string url, CancellationToken ct)
    {
        (long totalLength, byte[] probeBytes) = await ProbeLengthAsync(httpClient, url, ct).ConfigureAwait(false);
        var initialBlocks = new List<CachedBlock>();
        int requestCount = 1;

        if (probeBytes.Length > 0)
        {
            initialBlocks.Add(new CachedBlock(0, probeBytes));
        }

        if (totalLength > probeBytes.Length)
        {
            long tailStart = Math.Max(0, totalLength - DefaultChunkSize);
            long tailEnd = totalLength - 1;
            byte[] tailData = await FetchRangeAsync(httpClient, url, tailStart, tailEnd, ct).ConfigureAwait(false);
            initialBlocks.Add(new CachedBlock(tailStart, tailData));
            requestCount++;
        }

        return new BufferedHttpRangeStream(httpClient, url, totalLength, initialBlocks)
        {
            RangeRequestCount = requestCount
        };
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        ArgumentOutOfRangeException.ThrowIfNegative(target);
        _position = target;
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || _position >= _length)
        {
            return 0;
        }

        int totalRead = 0;
        while (totalRead < buffer.Length && _position < _length)
        {
            CachedBlock? block = FindBlockContaining(_position);
            if (block is null)
            {
                long remainingInFile = _length - _position;
                int requestSize = (int)Math.Min(remainingInFile, Math.Max(buffer.Length - totalRead, DefaultChunkSize));
                long end = _position + requestSize - 1;
                byte[] data = FetchRangeAsync(_httpClient, _url, _position, end, CancellationToken.None).GetAwaiter().GetResult();
                RangeRequestCount++;
                if (data.Length == 0)
                {
                    break;
                }

                block = new CachedBlock(_position, data);
                AddCachedBlock(block);
            }

            long offsetInBlock = _position - block.Offset;
            int available = (int)Math.Min(block.Data.Length - offsetInBlock, buffer.Length - totalRead);
            if (available <= 0)
            {
                break;
            }

            block.Data.AsSpan((int)offsetInBlock, available).CopyTo(buffer[totalRead..]);
            _position += available;
            totalRead += available;
        }

        return totalRead;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private CachedBlock? FindBlockContaining(long position)
    {
        for (int i = _blocks.Count - 1; i >= 0; i--)
        {
            CachedBlock b = _blocks[i];
            if (position >= b.Offset && position < b.Offset + b.Data.Length)
            {
                return b;
            }
        }
        return null;
    }

    private void AddCachedBlock(CachedBlock block)
    {
        if (_blocks.Count >= MaxCachedBlocks)
        {
            _blocks.RemoveAt(0);
        }
        _blocks.Add(block);
    }

    private static async Task<(long TotalLength, byte[] ProbeBytes)> ProbeLengthAsync(
        HttpClient httpClient,
        string url,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(0, 0);
                using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    ContentRangeHeaderValue? cr = response.Content.Headers.ContentRange;
                    if (cr?.Length is not long totalLen || totalLen <= 0)
                    {
                        throw new RangeNotSupportedException($"Missing Content-Range length from '{url}'.");
                    }

                    byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    return (totalLen, bytes);
                }

                if (response.StatusCode == HttpStatusCode.OK ||
                    response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    throw new RangeNotSupportedException($"Server returned {(int)response.StatusCode} instead of 206 Partial Content for '{url}'.");
                }

                if (!IsTransientStatus(response.StatusCode) || attempt == 3)
                {
                    response.EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex) when (ex is not RangeNotSupportedException && ex is not OperationCanceledException && attempt < 3)
            {
                // Transient network failure; retry below.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(350 * Math.Pow(2, attempt)), ct).ConfigureAwait(false);
        }

        throw new RangeNotSupportedException($"Unable to establish HTTP Range stream for '{url}'.");
    }

    private static async Task<byte[]> FetchRangeAsync(
        HttpClient httpClient,
        string url,
        long start,
        long end,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(start, end);
                using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    throw new RangeNotSupportedException($"Server ignored Range header and returned 200 OK for '{url}'.");
                }

                if (!IsTransientStatus(response.StatusCode) || attempt == 3)
                {
                    response.EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex) when (ex is not RangeNotSupportedException && ex is not OperationCanceledException && attempt < 3)
            {
                // Retry transient failures.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(350 * Math.Pow(2, attempt)), ct).ConfigureAwait(false);
        }

        throw new HttpRequestException($"Failed to fetch byte range {start}-{end} from '{url}'.");
    }

    private static bool IsTransientStatus(HttpStatusCode code) =>
        code == HttpStatusCode.TooManyRequests ||
        code == HttpStatusCode.RequestTimeout ||
        (int)code >= 500;

    private sealed record CachedBlock(long Offset, byte[] Data);
}
