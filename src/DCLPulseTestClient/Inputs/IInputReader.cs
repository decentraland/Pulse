namespace PulseTestClient.Inputs;

public interface IInputReader
{
    void Update(float deltaTime, InputState state);
}