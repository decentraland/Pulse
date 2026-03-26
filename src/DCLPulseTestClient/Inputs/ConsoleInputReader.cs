namespace PulseTestClient.Inputs;

public class ConsoleInputReader(string[] emotes) : IInputReader
{
    private ConsoleKey lastKey;

    public void Update(float dt, InputState state)
    {
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