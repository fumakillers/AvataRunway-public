namespace AvataRunway.Core;

public sealed class InvalidVrmDataException : Exception
{
    public InvalidVrmDataException(string message)
        : base(message)
    {
    }

    public InvalidVrmDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
