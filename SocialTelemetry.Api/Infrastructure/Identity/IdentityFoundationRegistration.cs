using Microsoft.AspNetCore.Identity;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Infrastructure.Identity;

public static class IdentityFoundationRegistration
{
    public static IServiceCollection AddIdentityFoundation(this IServiceCollection services)
    {
        // Account stores only: authentication schemes and password/session endpoints come later.
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version1)
            .AddEntityFrameworkStores<AppDbContext>();
        services.AddOpenIddict().AddCore(options => options.UseEntityFrameworkCore()
            .UseDbContext<AppDbContext>().ReplaceDefaultEntities<Guid>());
        return services;
    }
}
