using System;
using System.Buffers.Binary;
using System.IO;

namespace Pulse.Transport.WebTransport
{
    /// <summary>
    ///     Length-prefixed framing for the reliable WebTransport bidi stream. QUIC streams are byte
    ///     pipes with no message boundaries (unlike ENet packets), so each message is written as a
    ///     4-byte big-endian length followed by its payload. This is the wire contract for both ends
    ///     of the stream.
    /// </summary>
    public static class StreamFraming
    {
        public const int HEADER_SIZE = 4;

        /// <summary>Prefix <paramref name="payload" /> with its 4-byte big-endian length into a new array.</summary>
        public static byte[] Frame(ReadOnlySpan<byte> payload)
        {
            var framed = new byte[HEADER_SIZE + payload.Length];
            Frame(payload, framed);
            return framed;
        }

        /// <summary>
        ///     Write the 4-byte big-endian length prefix followed by <paramref name="payload" /> into
        ///     <paramref name="destination" /> (which must hold at least <c>HEADER_SIZE + payload.Length</c>
        ///     bytes) and return the number of bytes written — lets a caller frame into a reused or stack
        ///     buffer without allocating.
        /// </summary>
        public static int Frame(ReadOnlySpan<byte> payload, Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)payload.Length);
            payload.CopyTo(destination.Slice(HEADER_SIZE));
            return HEADER_SIZE + payload.Length;
        }
    }

    /// <summary>
    ///     Reassembles length-prefixed messages from raw stream chunks that may split or coalesce
    ///     frame boundaries arbitrarily. Not thread-safe; drive from a single reader (the WT loop).
    /// </summary>
    public sealed class StreamFrameReader
    {
        private const int HEADER_SIZE = StreamFraming.HEADER_SIZE;

        private readonly int maxMessageLength;

        // Contiguous buffer with a read/write cursor pair: unread bytes are buffer[start..end]. Reads
        // advance `start` (no per-frame shift); consumed front space is reclaimed lazily in Append, and
        // the buffer grows only when a single append plus an unread partial frame exceeds capacity.
        private byte[] buffer;
        private int start;
        private int end;

        public StreamFrameReader(int maxMessageLength)
        {
            if (maxMessageLength <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxMessageLength));

            this.maxMessageLength = maxMessageLength;
            buffer = new byte[HEADER_SIZE + maxMessageLength];
        }

        /// <summary>Append a raw inbound stream chunk.</summary>
        public void Append(ReadOnlySpan<byte> chunk)
        {
            EnsureWritable(chunk.Length);
            chunk.CopyTo(buffer.AsSpan(end));
            end += chunk.Length;
        }

        /// <summary>
        ///     Dequeue the next complete message (length prefix stripped), or return false if a full
        ///     frame is not buffered yet.
        /// </summary>
        /// <exception cref="InvalidDataException">A frame declares a length exceeding the cap.</exception>
        public bool TryRead(out byte[] message)
        {
            message = Array.Empty<byte>();
            if (end - start < HEADER_SIZE)
                return false;

            uint length = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(start));

            if (length > (uint)maxMessageLength)
            {
                // The stream is unrecoverable past a frame that overruns the cap — its next boundary is
                // lost — so drop what's buffered before signalling. Otherwise the offending header would
                // sit at the head and every later chunk re-read and re-throw; the caller debits its
                // corruption budget on the throw and disconnects the peer.
                start = end = 0;
                throw new InvalidDataException($"stream frame length {length} exceeds cap {maxMessageLength}");
            }

            int total = HEADER_SIZE + (int)length;
            if (end - start < total)
                return false;

            message = buffer.AsSpan(start + HEADER_SIZE, (int)length).ToArray();
            start += total;

            // Fully drained — rewind to the front so the next append starts at offset 0.
            if (start == end)
                start = end = 0;

            return true;
        }

        // Guarantee room for `count` more bytes: first reclaim consumed front space, then grow (double)
        // only if that still isn't enough.
        private void EnsureWritable(int count)
        {
            if (end + count <= buffer.Length)
                return;

            if (start > 0)
            {
                int live = end - start;
                if (live > 0)
                    Buffer.BlockCopy(buffer, start, buffer, 0, live);
                start = 0;
                end = live;
            }

            if (end + count <= buffer.Length)
                return;

            int size = buffer.Length * 2;
            while (size < end + count)
                size *= 2;
            Array.Resize(ref buffer, size);
        }
    }
}
