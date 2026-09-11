using BenchmarkDotNet.Attributes;
using Decentraland.Kernel.Comms.V3;
using Decentraland.Pulse;
using Google.Protobuf;
using Pulse.Clusters;
using System.Buffers;

namespace DCLPulseBenchmarks;

/// <summary>
///     Publish-side encode cost for the two <see cref="NatsPublisher" /> feed messages, across the
///     three shapes the publisher has had. All three end with the payload sitting in the buffer the
///     NATS client supplies, so the only difference is how it gets there.
///     <para />
///     <b>The three shapes.</b> <i>ToByteArray</i> allocated a fresh <c>byte[]</c> per publish and let
///     the client's raw serializer copy it into the client's writer — two walks of the message
///     (<c>CalculateSize</c> then encode) plus a heap allocation plus a memcpy. <i>Pooled bytes</i>
///     replaced the allocation with an <see cref="ArrayPool{T}" /> rent, keeping both walks and the
///     memcpy. <i>Straight into the writer</i> hands the client's own writer to protobuf, so the rent,
///     the separate size pass and the memcpy all disappear.
///     <para />
///     <b>Measured</b> (Ryzen 9 9955HX, .NET 10.0.10, 2026-07), cluster change at a 13 B payload and
///     topology at 9 islands / 1000 wallets / 44 242 B, in the order above:
///     <list type="bullet">
///         <item>change: 48.96 ns / 104 B → 33.76 ns / 0 B → 26.19 ns / 0 B</item>
///         <item>topology: 26.78 us / 44 336 B → 26.04 us / 0 B → 18.06 us / 0 B</item>
///     </list>
///     Pooling the bytes bought the allocation and little else: -31% on the 13 B change, where the
///     allocation was most of the cost, but only -3% on the topology, inside that run's error bars.
///     Going zero-copy is where the clock moves — -33% on the topology against either predecessor —
///     and it is invisible in the allocation column, since the pooled shape was already at 0 B. That is
///     exactly why nothing but a benchmark can tell those two apart.
///     <para />
///     Expect a few percent of run-to-run drift and bimodality warnings on the zero-copy rows; the gaps
///     above are wide enough to survive both, but do not read a 3% delta as a regression.
///     <para />
///     <b>What is not measured.</b> <see cref="ArrayBufferWriter{T}" /> stands in for the client's
///     <c>NatsPooledBufferWriter</c>, and the per-publish reset is charged to every shape identically,
///     so it cancels. The client's own copy out of that writer into its pipe writer is untouched by
///     this change and is not measured either — it is the same in all three.
/// </summary>
[MemoryDiagnoser]
public class NatsEncodeBenchmarks
{
    // The DenseAndSparse shape in ClusterTrackerBenchmarks settles at ~1000 peers in 9 clusters, and
    // the wallet lists are what an engine.islands payload is mostly made of.
    private const int CLUSTERS = 9;
    private const int PEERS = 1000;

    // Fake addresses, filled to the 42 characters of a real Ethereum address so the encoded size is
    // realistic.
    private const string WALLET_PREFIX = "0x00000000000000000000000000000000";

    private readonly PeerClusterChange change = new () { ClusterId = "C1234", Realm = "main" };
    private readonly IslandStatusMessage topology = BuildTopology();

    // Sized past the largest payload so neither shape is measuring the writer's growth.
    private readonly ArrayBufferWriter<byte> writer = new (128 * 1024);

    /// <summary>
    ///     One <c>peer.{addr}.cluster_change</c> the way the publisher first encoded it: a fresh array
    ///     per publish, then copied into the client's writer.
    /// </summary>
    [Benchmark(Description = "Cluster change — ToByteArray, then copied in")]
    public void ChangeToByteArray()
    {
        EncodeViaFreshArray(change);
    }

    /// <summary>
    ///     The same message the way it was encoded next: sized, written into a pooled rent, then copied
    ///     into the client's writer.
    /// </summary>
    [Benchmark(Description = "Cluster change — pooled bytes, then copied in")]
    public void ChangePooledBytes()
    {
        EncodeViaPooledBytes(change);
    }

    /// <summary>
    ///     The same message written straight into the client's writer.
    /// </summary>
    [Benchmark(Description = "Cluster change — straight into the writer")]
    public void ChangeZeroCopy()
    {
        EncodeIntoWriter(change);
    }

    [Benchmark(Description = "Topology — ToByteArray, then copied in")]
    public void TopologyToByteArray()
    {
        EncodeViaFreshArray(topology);
    }

    [Benchmark(Description = "Topology — pooled bytes, then copied in")]
    public void TopologyPooledBytes()
    {
        EncodeViaPooledBytes(topology);
    }

    [Benchmark(Description = "Topology — straight into the writer")]
    public void TopologyZeroCopy()
    {
        EncodeIntoWriter(topology);
    }

    /// <summary>
    ///     Reports the encoded size of each message, so a change in the shape being measured is visible
    ///     in the log rather than only in the timings.
    /// </summary>
    [GlobalSetup]
    public void ReportSizes()
    {
        Console.WriteLine($"// cluster change={change.CalculateSize()} B, "
                          + $"topology={topology.CalculateSize()} B ({CLUSTERS} islands, {PEERS} wallets)");
    }

    /// <summary>
    ///     The shape the publisher started with: a fresh array per publish, and the client's raw
    ///     serializer copying it into the buffer the client actually publishes from.
    /// </summary>
    private void EncodeViaFreshArray(IMessage message)
    {
        writer.ResetWrittenCount();
        writer.Write(message.ToByteArray());
    }

    /// <summary>
    ///     The shape it had next: a size pass, a pooled rent, an encode into it, and the same copy into
    ///     the client's buffer.
    /// </summary>
    private void EncodeViaPooledBytes(IMessage message)
    {
        writer.ResetWrittenCount();

        int size = message.CalculateSize();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);

        try
        {
            message.WriteTo(new Span<byte>(buffer, 0, size));
            writer.Write(new ReadOnlySpan<byte>(buffer, 0, size));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>
    ///     The shape the publisher has, through the serializer it ships: protobuf writes into the
    ///     client's buffer and nothing else touches the bytes.
    /// </summary>
    private void EncodeIntoWriter(IMessage message)
    {
        writer.ResetWrittenCount();

        NatsPublisher.SERIALIZER.Serialize(writer, message);
    }

    /// <summary>
    ///     An <c>engine.islands</c> snapshot in the shape <c>NatsPublisher</c> builds: every island
    ///     carrying its geometry and no peer cap, and every peer filed under one of them.
    /// </summary>
    private static IslandStatusMessage BuildTopology()
    {
        var message = new IslandStatusMessage();

        for (var i = 0; i < CLUSTERS; i++)
            message.Data.Add(new IslandData
            {
                Id = $"C{i + 1}",
                MaxPeers = 0,
                Radius = 60f + i,
                Center = new Decentraland.Common.Position { X = i * 400f, Y = 0f, Z = i * 300f },
            });

        for (var peer = 0; peer < PEERS; peer++)
            message.Data[peer % CLUSTERS].Peers.Add($"{WALLET_PREFIX}{peer:x8}");

        return message;
    }
}
