namespace SocialTelemetry.Application.Contracts.Profiles;

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
