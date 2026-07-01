using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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

        /// <summary>Prefix <paramref name="payload" /> with its 4-byte big-endian length.</summary>
        public static byte[] Frame(ReadOnlySpan<byte> payload)
        {
            var framed = new byte[HEADER_SIZE + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)payload.Length);
            payload.CopyTo(framed.AsSpan(HEADER_SIZE));
            return framed;
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
        private readonly List<byte> buffer = new();

        public StreamFrameReader(int maxMessageLength)
        {
            if (maxMessageLength <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxMessageLength));

            this.maxMessageLength = maxMessageLength;
        }

        /// <summary>Append a raw inbound stream chunk.</summary>
        public void Append(ReadOnlySpan<byte> chunk)
        {
            foreach (byte b in chunk)
                buffer.Add(b);
        }

        /// <summary>
        ///     Dequeue the next complete message (length prefix stripped), or return false if a full
        ///     frame is not buffered yet.
        /// </summary>
        /// <exception cref="InvalidDataException">A frame declares a length exceeding the cap.</exception>
        public bool TryRead(out byte[] message)
        {
            message = Array.Empty<byte>();
            if (buffer.Count < HEADER_SIZE)
                return false;

            Span<byte> header = stackalloc byte[HEADER_SIZE];
            for (var i = 0; i < HEADER_SIZE; i++)
                header[i] = buffer[i];
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);

            if (length > (uint)maxMessageLength)
            {
                // The stream is unrecoverable past a frame that overruns the cap — its next boundary is
                // lost — so drop what's buffered before signalling. Otherwise the offending header sits at
                // the head and every later chunk re-appends and re-throws, growing the buffer without
                // bound; the caller debits its corruption budget on the throw and disconnects the peer.
                buffer.Clear();
                throw new InvalidDataException($"stream frame length {length} exceeds cap {maxMessageLength}");
            }

            int total = HEADER_SIZE + (int)length;
            if (buffer.Count < total)
                return false;

            message = new byte[length];
            for (var i = 0; i < (int)length; i++)
                message[i] = buffer[HEADER_SIZE + i];
            buffer.RemoveRange(0, total);
            return true;
        }
    }
}
