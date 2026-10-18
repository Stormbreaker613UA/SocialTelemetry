namespace SocialTelemetry.Api.Infrastructure.Profiles;

public sealed record LocalProfileBinding(Guid UserProfileId, Guid SelectionVersion);

// Scoped and assigned once before dispatch. No feature may reread the selector to retarget work.
public sealed class LocalProfileOperation
{
    public LocalProfileBinding? Binding { get; private set; }
    public CancellationToken Lifetime { get; private set; }

    public void Bind(LocalProfileBinding binding, CancellationToken lifetime)
    {
        if (Binding is not null) throw new InvalidOperationException("The operation already has an owner.");
        Binding = binding;
        Lifetime = lifetime;
    }

    public Guid RequireOwner() => Binding?.UserProfileId
        ?? throw new LocalProfileAccessException(LocalProfileFailure.SelectionRequired);
}

public enum LocalProfileFailure
{
    SelectionRequired,
    SelectionChanged,
    AssertionRequired,
    InvalidAssertion,
    ProtectedAccessUnavailable
}

public sealed class LocalProfileAccessException(LocalProfileFailure failure) : Exception(failure switch
{
    LocalProfileFailure.SelectionRequired => "Select a local profile first.",
    LocalProfileFailure.SelectionChanged => "The selected profile changed. Reload before continuing.",
    LocalProfileFailure.AssertionRequired => "The current profile and selection version must be supplied.",
    LocalProfileFailure.InvalidAssertion => "The profile assertion is invalid.",
    LocalProfileFailure.ProtectedAccessUnavailable => "This profile requires account access that is not supported yet.",
    _ => "Local profile access is unavailable."
})
{
    public LocalProfileFailure Failure { get; } = failure;
    public int StatusCode => Failure == LocalProfileFailure.InvalidAssertion ? 400 :
        Failure == LocalProfileFailure.ProtectedAccessUnavailable ? 403 : 409;
}
