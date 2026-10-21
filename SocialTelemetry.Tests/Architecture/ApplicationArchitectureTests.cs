using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Contracts.Transcription;
using SocialTelemetry.Application.Contracts.AI;
using System.Reflection;
using System.Xml.Linq;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Domain.Users;
using SocialTelemetry.Infrastructure.AI;
using SocialTelemetry.Application.Contracts.AI.Models;
using SocialTelemetry.Infrastructure.Storage;
using SocialTelemetry.Infrastructure.Transcription;

namespace SocialTelemetry.Tests.Architecture;

public sealed class ApplicationArchitectureTests
{
    private static readonly Assembly ApplicationAssembly = typeof(IAiClient).Assembly;
    private static readonly Assembly DomainAssembly = typeof(UserProfile).Assembly;
    private const BindingFlags PublicDeclaredMembers = BindingFlags.Public | BindingFlags.Instance |
                                                        BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void Existing_contracts_belong_to_Application_with_only_Domain_and_BCL_dependencies()
    {
        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Architecture", "SocialTelemetry.Application.csproj"));
        Assert.DoesNotContain(project.Descendants(), element =>
            element.Name.LocalName is "PackageReference" or "FrameworkReference");
        var reference = Assert.Single(project.Descendants(), element => element.Name.LocalName == "ProjectReference");
        Assert.Equal("../SocialTelemetry.Domain/SocialTelemetry.Domain.csproj", reference.Attribute("Include")?.Value.Replace('\\', '/'));

        var apiAssembly = typeof(Program).Assembly;
        Assert.Equal("SocialTelemetry.Application", ApplicationAssembly.GetName().Name);
        Assert.NotSame(apiAssembly, ApplicationAssembly);
        Assert.NotSame(DomainAssembly, ApplicationAssembly);
        Type[] representativeTypes =
        [
            typeof(IAiClient), typeof(IAiExecutionLease), typeof(AiSelectedModel), typeof(AiExecutionBinding),
            typeof(AiModelCapabilities), typeof(AiCapabilitySupport),
            typeof(AiTextRequest), typeof(AiTextResponse), typeof(InteractionAnalysisContext),
            typeof(InteractionAnalysisResult), typeof(SuggestedProfileUpdate), typeof(ISpeechToTextClient),
            typeof(SpeechToTextResult), typeof(IAttachmentStorage), typeof(IFileStorage),
            typeof(NotFoundException), typeof(ConflictException)
        ];
        Assert.All(representativeTypes, contract => Assert.Same(ApplicationAssembly, contract.Assembly));
        Assert.All(ApplicationAssembly.GetExportedTypes(), contract => Assert.Null(apiAssembly.GetType(contract.FullName!)));
        Assert.All(ApplicationAssembly.GetReferencedAssemblies(), identity => AssertAllowedAssembly(Assembly.Load(identity)));
    }

    [Fact]
    public void Public_contract_type_graph_contains_only_Application_Domain_and_BCL_types()
    {
        var inspectedTypes = new HashSet<Type>();
        foreach (var contract in ApplicationAssembly.GetExportedTypes())
            InspectContractType(contract, inspectedTypes);
    }

    private static void AssertAllowedAssembly(Assembly assembly)
    {
        if (assembly == ApplicationAssembly || assembly == DomainAssembly)
            return;

        // Resolve actual assemblies, not legacy namespaces or System.* name prefixes.
        Assert.Equal(Path.GetDirectoryName(typeof(object).Assembly.Location), Path.GetDirectoryName(assembly.Location));
    }

    private static void InspectContractType(Type type, HashSet<Type> inspectedTypes)
    {
        if (!inspectedTypes.Add(type))
            return;

        AssertAllowedAssembly(type.Assembly);
        if (type.GetElementType() is { } elementType)
            InspectContractType(elementType, inspectedTypes);
        foreach (var argument in type.GetGenericArguments())
            InspectContractType(argument, inspectedTypes);
        if (type.IsGenericParameter)
        {
            foreach (var constraint in type.GetGenericParameterConstraints())
                InspectContractType(constraint, inspectedTypes);
            return;
        }

        if (type.Assembly != ApplicationAssembly && type.Assembly != DomainAssembly)
            return;

        InspectAttributes(type.GetCustomAttributesData());
        if (type.BaseType is { } baseType)
            InspectContractType(baseType, inspectedTypes);
        foreach (var implementedInterface in type.GetInterfaces())
            InspectContractType(implementedInterface, inspectedTypes);
        foreach (var method in type.GetMethods(PublicDeclaredMembers))
        {
            InspectAttributes(method.GetCustomAttributesData());
            InspectContractType(method.ReturnType, inspectedTypes);
            InspectAttributes(method.ReturnParameter.GetCustomAttributesData());
            InspectParameters(method.GetParameters(), inspectedTypes);
            foreach (var argument in method.GetGenericArguments())
                InspectContractType(argument, inspectedTypes);
        }
        foreach (var constructor in type.GetConstructors(PublicDeclaredMembers))
        {
            InspectAttributes(constructor.GetCustomAttributesData());
            InspectParameters(constructor.GetParameters(), inspectedTypes);
        }
        foreach (var property in type.GetProperties(PublicDeclaredMembers))
        {
            InspectAttributes(property.GetCustomAttributesData());
            InspectContractType(property.PropertyType, inspectedTypes);
            InspectParameters(property.GetIndexParameters(), inspectedTypes);
        }
        foreach (var field in type.GetFields(PublicDeclaredMembers))
        {
            InspectAttributes(field.GetCustomAttributesData());
            InspectContractType(field.FieldType, inspectedTypes);
        }
        foreach (var declaredEvent in type.GetEvents(PublicDeclaredMembers))
        {
            InspectAttributes(declaredEvent.GetCustomAttributesData());
            if (declaredEvent.EventHandlerType is { } handlerType)
                InspectContractType(handlerType, inspectedTypes);
        }
    }

    private static void InspectParameters(IEnumerable<ParameterInfo> parameters, HashSet<Type> inspectedTypes)
    {
        foreach (var parameter in parameters)
        {
            InspectAttributes(parameter.GetCustomAttributesData());
            InspectContractType(parameter.ParameterType, inspectedTypes);
        }
    }

    private static void InspectAttributes(IEnumerable<CustomAttributeData> attributes)
    {
        foreach (var attribute in attributes)
            AssertAllowedAssembly(attribute.AttributeType.Assembly);
    }
}
