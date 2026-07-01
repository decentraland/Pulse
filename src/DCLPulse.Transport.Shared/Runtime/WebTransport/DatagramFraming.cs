using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Pulse.Transport.WebTransport
{
    /// <summary>
    ///     Header framing for unreliable WebTransport datagrams: a 1-byte channel id followed by a
    ///     4-byte big-endian sequence number, then the payload. The invariant both ends rely on: every
    ///     datagram framed this way carries the full header on every channel, so a receiver always parses
    ///     it. On the sequenced channel the sequence drives stale-drop (see <see cref="DatagramDeduper" />);
    ///     on the unsequenced channel the sequence is present but ignored.
    /// </summary>
    public static class DatagramFraming
    {
        public const int HEADER_SIZE = 5;

        public static byte[] Frame(byte channelId, uint seq, ReadOnlySpan<byte> payload)
        {
            var framed = new byte[HEADER_SIZE + payload.Length];
            framed[0] = channelId;
            BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(1), seq);
            payload.CopyTo(framed.AsSpan(HEADER_SIZE));
            return framed;
        }

        /// <summary>
        ///     Parse the header. <paramref name="payload" /> aliases <paramref name="datagram" /> and is
        ///     valid only as long as it is. Returns false if the datagram is too short to hold a header.
        /// </summary>
        public static bool TryParse(
            ReadOnlySpan<byte> datagram,
            out byte channelId,
            out uint seq,
            out ReadOnlySpan<byte> payload)
        {
            if (datagram.Length < HEADER_SIZE)
            {
                channelId = 0;
                seq = 0;
                payload = default;
                return false;
            }

            channelId = datagram[0];
            seq = BinaryPrimitives.ReadUInt32BigEndian(datagram.Slice(1));
            payload = datagram.Slice(HEADER_SIZE);
            return true;
        }
    }

    /// <summary>Assigns monotonically increasing per-channel sequence numbers for outbound datagrams.</summary>
    public sealed class DatagramSequencer
    {
        private readonly Dictionary<byte, uint> next = new();

        public uint Next(byte channelId)
        {
            next.TryGetValue(channelId, out uint seq);
            next[channelId] = seq + 1;
            return seq;
        }
    }

    /// <summary>
    ///     Per-channel staleness filter for inbound sequenced datagrams. Accepts a datagram only if its
    ///     sequence is newer than the latest accepted on that channel, using serial-number arithmetic
    ///     (RFC 1982) so it survives u32 wraparound. Drops duplicates and out-of-order stragglers.
    ///     Only the sequenced channel runs through this; unsequenced datagrams are delivered as-is.
    /// </summary>
    public sealed class DatagramDeduper
    {
        private readonly Dictionary<byte, uint> lastSeen = new();

        public bool ShouldAccept(byte channelId, uint seq)
        {
            if (!lastSeen.TryGetValue(channelId, out uint last))
            {
                lastSeen[channelId] = seq;
                return true;
            }

            // (int)(seq - last) > 0  ⇒  seq is ahead of last within the positive half of the u32 range,
            // which stays correct across wraparound (e.g. last = uint.MaxValue, seq = 0 ⇒ diff = 1).
            if ((int)(seq - last) > 0)
            {
                lastSeen[channelId] = seq;
                return true;
            }

            return false;
        }
    }
}
