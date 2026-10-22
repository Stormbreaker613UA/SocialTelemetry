using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Features.LocalAccess;

namespace SocialTelemetry.Infrastructure.Identity;

public static class LocalAccessRegistration
{
    public const string Scheme = "LocalDevice";
    public const string DeviceClaim = "local-device-proof";

    public static IServiceCollection AddLocalAccess(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LocalAccessOptions>().BindConfiguration("LocalAccess", binder => binder.ErrorOnUnknownConfiguration = true)
            .ValidateDataAnnotations().Validate(options => Path.IsPathFullyQualified(options.DataDirectory), "Local access requires an absolute key location.").ValidateOnStart();
        services.AddOptions<IdentityOptions>().Configure<Microsoft.Extensions.Options.IOptions<LocalAccessOptions>>((identity, policy) =>
        {
            identity.Password.RequiredLength = policy.Value.MinimumPasswordLength;
            identity.Lockout.MaxFailedAccessAttempts = policy.Value.FailedAttempts;
            identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(policy.Value.LockoutMinutes);
            identity.Tokens.PasswordResetTokenProvider = "LocalPasswordReset";
        });
        services.AddTransient<DataProtectorTokenProvider<ApplicationUser>>();
        services.Configure<IdentityOptions>(identity => identity.Tokens.ProviderMap["LocalPasswordReset"] =
            new TokenProviderDescriptor(typeof(DataProtectorTokenProvider<ApplicationUser>)));
        services.AddSingleton<LocalAccessRuntime>();
        services.AddScoped<LocalAccessRequest>();
        services.AddScoped<LocalAccessValidation>();
        services.AddScoped<LocalAccessManagement>();
        services.AddScoped<ILocalAccessManagement>(services => services.GetRequiredService<LocalAccessManagement>());
        services.AddSingleton<LocalAccessKeyRing>();
        services.Replace(ServiceDescriptor.Singleton<IDataProtectionProvider>(services => services.GetRequiredService<LocalAccessKeyRing>()));
        services.AddSingleton<LocalDeviceTicketStore>();
        services.AddAuthentication().AddCookie(Scheme, cookie =>
        {
            cookie.Cookie.Name = "SocialTelemetry.LocalDevice";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
            cookie.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
            cookie.SlidingExpiration = false;
            cookie.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            cookie.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        services.AddOptions<CookieAuthenticationOptions>(Scheme).Configure<LocalDeviceTicketStore, LocalAccessKeyRing>((cookie, store, keys) =>
        {
            cookie.SessionStore = store;
            cookie.TicketDataFormat = new TicketDataFormat(keys.CreateProtector("LocalDeviceCookie.v1"));
        });
        return services;
    }
}
