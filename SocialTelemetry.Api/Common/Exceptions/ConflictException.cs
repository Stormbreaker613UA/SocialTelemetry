namespace SocialTelemetry.Api.Common.Exceptions;

public sealed class ConflictException : Exception
{
    public ConflictException()
    {
    }

    public ConflictException(string message) : base(message)
    {
    }
}
