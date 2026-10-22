using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Infrastructure.Identity;

// Cookie IO runs before admission/after its commit in a separate framework scope; it never
// opens a second context inside a protected application operation or reads journal data.
public sealed class LocalDeviceTicketStore(IServiceScopeFactory scopes, IDataProtectionProvider protection) : ITicketStore
{
    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var token = ticket.Principal.FindFirst(LocalAccessRegistration.DeviceClaim)?.Value
            ?? throw new InvalidOperationException("Missing device evidence.");
        await RenewAsync(token, ticket);
        return token;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = LocalAccessValidation.TokenHash(key);
        var row = await database.Set<LocalDeviceTicket>().SingleOrDefaultAsync(device => device.TokenHash == hash)
            ?? throw new InvalidOperationException("The device session is unavailable.");
        row.ProtectedTicket = protection.CreateProtector("LocalDeviceTicket.v1").Protect(TicketSerializer.Default.Serialize(ticket));
        await database.SaveChangesAsync();
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (key.Length != 64) return null;
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = LocalAccessValidation.TokenHash(key);
        var row = await database.Set<LocalDeviceTicket>().AsNoTracking().SingleOrDefaultAsync(device => device.TokenHash == hash);
        if (row?.ProtectedTicket is null) return null;
        try { return TicketSerializer.Default.Deserialize(protection.CreateProtector("LocalDeviceTicket.v1").Unprotect(row.ProtectedTicket)); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public async Task RemoveAsync(string key)
    {
        // Removing/expiring a transport ticket cannot revoke deliberately remembered grants
        // for other profiles. Explicit lock/logout performs the scoped durable revocation.
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = LocalAccessValidation.TokenHash(key);
        await database.Set<LocalDeviceTicket>().Where(device => device.TokenHash == hash)
            .ExecuteUpdateAsync(update => update.SetProperty(device => device.ProtectedTicket, (byte[]?)null));
    }
}
