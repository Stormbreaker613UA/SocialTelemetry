using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Features.LocalProfiles;

namespace SocialTelemetry.Application.Features.LocalAccess;

public sealed record AccessState(Guid? UserProfileId, Guid Version, bool Protected, bool Locked,
    bool Remembered, DateTimeOffset? ExpiresAt)
{
    public string Mode => "Local";
    public string Source => "Local";
}

public sealed record AccessChange(SelectionSnapshot Selection, LocalAccessEvidence? DeviceEvidence = null, string? RecoveryCode = null)
{
    public bool PersistentDevice { get; init; }
    public DateTimeOffset? DeviceExpiresAt { get; init; }
}
public sealed record PasswordSignIn(string Login, string Password, Guid UserProfileId, Guid ExpectedVersion, bool Remember);
public sealed record ProtectionEnrollment(string Login, string Password);
public sealed record PasswordChange(string CurrentPassword, string NewPassword);
public sealed record RecoveryInput(string Login, string RecoveryCode, string NewPassword);

public interface ILocalAccessManagement
{
    Task<AccessState> StatusAsync(CancellationToken cancellationToken);
    Task<AccessChange> EnableAsync(ProtectionEnrollment input, CancellationToken cancellationToken);
    Task<AccessChange> SignInAsync(PasswordSignIn input, CancellationToken cancellationToken);
    Task<AccessChange> ResumeAsync(Guid userProfileId, Guid expectedVersion, CancellationToken cancellationToken);
    Task<AccessChange> ChangePasswordAsync(PasswordChange input, CancellationToken cancellationToken);
    Task<AccessChange> DisableAsync(string password, CancellationToken cancellationToken);
    Task<AccessChange> LockAsync(Guid userProfileId, CancellationToken cancellationToken);
    Task<AccessChange> RotateRecoveryAsync(string password, CancellationToken cancellationToken);
    Task<AccessChange> RecoverAsync(RecoveryInput input, CancellationToken cancellationToken);
    Task ActivityAsync(CancellationToken cancellationToken);
}

public static class LocalAccessInput
{
    // Bound hostile inputs before password hashing, normalization, or database work.
    public static void Check(string? secret, int maximum = 1024)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > maximum)
            throw new LocalAccessException(LocalAccessFailure.InvalidInput);
    }
}
