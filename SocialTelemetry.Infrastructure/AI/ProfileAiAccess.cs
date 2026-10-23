using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.AI;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Contracts.AI.Models;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Profiles;
using SocialTelemetry.Application.Features.AiConnection;

namespace SocialTelemetry.Infrastructure.AI;

public sealed class ProfileAiAccess(AppDbContext database, LocalProfileOperation operation,
    ProfileAiLifetimes lifetimes, ChatGptConnection connection,
    SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario? profileResolver = null)
{
    // Adapter identifier belongs here, never in social domain or analysis feature code.
    private const string ProviderId = "chatgpt-plan";

    public async Task<ProfileAiSelection?> ReadAsync(CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        return await database.Set<ProfileAiSelection>().AsNoTracking()
            .SingleOrDefaultAsync(selection => selection.UserProfileId == owner, admission.CancellationToken);
    }

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

    public async Task<IReadOnlyList<ChatGptAccountSummary>> UnassignedAsync(CancellationToken cancellationToken)
    {
        operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        return await connection.GetUnassignedAsync(admission.CancellationToken);
    }

    public async Task SelectConnectionAsync(Guid connectionId, bool adoptUnassigned, CancellationToken cancellationToken)
    {
        var before = await ReadAsync(cancellationToken);
        string? model;
        // Claims still precede SQL publication, but admission must serialize with journal deletion.
        // Acquire SQL before the credential file; neither protection spans provider/network work.
        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            await database.LockAnalysisContextAsync(operation.RequireOwner(), cancellationToken);
            model = await connection.ClaimAsync(operation.RequireOwner(), connectionId, adoptUnassigned, cancellationToken,
                RevalidateCallbackAdmissionAsync);
            await transaction.CommitAsync(cancellationToken);
        }
        // Protected claim precedes SQL publication. SQL failure cannot give another owner access;
        // the connection remains exclusively claimed and the same owner can explicitly retry.
        await ChangeAsync(before?.Generation ?? Guid.Empty, selection =>
        {
            selection.ProviderId = ProviderId;
            selection.ConnectionId = connectionId;
            selection.ModelId = ConnectionSelectionPolicy.ModelForSelection(before?.ConnectionId, before?.ModelId, connectionId, model);
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
        SocialTelemetry.Application.Features.AiConnection.SelectModel.Scenario.ValidateInput(model);
        var before = await ReadAsync(cancellationToken);
        if (before is not { Enabled: true, ConnectionId: { } id }) throw new AiProviderException(AiFailure.NotConnected);
        var catalog = await connection.GetOwnedModelsAsync(operation.RequireOwner(), id, before.ModelId, cancellationToken);
        if (!SocialTelemetry.Application.Features.AiConnection.SelectModel.Scenario.ShouldChange(
                model, before.ModelId, catalog.Models.Select(candidate => candidate.Id))) return;
        await ChangeAsync(before.Generation, selection => selection.ModelId = model, cancellationToken);
    }

    public async Task BeginAsync(Guid? connectionId, bool newAccount, bool requestConsent, CancellationToken cancellationToken)
    {
        await connection.AuthorizationGate.WaitAsync(cancellationToken);
        try
        {
            await connection.EnsureCanBeginAsync(cancellationToken);
            var before = await ReadAsync(cancellationToken);
            var selectedId = ConnectionSelectionPolicy.ConnectionForBegin(connectionId, newAccount, before?.ConnectionId);
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
        var callback = await connection.TakeBoundCallbackAsync(state, code, clientId, error, cancellationToken);
        var attempt = callback.Attempt;
        var binding = attempt.OwnerBinding ?? throw new AiProviderException(AiFailure.InvalidCallback);
        await (profileResolver ?? throw new LocalAccessException(LocalAccessFailure.Required))
            .RestoreAsync(binding, attempt.OwnerLifetime, cancellationToken);
        var generation = attempt.SelectionGeneration ?? throw new AiProviderException(AiFailure.InvalidCallback);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attempt.OwnerLifetime, attempt.SelectionLifetime);
        cancellationToken = lifetime.Token;
        await RegisterCallbackAsync(callback, generation, cancellationToken);
        // Neither SQL protection nor the credential file lock spans provider/identity calls.
        await RevalidateCallbackAdmissionAsync(cancellationToken);
        var identity = await connection.ExchangeBoundCallbackAsync(callback, cancellationToken);
        ChatGptConnection.PublishedCallback? published = null;
        ProfileAiSelection completed;
        try
        {
            completed = await ChangeCoreAsync(generation, async selection =>
            {
                // LockAnalysisContext has revalidated the original access under the selector.
                // File publication and SQL selection share this short authorized commit boundary.
                published = await connection.PublishBoundCallbackAsync(callback, identity, RevalidateCallbackAdmissionAsync, cancellationToken);
                var id = attempt.ConnectionId;
                selection.ModelId = ConnectionSelectionPolicy.ModelAfterCallback(selection.ConnectionId, selection.ModelId, id);
                selection.ProviderId = ProviderId;
                selection.ConnectionId = id;
                selection.Enabled = true;
            }, cancellationToken);
        }
        finally
        {
            if (published is not null) connection.RetireCallbackSession(published);
        }
        return (published ?? throw new AiProviderException(AiFailure.InvalidCallback)).Status with { SelectedModel = completed.ModelId };
    }

    private async Task RegisterCallbackAsync(ChatGptConnection.BoundCallback callback, Guid generation, CancellationToken cancellationToken)
    {
        await lifetimes.Changes.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var owner = operation.RequireOwner();
            await database.LockAnalysisContextAsync(owner, cancellationToken);
            var current = await database.Set<ProfileAiSelection>().AsNoTracking()
                .SingleOrDefaultAsync(selection => selection.UserProfileId == owner, cancellationToken);
            if (current?.Generation != generation) throw new AiProviderException(AiFailure.ExecutionInvalidated);
            await connection.RegisterBoundCallbackAsync(callback, RevalidateCallbackAdmissionAsync, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { lifetimes.Changes.Release(); }
    }

    private async Task RevalidateCallbackAdmissionAsync(CancellationToken cancellationToken)
    {
        using var admission = await operation.EnterAsync(cancellationToken);
    }

    public async Task<ChatGptDisconnectResult> DisconnectAsync(CancellationToken cancellationToken)
    {
        var before = await ReadAsync(cancellationToken);
        await ChangeAsync(before?.Generation ?? Guid.Empty, selection => selection.Enabled = false, cancellationToken);
        // SQL disable/revocation generation is durable before file/network work. File failure
        // cannot re-enable inference, including after a host restart.
        return await connection.DisconnectOwnedAsync(operation.RequireOwner(), before?.ConnectionId, cancellationToken);
    }

    private Task<ProfileAiSelection> ChangeAsync(Guid expectedGeneration, Action<ProfileAiSelection> change, CancellationToken cancellationToken) =>
        ChangeCoreAsync(expectedGeneration, selection => { change(selection); return Task.CompletedTask; }, cancellationToken);

    private async Task<ProfileAiSelection> ChangeCoreAsync(Guid expectedGeneration, Func<ProfileAiSelection, Task> change, CancellationToken cancellationToken)
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
            await change(selection);
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
