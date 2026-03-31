namespace PulseTestClient.Inputs;

public class Bot : IInputReader
{
    private readonly Random random = new();
    private readonly FastNoiseLite noiseForward;
    private readonly FastNoiseLite noiseStrafe;
    private readonly FastNoiseLite noiseRotation;
    private readonly string[] emotes;
    private readonly float moveSpeed;
    private readonly float noiseSpeed;
    private readonly float emoteInterval;
    private readonly float emoteCooldown;
    private readonly bool jumpEnabled;
    private readonly float jumpMinInterval;
    private readonly float jumpMaxInterval;
    private float time;
    private float emoteElapsed;
    private float emoteCooldownRemaining;
    private float jumpCountdown;

    public Bot(
        string[] emotes,
        float moveSpeed = 5f,
        float noiseSpeed = 0.3f,
        float emoteInterval = 5f,
        float emoteCooldown = 5f,
        bool jumpEnabled = true,
        float jumpMinInterval = 3f,
        float jumpMaxInterval = 10f)
    {
        noiseForward = CreateNoise(random.Next());
        noiseStrafe = CreateNoise(random.Next());
        noiseRotation = CreateNoise(random.Next());
        this.emotes = emotes;
        this.moveSpeed = moveSpeed;
        this.noiseSpeed = noiseSpeed;
        this.emoteInterval = emoteInterval;
        this.emoteCooldown = emoteCooldown;
        this.jumpEnabled = jumpEnabled;
        this.jumpMinInterval = jumpMinInterval;
        this.jumpMaxInterval = jumpMaxInterval;
        jumpCountdown = NextJumpDelay();
    }

    public void Update(float deltaTime, InputState state)
    {
        time += deltaTime;
        emoteCooldownRemaining -= deltaTime;

        if (emoteCooldownRemaining <= 0f)
            emoteElapsed += deltaTime;

        if (emoteElapsed >= emoteInterval)
        {
            emoteElapsed = 0f;
            emoteCooldownRemaining = emoteCooldown;
            if (emotes.Length > 0)
                state.EmoteId = emotes[random.Next(emotes.Length)];
            return;
        }

        if (emoteCooldownRemaining > 0f)
            return;

        if (jumpEnabled)
        {
            jumpCountdown -= deltaTime;

            if (jumpCountdown <= 0f)
            {
                state.Jump = true;
                jumpCountdown = NextJumpDelay();
            }
        }

        float t = time * noiseSpeed;
        state.Velocity.Z = noiseForward.GetNoise(t, 0) * moveSpeed;
        state.Velocity.X = noiseStrafe.GetNoise(t, 0) * moveSpeed;
        state.RotationDelta = noiseRotation.GetNoise(t, 0);
    }

    private float NextJumpDelay() =>
        jumpMinInterval + (random.NextSingle() * (jumpMaxInterval - jumpMinInterval));

    private static FastNoiseLite CreateNoise(int seed)
    {
        var noise = new FastNoiseLite(seed);
        noise.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
        noise.SetFrequency(0.5f);
        return noise;
    }
}