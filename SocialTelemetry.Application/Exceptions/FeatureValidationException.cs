namespace SocialTelemetry.Application.Exceptions;

public sealed class FeatureValidationException(string detail) : Exception(detail);
