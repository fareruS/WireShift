using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NetBinder.Shared.Protocol;

/// <summary>
/// Framing for the named pipe protocol shared by the UI and the background service.
///
/// Wire format: 4-byte little-endian payload length, followed by that many UTF-8 bytes
/// of JSON. Both sides must read a full frame: a single <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/>
/// call may return fewer bytes than requested, and treating a short read as a failed
/// message would desynchronize the stream.
/// </summary>
public static class PipeFraming
{
    /// <summary>Size of the little-endian length prefix.</summary>
    public const int LengthPrefixBytes = sizeof(int);

    /// <summary>Upper bound for a single message payload.</summary>
    public const int MaxPayloadBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Writes one length-prefixed message and flushes it.
    /// </summary>
    public static async Task WriteMessageAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (payload.Length <= 0 || payload.Length > MaxPayloadBytes)
            throw new IOException($"Invalid pipe message length: {payload.Length}");

        var prefix = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one length-prefixed message.
    /// Returns <c>null</c> when the peer closed the stream cleanly at a frame boundary.
    /// Throws <see cref="IOException"/> if the frame is malformed or truncated.
    /// </summary>
    public static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var prefix = new byte[LengthPrefixBytes];
        if (!await ReadExactlyAsync(stream, prefix, ct).ConfigureAwait(false))
            return null;

        int payloadLength = BitConverter.ToInt32(prefix, 0);
        if (payloadLength <= 0 || payloadLength > MaxPayloadBytes)
            throw new IOException($"Invalid pipe message length: {payloadLength}");

        var payload = new byte[payloadLength];
        if (!await ReadExactlyAsync(stream, payload, ct).ConfigureAwait(false))
            throw new IOException("Pipe closed in the middle of a message");

        return payload;
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> completely, or returns <c>false</c> if the stream
    /// ended first. Streams are allowed to satisfy partial reads, so every caller that
    /// expects a fixed-size record must loop until the buffer is full.
    /// </summary>
    public static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
            if (read == 0)
                return false;

            totalRead += read;
        }

        return true;
    }
}
