namespace SocialTelemetry.Application.Features.AiConnection;

public static class ConnectionSelectionPolicy
{
    public static string? ModelForSelection(Guid? currentConnection, string? currentModel,
        Guid nextConnection, string? adoptedModel) => currentConnection == nextConnection ? currentModel : adoptedModel;

    public static Guid? ConnectionForBegin(Guid? requestedConnection, bool newAccount, Guid? selectedConnection)
    {
        if (newAccount) return null;
        return requestedConnection ?? selectedConnection;
    }

    public static string? ModelAfterCallback(Guid? currentConnection, string? currentModel, Guid completedConnection) =>
        currentConnection == completedConnection ? currentModel : null;
}
