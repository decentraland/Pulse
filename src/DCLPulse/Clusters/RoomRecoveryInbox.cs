using System.Threading.Channels;

namespace Pulse.Clusters;

/// <summary>Bounds backend confirmations until the tracker applies them on its own thread.</summary>
public sealed class RoomRecoveryInbox
{
    internal const int MAX_CONFIRMATIONS_PER_PASS = 1024;
    private readonly Channel<RoomRecoveryConfirmation> confirmations;

    public RoomRecoveryInbox(int capacity = MAX_CONFIRMATIONS_PER_PASS)
    {
        confirmations = Channel.CreateBounded<RoomRecoveryConfirmation>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    internal bool TryWrite(RoomRecoveryConfirmation confirmation) => confirmations.Writer.TryWrite(confirmation);

    internal bool TryRead(out RoomRecoveryConfirmation confirmation) => confirmations.Reader.TryRead(out confirmation);
}

internal readonly record struct RoomRecoveryConfirmation(
    string Wallet, string Epoch, string Revision, string OperationId, string ClusterId, ulong RevokeBefore,
    bool Bootstrap = false, bool ObservedReady = false);
