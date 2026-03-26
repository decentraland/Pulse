using System.Numerics;

namespace PulseTestClient.Inputs;

public class InputState
{
    public Vector3 Velocity;
    public float RotationDelta;
    public string? EmoteId;
    public bool Quit;

    public void Reset()
    {
        Velocity = Vector3.Zero;
        RotationDelta = 0f;
        EmoteId = null;
        Quit = false;
    }
}