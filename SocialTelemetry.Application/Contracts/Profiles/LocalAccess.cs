namespace SocialTelemetry.Application.Contracts.Profiles;

// Opaque device evidence is a bearer secret, never a profile assertion or a URL parameter.
public sealed record LocalAccessEvidence(string? DeviceToken);
public sealed record LocalAccessBinding(Guid GrantId, Guid AccountId, Guid SecurityEpoch, LocalAccessEvidence Evidence);

public enum LocalAccessFailure { Required, InvalidCredentials, LockedOut, InvalidInput, SecurityChanged, StorageUnavailable, LocalRequestRequired }

public sealed class LocalAccessException(LocalAccessFailure failure) : Exception("Local access failed.")
{
    public LocalAccessFailure Failure { get; } = failure;
}

public interface ILocalProfileAdmission
{
    Task ValidateAsync(LocalProfileBinding binding, CancellationToken cancellationToken);
    Task RecordActivityAsync(LocalProfileBinding binding, CancellationToken cancellationToken);
}

// HTTP and future in-process hosts supply the same evidence. Setting it does not authorize anything.
public sealed class LocalAccessRequest
{
    public LocalAccessEvidence Evidence { get; private set; } = new(null);
    public void SetEvidence(LocalAccessEvidence evidence) => Evidence = evidence;
}

public sealed class LocalOperationAdmission(CancellationTokenSource cancellation, Action restore) : IDisposable
{
    public CancellationToken CancellationToken => cancellation.Token;
    public void Dispose() { restore(); cancellation.Dispose(); }
}
