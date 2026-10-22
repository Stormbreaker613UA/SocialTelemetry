namespace SocialTelemetry.Application.Contracts.Profiles;

public sealed record LocalProfileBinding(Guid UserProfileId, Guid SelectionVersion)
{
    public LocalAccessBinding? Access { get; init; }
}

// Scoped and assigned once before dispatch. No feature may reread the selector to retarget work.
public sealed class LocalProfileOperation
{
    public LocalProfileBinding? Binding { get; private set; }
    public CancellationToken Lifetime { get; private set; }
    private ILocalProfileAdmission? admission;
    public bool UserMutation { get; private set; }

    public void Bind(LocalProfileBinding binding, CancellationToken lifetime)
    {
        if (Binding is not null) throw new InvalidOperationException("The operation already has an owner.");
        Binding = binding;
        Lifetime = lifetime;
    }

    internal void Authorize(ILocalProfileAdmission verifiedAdmission) => admission = verifiedAdmission;

    public async Task<LocalOperationAdmission> EnterAsync(CancellationToken cancellationToken, bool userMutation = false)
    {
        var captured = Binding ?? throw new LocalProfileAccessException(LocalProfileFailure.SelectionRequired);
        var verifier = admission ?? throw new LocalAccessException(LocalAccessFailure.Required);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            await verifier.ValidateAsync(captured, linked.Token);
            var previous = UserMutation;
            UserMutation = userMutation;
            return new LocalOperationAdmission(linked, () => UserMutation = previous);
        }
        catch { linked.Dispose(); throw; }
    }

    public Task RecordStartAsync(CancellationToken cancellationToken)
    {
        var captured = Binding ?? throw new LocalAccessException(LocalAccessFailure.Required);
        return (admission ?? throw new LocalAccessException(LocalAccessFailure.Required)).RecordActivityAsync(captured, cancellationToken);
    }

    public Guid RequireOwner() => Binding?.UserProfileId
        ?? throw new LocalProfileAccessException(LocalProfileFailure.SelectionRequired);
}
