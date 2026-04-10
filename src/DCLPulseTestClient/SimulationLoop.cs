using System.Numerics;
using Decentraland.Pulse;
using Pulse.Transport;
using PulseTestClient.Networking;
using PulseTestClient.Timing;

namespace PulseTestClient;

public class SimulationLoop(
    IReadOnlyList<BotSession> sessions,
    ClientOptions options,
    BotBehaviorSettings behavior,
    ParcelEncoder parcelEncoder,
    ITimeProvider timeProvider)
{
    private const float TICK_RATE = 1 / 30f;
    private const int TICK_RATE_MS = (int)(TICK_RATE * 1000);

    public async Task RunAsync(CancellationToken ct)
    {
        // Stagger bots evenly across one tick interval so they don't all fire at once
        uint now = timeProvider.TimeSinceStartupMs;

        for (var i = 0; i < sessions.Count; i++)
            sessions[i].NextTickMs = now + (uint)(TICK_RATE_MS * i / sessions.Count);

        while (!ct.IsCancellationRequested)
        {
            now = timeProvider.TimeSinceStartupMs;
            var anyUpdated = false;

            foreach (BotSession bot in sessions)
            {
                if (now < bot.NextTickMs)
                    continue;

                bot.NextTickMs = now + TICK_RATE_MS;
                anyUpdated = true;

                UpdateBot(bot);

                if (bot.InputCollector.Quit)
                    return;
            }

            if (!anyUpdated)
                await Task.Delay(1, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private void UpdateBot(BotSession bot)
    {
        float deltaTimeSecs = (timeProvider.TimeSinceStartupMs - bot.LastFrameTick) / 1000f;
        bot.LastFrameTick = timeProvider.TimeSinceStartupMs;

        bot.InputCollector.Reset();
        bot.InputReader.Update(deltaTimeSecs, bot.InputCollector);

        if (bot.InputCollector.Quit)
            return;

        float dx = bot.InputCollector.Velocity.X;
        float dz = bot.InputCollector.Velocity.Z;
        float dRot = bot.InputCollector.RotationDelta;
        bool moving = dx != 0f || dz != 0f;

        float rotationY = bot.RotationY;

        if (moving)
            rotationY = MathF.Atan2(dx, dz) * (180f / MathF.PI);
        else
            rotationY += dRot * options.RotateSpeed * TICK_RATE;

        bot.RotationY = rotationY;

        float radY = rotationY * MathF.PI / 180f;
        float forward = dz * TICK_RATE;
        float strafe = dx * TICK_RATE;

        Vector3 pos = bot.Position;
        pos.X += (MathF.Sin(radY) * forward) + (MathF.Cos(radY) * strafe);
        pos.Z += (MathF.Cos(radY) * forward) - (MathF.Sin(radY) * strafe);

        // Jump initiation
        if (bot.InputCollector.Jump && !bot.Airborne)
        {
            bot.JumpCount++;
            bot.Airborne = true;
            bot.GroundY = pos.Y;
            bot.VerticalVelocity = behavior.JumpVelocity;
        }

        // Vertical physics
        if (bot.Airborne)
        {
            bot.VerticalVelocity -= behavior.Gravity * deltaTimeSecs;
            pos.Y += bot.VerticalVelocity * deltaTimeSecs;

            if (pos.Y <= bot.GroundY)
            {
                pos.Y = bot.GroundY;
                bot.VerticalVelocity = 0f;
                bot.Airborne = false;
            }
        }

        // Clamp to dispersion radius
        float offsetX = pos.X - bot.SpawnOrigin.X;
        float offsetZ = pos.Z - bot.SpawnOrigin.Z;
        float distSq = (offsetX * offsetX) + (offsetZ * offsetZ);

        if (distSq > options.DispersionRadius * options.DispersionRadius)
        {
            float dist = MathF.Sqrt(distSq);
            pos.X = bot.SpawnOrigin.X + (offsetX / dist * options.DispersionRadius);
            pos.Z = bot.SpawnOrigin.Z + (offsetZ / dist * options.DispersionRadius);
        }

        bot.Position = pos;

        var velocity = new Vector3(
            (MathF.Sin(radY) * dz) + (MathF.Cos(radY) * dx),
            bot.VerticalVelocity,
            (MathF.Cos(radY) * dz) - (MathF.Sin(radY) * dx));

        uint stateFlags = bot.Airborne
            ? (uint)PlayerAnimationFlags.None
            : (uint)PlayerAnimationFlags.Grounded;

        int parcelIndex = parcelEncoder.EncodeGlobalPosition(bot.Position, out Vector3 relativePosition);

        bot.Pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
        {
            Input = new PlayerStateInput
            {
                State = new PlayerState
                {
                    HeadPitch = 0f,
                    HeadYaw = rotationY,
                    GlideState = new GlideState(),
                    MovementBlend = moving ? 1f : 0f,
                    Position = new Decentraland.Common.Vector3
                        { X = relativePosition.X, Y = relativePosition.Y, Z = relativePosition.Z },
                    RotationY = rotationY,
                    SlideBlend = 0f,
                    StateFlags = stateFlags,
                    JumpCount = bot.JumpCount,
                    ParcelIndex = parcelIndex,
                    Velocity = new Decentraland.Common.Vector3 { X = velocity.X, Y = velocity.Y, Z = velocity.Z },
                },
            },
        }, PacketMode.UNRELIABLE_SEQUENCED));

        if (bot.InputCollector.EmoteId is { } emoteId)
        {
            bot.Pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
            {
                EmoteStart = new EmoteStart { EmoteId = emoteId },
            }, PacketMode.RELIABLE));
        }
    }
}
