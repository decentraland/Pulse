using System.Collections.Concurrent;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace DCLPulseTests;

[TestFixture]
public class IdentityBoardTests
{
    private const int MAX_PEERS = 256;

    [Test]
    public void ConcurrentSetAndGet_ReadersNeverSeeTornValues()
    {
        var board = new IdentityBoard(MAX_PEERS);
        var expectedWallets = new string[MAX_PEERS];

        for (var i = 0; i < MAX_PEERS; i++)
            expectedWallets[i] = $"0xWALLET_{i:X4}";

        var writerCount = 4;
        var readerCount = 4;
        var readIterations = 100_000;

        using var barrier = new Barrier(writerCount + readerCount);
        var errors = new ConcurrentBag<string>();

        Task[] writers = Enumerable.Range(0, writerCount)
                                   .Select(w => Task.Run(() =>
                                    {
                                        barrier.SignalAndWait();

                                        // Each writer owns a stripe, just like the real worker model
                                        for (int i = w; i < MAX_PEERS; i += writerCount) { board.Set(new PeerIndex((uint)i), expectedWallets[i]); }
                                    }))
                                   .ToArray();

        Task[] readers = Enumerable.Range(0, readerCount)
                                   .Select(_ => Task.Run(() =>
                                    {
                                        barrier.SignalAndWait();

                                        for (var iter = 0; iter < readIterations; iter++)
                                        {
                                            for (var i = 0; i < MAX_PEERS; i++)
                                            {
                                                string? value = board.GetWalletIdByPeerIndex(new PeerIndex((uint)i));

                                                // Must be either null (not yet written) or the exact expected value
                                                if (value != null && value != expectedWallets[i])
                                                    errors.Add($"Slot {i}: expected null or '{expectedWallets[i]}', got '{value}'");
                                            }
                                        }
                                    }))
                                   .ToArray();

        Task.WaitAll(writers.Concat(readers).ToArray());

        Assert.That(errors, Is.Empty, $"Torn reads detected:\n{string.Join("\n", errors.Take(10))}");
    }

    [Test]
    public void ConcurrentSetAndClear_ReadersNeverSeeTornValues()
    {
        var board = new IdentityBoard(MAX_PEERS);
        var expectedWallets = new string[MAX_PEERS];

        for (var i = 0; i < MAX_PEERS; i++)
            expectedWallets[i] = $"0xWALLET_{i:X4}";

        var writerCount = 4;
        var readerCount = 4;
        var cycles = 1_000;
        var readIterations = 50_000;

        using var barrier = new Barrier(writerCount + readerCount);
        var errors = new ConcurrentBag<string>();

        // Writers repeatedly set and clear to maximize the chance of torn reads
        Task[] writers = Enumerable.Range(0, writerCount)
                                   .Select(w => Task.Run(() =>
                                    {
                                        barrier.SignalAndWait();

                                        for (var cycle = 0; cycle < cycles; cycle++)
                                        {
                                            for (int i = w; i < MAX_PEERS; i += writerCount)
                                            {
                                                var id = new PeerIndex((uint)i);
                                                board.Set(id, expectedWallets[i]);
                                                board.Clear(id);
                                                board.Set(id, expectedWallets[i]);
                                            }
                                        }
                                    }))
                                   .ToArray();

        Task[] readers = Enumerable.Range(0, readerCount)
                                   .Select(_ => Task.Run(() =>
                                    {
                                        barrier.SignalAndWait();

                                        for (var iter = 0; iter < readIterations; iter++)
                                        {
                                            for (var i = 0; i < MAX_PEERS; i++)
                                            {
                                                string? value = board.GetWalletIdByPeerIndex(new PeerIndex((uint)i));

                                                if (value != null && value != expectedWallets[i])
                                                    errors.Add($"Slot {i}: expected null or '{expectedWallets[i]}', got '{value}'");
                                            }
                                        }
                                    }))
                                   .ToArray();

        Task.WaitAll(writers.Concat(readers).ToArray());

        Assert.That(errors, Is.Empty, $"Torn reads detected:\n{string.Join("\n", errors.Take(10))}");
    }

    [Test]
    public void ConcurrentReaders_AllSeeWrittenValueAfterWriterCompletes()
    {
        var board = new IdentityBoard(MAX_PEERS);
        var expectedWallets = new string[MAX_PEERS];

        for (var i = 0; i < MAX_PEERS; i++)
        {
            expectedWallets[i] = $"0xWALLET_{i:X4}";
            board.Set(new PeerIndex((uint)i), expectedWallets[i]);
        }

        var readerCount = 8;
        var readIterations = 100_000;
        var errors = new ConcurrentBag<string>();

        Task[] readers = Enumerable.Range(0, readerCount)
                                   .Select(_ => Task.Run(() =>
                                    {
                                        for (var iter = 0; iter < readIterations; iter++)
                                        {
                                            for (var i = 0; i < MAX_PEERS; i++)
                                            {
                                                string? value = board.GetWalletIdByPeerIndex(new PeerIndex((uint)i));

                                                if (value != expectedWallets[i])
                                                    errors.Add($"Slot {i}: expected '{expectedWallets[i]}', got '{value}'");
                                            }
                                        }
                                    }))
                                   .ToArray();

        Task.WaitAll(readers);

        Assert.That(errors, Is.Empty, $"Incorrect reads:\n{string.Join("\n", errors.Take(10))}");
    }
}
