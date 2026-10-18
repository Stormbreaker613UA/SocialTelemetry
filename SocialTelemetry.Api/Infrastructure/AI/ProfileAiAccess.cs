using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.AI.Models;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Profiles;

namespace SocialTelemetry.Api.Infrastructure.AI;

public sealed class ProfileAiAccess(AppDbContext database, LocalProfileOperation operation,
    ProfileAiLifetimes lifetimes, ChatGptConnection connection)
{
    // Adapter identifier belongs here, never in social domain or analysis feature code.
    private const string ProviderId = "chatgpt-plan";

    public async Task<ProfileAiSelection?> ReadAsync(CancellationToken cancellationToken) =>
        await database.Set<ProfileAiSelection>().AsNoTracking()
            .SingleOrDefaultAsync(selection => selection.UserProfileId == operation.RequireOwner(), cancellationToken);

    public async Task<(AiExecutionBinding Binding, string Provider, string Model, CancellationToken Lifetime)> CaptureAsync(CancellationToken cancellationToken)
    {
        var captured = await CaptureSelectionAsync(null, cancellationToken);
        var selection = captured.Selection;
        if (selection is not { Enabled: true, ConnectionId: { } connectionId }) throw new AiProviderException(AiFailure.NotConnected);
        if (selection.ModelId is null) throw new AiProviderException(AiFailure.ModelNotSelected);
        return (new AiExecutionBinding(selection.UserProfileId, connectionId, selection.Generation), selection.ProviderId,
            selection.ModelId, captured.Lifetime);
    }

    private async Task<(ProfileAiSelection Selection, CancellationToken Lifetime)> CaptureSelectionAsync(
        Guid? expectedGeneration, CancellationToken cancellationToken)
    {
        var observed = await ReadAsync(cancellationToken) ?? throw new AiProviderException(AiFailure.NotConnected);
        if (expectedGeneration is { } expected && observed.Generation != expected)
            throw new AiProviderException(AiFailure.ExecutionInvalidated);

        await lifetimes.Changes.WaitAsync(cancellationToken);
        CancellationTokenSource? retired = null;
        try
        {
            // The first read can outlive a local change. Re-read under the publication
            // coordinator; never infer ordering from GUIDs or republish a stale generation.
            var current = await ReadAsync(cancellationToken);
            if (current is null || current.Generation != observed.Generation)
                throw new AiProviderException(AiFailure.ExecutionInvalidated);
            retired = lifetimes.Publish(current.UserProfileId, current.Generation);
            return (current, lifetimes.Capture(current.UserProfileId, current.Generation));
        }
        finally
        {
            lifetimes.Changes.Release();
            ProfileAiLifetimes.Cancel(retired);
        }
    }

    public async Task ValidateAsync(AiExecutionBinding binding, CancellationToken cancellationToken)
    {
        if (binding.UserProfileId != operation.RequireOwner()) throw new AiProviderException(AiFailure.ExecutionInvalidated);
        var selection = await ReadAsync(cancellationToken);
        if (selection is not { Enabled: true } || selection.ConnectionId != binding.ConnectionId || selection.Generation != binding.SelectionGeneration)
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
    }

    public async Task ProtectAsync(AiExecutionBinding binding, CancellationToken cancellationToken)
    {
        await database.LockAnalysisContextAsync(binding.UserProfileId, cancellationToken);
        await ValidateAsync(binding, cancellationToken);
    }

    public async Task<ChatGptConnectionStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var selection = await ReadAsync(cancellationToken);
        var status = await connection.GetOwnedStatusAsync(operation.RequireOwner(), selection?.ConnectionId, cancellationToken);
        return status with { SelectedModel = selection?.ModelId, Connected = selection?.Enabled == true && status.Connected,
            InferencePermissionAvailable = selection?.Enabled == true && status.InferencePermissionAvailable };
    }

    public Task<IReadOnlyList<ChatGptAccountSummary>> UnassignedAsync(CancellationToken cancellationToken)
    {
        operation.RequireOwner();
        return connection.GetUnassignedAsync(cancellationToken);
    }

    public async Task SelectConnectionAsync(Guid connectionId, bool adoptUnassigned, CancellationToken cancellationToken)
    {
        var before = await ReadAsync(cancellationToken);
        var model = await connection.ClaimAsync(operation.RequireOwner(), connectionId, adoptUnassigned, cancellationToken);
        // Protected claim precedes SQL publication. SQL failure cannot give another owner access;
        // the connection remains exclusively claimed and the same owner can explicitly retry.
        await ChangeAsync(before?.Generation ?? Guid.Empty, selection =>
        {
            selection.ProviderId = ProviderId;
            selection.ConnectionId = connectionId;
            selection.ModelId = before?.ConnectionId == connectionId ? before.ModelId : model;
            selection.Enabled = true;
        }, cancellationToken);
    }

    public async Task<ChatGptModelCatalog> ModelsAsync(CancellationToken cancellationToken)
    {
        var selection = await ReadAsync(cancellationToken);
        if (selection is not { Enabled: true, ConnectionId: { } id }) throw new AiProviderException(AiFailure.NotConnected);
        return await connection.GetOwnedModelsAsync(operation.RequireOwner(), id, selection.ModelId, cancellationToken);
    }

    public async Task SelectModelAsync(string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200) throw new AiProviderException(AiFailure.ModelUnavailable);
        var before = await ReadAsync(cancellationToken);
        if (before is not { Enabled: true, ConnectionId: { } id }) throw new AiProviderException(AiFailure.NotConnected);
        var catalog = await connection.GetOwnedModelsAsync(operation.RequireOwner(), id, before.ModelId, cancellationToken);
        if (!catalog.Models.Any(candidate => candidate.Id == model)) throw new AiProviderException(AiFailure.ModelUnavailable);
        if (before.ModelId == model) return;
        await ChangeAsync(before.Generation, selection => selection.ModelId = model, cancellationToken);
    }

    public async Task BeginAsync(Guid? connectionId, bool newAccount, bool requestConsent, CancellationToken cancellationToken)
    {
        await connection.AuthorizationGate.WaitAsync(cancellationToken);
        try
        {
            await connection.EnsureCanBeginAsync(cancellationToken);
            var before = await ReadAsync(cancellationToken);
            var selectedId = newAccount ? null : connectionId ?? before?.ConnectionId;
            if (selectedId is { } id)
            {
                var status = await connection.GetOwnedStatusAsync(operation.RequireOwner(), id, cancellationToken);
                if (status.ConnectionId != id) throw new AiProviderException(AiFailure.NotConnected);
            }
            // Starting authorization invalidates old executions, but a failed reconnect retains
            // the prior owned selection/credentials. Only successful publication selects a new connection.
            var pendingSelection = await ChangeAsync(before?.Generation ?? Guid.Empty, _ => { }, cancellationToken);
            var captured = await CaptureSelectionAsync(pendingSelection.Generation, cancellationToken);
            await connection.BeginAsync(selectedId, newAccount, requestConsent, cancellationToken,
                operation.Binding, pendingSelection.Generation, operation.Lifetime, captured.Lifetime);
        }
        finally { connection.AuthorizationGate.Release(); }
    }

    public async Task<ChatGptConnectionStatus> CompleteAsync(string? state, string? code, string? clientId, string? error, CancellationToken cancellationToken)
    {
        // This adapter phase owns only the protected file; it releases that lock before SQL.
        var completed = await connection.CompleteBoundAsync(state, code, clientId, error, cancellationToken);
        var binding = completed.Binding ?? throw new AiProviderException(AiFailure.InvalidCallback);
        operation.Bind(binding, completed.OwnerLifetime);
        if (completed.Status.ConnectionId is not { } id || completed.Generation is not { } generation)
            throw new AiProviderException(AiFailure.InvalidCallback);
        await ChangeAsync(generation, selection =>
        {
            if (selection.ConnectionId != id) selection.ModelId = null;
            selection.ProviderId = ProviderId;
            selection.ConnectionId = id;
            selection.Enabled = true;
        }, cancellationToken);
        return await StatusAsync(cancellationToken);
    }

    public async Task<ChatGptDisconnectResult> DisconnectAsync(CancellationToken cancellationToken)
    {
        var before = await ReadAsync(cancellationToken);
        await ChangeAsync(before?.Generation ?? Guid.Empty, selection => selection.Enabled = false, cancellationToken);
        // SQL disable/revocation generation is durable before file/network work. File failure
        // cannot re-enable inference, including after a host restart.
        return await connection.DisconnectOwnedAsync(operation.RequireOwner(), before?.ConnectionId, cancellationToken);
    }

    private async Task<ProfileAiSelection> ChangeAsync(Guid expectedGeneration, Action<ProfileAiSelection> change, CancellationToken cancellationToken)
    {
        await lifetimes.Changes.WaitAsync(cancellationToken);
        CancellationTokenSource? retired = null;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var owner = operation.RequireOwner();
            await database.LockAnalysisContextAsync(owner, cancellationToken);
            var selection = await database.Set<ProfileAiSelection>().SingleOrDefaultAsync(selection => selection.UserProfileId == owner, cancellationToken);
            if ((selection?.Generation ?? Guid.Empty) != expectedGeneration) throw new AiProviderException(AiFailure.ExecutionInvalidated);
            if (selection is null)
            {
                selection = new ProfileAiSelection { UserProfileId = owner };
                database.Set<ProfileAiSelection>().Add(selection);
            }
            change(selection);
            selection.Generation = Guid.NewGuid();
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            retired = lifetimes.Publish(owner, selection.Generation);
            return selection;
        }
        finally
        {
            lifetimes.Changes.Release();
            ProfileAiLifetimes.Cancel(retired);
        }
    }
}
