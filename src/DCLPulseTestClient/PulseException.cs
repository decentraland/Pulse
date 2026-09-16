namespace PulseTestClient
{
    public class PulseException : Exception
    {
        public PulseException(string message) : base(message) { }

        public PulseException(string message, Exception innerException) : base(message, innerException) { }
    }
}
