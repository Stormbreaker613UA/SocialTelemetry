namespace SocialTelemetry.Application.Contracts.Profiles;

public enum LocalProfileFailure
{
    SelectionRequired,
    SelectionChanged,
    AssertionRequired,
    InvalidAssertion,
    ProtectedAccessUnavailable
}

public sealed class LocalProfileAccessException(LocalProfileFailure failure) : Exception(failure.ToString())
{
    public LocalProfileFailure Failure { get; } = failure;
}
