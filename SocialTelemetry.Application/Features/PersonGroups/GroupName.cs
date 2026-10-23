using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Application.Features.PersonGroups;

internal static class GroupName
{
    internal static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new FeatureValidationException("Name is required.");
        var normalized = name.Trim();
        if (normalized.Length > PersonGroup.MaximumNameLength)
            throw new FeatureValidationException($"Name cannot exceed {PersonGroup.MaximumNameLength} characters.");
        return normalized;
    }
}
