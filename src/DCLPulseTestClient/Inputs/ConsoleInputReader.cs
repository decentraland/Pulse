namespace PulseTestClient.Inputs;

public class ConsoleInputReader(string[] emotes) : IInputReader
{
    // Console.KeyAvailable throws outright when stdin is redirected or there is no console, which is
    // every non-interactive run — a CI job, or a local run with the output piped to a file. Reading
    // it once at construction rather than guarding each poll: it cannot change for the process, and
    // the single-bot path polls this every frame.
    private static readonly bool KEYBOARD_AVAILABLE = !Console.IsInputRedirected;

    private ConsoleKey lastKey;

    public void Update(float dt, InputState state)
    {
        // No keyboard means no manual quit: the bot input drives, and the run ends on its own
        // deadline or on Ctrl+C. Silently doing nothing is right here — a non-interactive run asked
        // for no keyboard, so this is not a degraded mode to warn about.
        if (!KEYBOARD_AVAILABLE)
            return;

        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true).Key;

            switch (key)
            {
                case ConsoleKey.W: state.Velocity.Z += 1f; break;
                case ConsoleKey.S: state.Velocity.Z -= 1f; break;
                case ConsoleKey.A: state.Velocity.X -= 1f; break;
                case ConsoleKey.D: state.Velocity.X += 1f; break;
                case ConsoleKey.Q: state.RotationDelta -= 1f; break;
                case ConsoleKey.E: state.RotationDelta += 1f; break;
                case ConsoleKey.Escape: state.Quit = true; return;
                case >= ConsoleKey.D0 and <= ConsoleKey.D9 when lastKey == ConsoleKey.B:
                    int index = key - ConsoleKey.D0;
                    if (index < emotes.Length)
                        state.EmoteId = emotes[index];
                    break;
            }

            lastKey = key;
        }
    }
}