using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Application.Features.Interactions.GetAttachment;

public sealed class Scenario(IAttachmentPersistence persistence, LocalProfileOperation operation)
{
    public async Task<Result> ExecuteAsync(Guid interactionId, Guid attachmentId, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        cancellationToken = admission.CancellationToken;
        var attachment = await persistence.FindAttachmentAsync(ownerId, interactionId, attachmentId, true, cancellationToken)
            ?? throw new NotFoundException();
        return new Result(attachment.Id, attachment.InteractionId, attachment.Type, attachment.Status, attachment.MimeType,
            attachment.Type == AttachmentType.Text ? attachment.TextContent : null, attachment.CreatedAt);
    }
}

public sealed record Result(Guid Id, Guid InteractionId, AttachmentType Type, AttachmentStatus Status,
    string? MimeType, string? TextContent, DateTimeOffset CreatedAt);
