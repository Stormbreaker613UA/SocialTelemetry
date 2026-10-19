using System.Reflection;
using System.Xml.Linq;
using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Domain.Users;
using SocialTelemetry.Infrastructure.Identity;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Transcription;

namespace SocialTelemetry.Tests.Architecture;

public sealed class InfrastructureArchitectureTests
{
    [Fact]
    public void Infrastructure_owns_mechanisms_without_an_Api_reverse_reference()
    {
        var infrastructure = typeof(AppDbContext).Assembly;
        var api = typeof(Program).Assembly;
        Assert.NotSame(api, infrastructure);
        Assert.Equal("SocialTelemetry.Infrastructure", infrastructure.GetName().Name);
        Assert.Same(infrastructure, typeof(ApplicationUser).Assembly);
        Assert.Same(infrastructure, typeof(TranscriptionProcessRunner).Assembly);
        Assert.Same(infrastructure, typeof(AppDbContextFactory).Assembly);
        Assert.Same(infrastructure, typeof(ProviderMigrationsAssembly).Assembly);
        Assert.All(infrastructure.GetReferencedAssemblies(), identity => Assert.NotSame(api, Assembly.Load(identity)));
        var productReferences = infrastructure.GetReferencedAssemblies().Select(Assembly.Load)
            .Where(assembly => assembly == typeof(IAiClient).Assembly || assembly == typeof(UserProfile).Assembly).ToArray();
        Assert.Contains(typeof(IAiClient).Assembly, productReferences);
        Assert.Contains(typeof(UserProfile).Assembly, productReferences);

        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Architecture", "SocialTelemetry.Infrastructure.csproj"));
        var references = project.Descendants().Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")!.Value.Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "../SocialTelemetry.Application/SocialTelemetry.Application.csproj",
            "../SocialTelemetry.Domain/SocialTelemetry.Domain.csproj"
        }, references);
    }
}
