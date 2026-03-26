namespace PulseTestClient.Inputs;

public class PlayLoopEmote(string emote) : IInputReader
{
    private bool played;
    
    public void Update(float deltaTime, InputState state)
    {
        if (played) return;

        state.EmoteId = emote;
        played = true;
    }
}