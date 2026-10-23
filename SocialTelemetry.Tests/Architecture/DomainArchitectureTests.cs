using System.Reflection;
using System.Xml.Linq;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Domain.Users;

namespace SocialTelemetry.Tests.Architecture;

public sealed class DomainArchitectureTests
{
    [Fact]
    public void Existing_entities_share_a_separate_BCL_only_domain_assembly()
    {
        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Architecture", "SocialTelemetry.Domain.csproj"));
        Assert.DoesNotContain(project.Descendants(), element =>
            element.Name.LocalName is "PackageReference" or "ProjectReference" or "FrameworkReference");

        var domainAssembly = typeof(UserProfile).Assembly;
        var apiAssembly = typeof(Program).Assembly;
        Assert.Equal("SocialTelemetry.Domain", domainAssembly.GetName().Name);
        Assert.NotSame(apiAssembly, domainAssembly);
        Type[] representativeTypes =
        [
            typeof(PersonGroup), typeof(PersonGroupMembership), typeof(Person), typeof(PersonFact), typeof(PersonInference), typeof(SuggestedProfileUpdate),
            typeof(Interaction), typeof(InteractionParticipant), typeof(InteractionAttachment),
            typeof(AttachmentTranscript), typeof(InteractionAnalysis), typeof(AttachmentType)
        ];
        Assert.All(representativeTypes, entity =>
        {
            Assert.Same(domainAssembly, entity.Assembly);
            Assert.Null(apiAssembly.GetType(entity.FullName!));
        });

        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        Assert.All(domainAssembly.GetReferencedAssemblies(), reference =>
        {
            var dependency = Assembly.Load(reference);
            Assert.NotSame(apiAssembly, dependency);
            // Resolve the compiled identity: a System.* name alone does not prove BCL ownership.
            Assert.Equal(runtimeDirectory, Path.GetDirectoryName(dependency.Location));
        });
    }
}
