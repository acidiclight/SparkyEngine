namespace Sparky;

public class SparkyException : Exception
{
    public SparkyException(string message)
        : base($"A Sparky error has occurred: {message}")
    {
            
    }
}