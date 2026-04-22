using System.Collections.Concurrent;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace DCLPulseTests;

[TestFixture]
public class IdentityBoardTests
{
    private const int MAX_PEERS = 256;

    // ── Contract tests ──────────────────────────────────────────────────
    //
    // These tests pin the wallet ↔ PeerIndex mapping contract that the upcoming
    // allocator refactor depends on. Specifically, the "same wallet → same PeerIndex
    // on reconnect" invariant is built on `TryGetPeerIndexByWallet` returning the
    // currently-associated index for a wallet, and `Remove(PeerIndex)` clearing both
    // directions of the mapping.

    [Test]
    public void Set_ThenGetWalletByPeerIndex_ReturnsWallet()
    {
        var board = new IdentityBoard(MAX_PEERS);

        board.Set(new PeerIndex(7), "0xABC");

        Assert.That(board.GetWalletIdByPeerIndex(new PeerIndex(7)), Is.EqualTo("0xABC"));
    }

    [Test]
    public void Set_ThenTryGetPeerIndexByWallet_ReturnsPeerIndex()
    {
        var board = new IdentityBoard(MAX_PEERS);

        board.Set(new PeerIndex(7), "0xABC");

        Assert.That(board.TryGetPeerIndexByWallet("0xABC", out PeerIndex pi), Is.True);
        Assert.That(pi, Is.EqualTo(new PeerIndex(7)));
    }

    [Test]
    public void TryGetPeerIndexByWallet_IsCaseInsensitive()
    {
        var board = new IdentityBoard(MAX_PEERS);

        board.Set(new PeerIndex(7), "0xAbCdEf");

        Assert.That(board.TryGetPeerIndexByWallet("0xabcdef", out PeerIndex pi), Is.True);
        Assert.That(pi, Is.EqualTo(new PeerIndex(7)));
        Assert.That(board.TryGetPeerIndexByWallet("0XABCDEF", out pi), Is.True);
        Assert.That(pi, Is.EqualTo(new PeerIndex(7)));
    }

    [Test]
    public void TryGetPeerIndexByWallet_UnknownWallet_ReturnsFalse()
    {
        var board = new IdentityBoard(MAX_PEERS);

        Assert.That(board.TryGetPeerIndexByWallet("0xUNKNOWN", out _), Is.False);
    }

    [Test]
    public void GetWalletByPeerIndex_UnsetSlot_ReturnsNull()
    {
        var board = new IdentityBoard(MAX_PEERS);

        Assert.That(board.GetWalletIdByPeerIndex(new PeerIndex(5)), Is.Null);
    }

    [Test]
    public void Remove_ClearsBothDirections()
    {
        var board = new IdentityBoard(MAX_PEERS);
        board.Set(new PeerIndex(7), "0xABC");

        board.Remove(new PeerIndex(7));

        Assert.That(board.GetWalletIdByPeerIndex(new PeerIndex(7)), Is.Null);
        Assert.That(board.TryGetPeerIndexByWallet("0xABC", out _), Is.False);
    }

    [Test]
    public void Remove_UnknownPeerIndex_IsNoOp()
    {
        var board = new IdentityBoard(MAX_PEERS);
        board.Set(new PeerIndex(7), "0xABC");

        board.Remove(new PeerIndex(42)); // never set

        Assert.That(board.GetWalletIdByPeerIndex(new PeerIndex(7)), Is.EqualTo("0xABC"));
        Assert.That(board.TryGetPeerIndexByWallet("0xABC", out PeerIndex pi), Is.True);
        Assert.That(pi, Is.EqualTo(new PeerIndex(7)));
    }

    /// <summary>
    ///     Today, re-associating a wallet with a different PeerIndex leaves the old index still
    ///     mapped to the wallet string (one-way leak in <c>walletsByPeerIds</c>) — only the
    ///     wallet → index direction is overwritten. This test pins the current behavior so the
    ///     refactor either preserves it deliberately or fixes it deliberately.
    /// </summary>
    [Test]
    public void Set_SameWalletOnDifferentPeerIndex_ForwardMapMovesReverseMapDoesNot()
    {
        var board = new IdentityBoard(MAX_PEERS);
        board.Set(new PeerIndex(7), "0xABC");

        board.Set(new PeerIndex(12), "0xABC");

        // Forward map: wallet now points at the new PeerIndex.
        Assert.That(board.TryGetPeerIndexByWallet("0xABC", out PeerIndex pi), Is.True);
        Assert.That(pi, Is.EqualTo(new PeerIndex(12)));

        // Reverse map: the old slot still carries the wallet string (current behavior).
        Assert.That(board.GetWalletIdByPeerIndex(new PeerIndex(7)), Is.EqualTo("0xABC"));
        Assert.That(board.GetWalletIdByPeerIndex(new PeerIndex(12)), Is.EqualTo("0xABC"));
    }

    /// <summary>
    ///     Core invariant for the "same wallet → same PeerIndex" refactor: after a clean
    ///     disconnect (Remove), re-authenticating the same wallet must be able to discover
    ///     that the wallet has no live binding, so the allocator can decide whether to reuse
    ///     the old PeerIndex or issue a new one.
    /// </summary>
    [Test]
    public void ReconnectFlow_AfterRemove_LookupYieldsNoBinding()
    {
        var board = new IdentityBoard(MAX_PEERS);
        board.Set(new PeerIndex(7), "0xABC");
        board.Remove(new PeerIndex(7));

        Assert.That(board.TryGetPeerIndexByWallet("0xABC", out _), Is.False,
            "after Remove the wallet has no binding — reconnect allocator sees a clean slate");
    }

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
                                                board.Remove(id);
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
