namespace PulseTestClient.Inputs;

public class BotWithManualExitInput(IInputReader bot, IInputReader keyboard) : IInputReader
{
    public void Update(float deltaTime, InputState state)
    {
        keyboard.Update(deltaTime, state);
        
        if (!state.Quit)
            bot.Update(deltaTime, state);
    }
}