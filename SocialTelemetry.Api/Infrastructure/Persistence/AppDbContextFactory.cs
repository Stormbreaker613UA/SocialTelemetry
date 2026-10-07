using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using SocialTelemetry.Api.Infrastructure.Runtime;

namespace SocialTelemetry.Api.Infrastructure.Persistence;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var arguments = new ConfigurationBuilder().AddCommandLine(args).Build();
        var provider = arguments["provider"];
        if (provider is not ("PostgreSql" or "Sqlite"))
            throw new InvalidOperationException("Specify -- --provider PostgreSql or -- --provider Sqlite explicitly.");
        var directory = Directory.GetCurrentDirectory();
        if (!File.Exists(Path.Combine(directory, "SocialTelemetry.Api.csproj")))
            directory = Path.Combine(directory, "SocialTelemetry.Api");
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var configuration = new ConfigurationBuilder().SetBasePath(directory)
            .AddJsonFile("appsettings.json").AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables().AddCommandLine(args).Build();
        var options = configuration.GetSection("Persistence").Get<PersistenceOptions>() ?? new();
        options.Provider = provider;
        var paths = new ApplicationPaths(Options.Create(configuration.GetSection("ApplicationData").Get<ApplicationDataOptions>() ?? new()),
            new DesignEnvironment { ContentRootPath = directory });
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        PersistenceRegistration.Configure(builder, options, paths, configuration.GetConnectionString("Default"));
        return new AppDbContext(builder.Options);
    }

    private sealed class DesignEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "SocialTelemetry.Api";
        public string EnvironmentName { get; set; } = "DesignTime";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
