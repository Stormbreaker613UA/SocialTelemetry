using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Tests.Architecture;

public sealed class JournalFeatureArchitectureTests
{
    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(IAiClient).Assembly;
    private static readonly Assembly DomainAssembly = typeof(Person).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(AppDbContext).Assembly;
    private static readonly HashSet<string?> FrameworkDirectories =
    [
        Path.GetDirectoryName(typeof(object).Assembly.Location),
        Path.GetDirectoryName(typeof(HttpContext).Assembly.Location)
    ];
    private static readonly HashSet<Assembly> TransportAssemblies =
    [
        typeof(FastEndpoints.Endpoint<>).Assembly,
        typeof(FastEndpoints.IServiceResolverBase).Assembly,
        typeof(FastEndpoints.Http).Assembly,
        typeof(FastEndpoints.Void).Assembly,
        typeof(FluentValidation.ValidationException).Assembly
    ];
    private static readonly Dictionary<short, OpCode> Instructions = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(instruction => instruction.Value);
    private const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void Api_has_no_runtime_EF_reference_or_direct_EF_package_except_private_startup_tooling()
    {
        Assert.DoesNotContain(ApiAssembly.GetReferencedAssemblies(), identity =>
            Assembly.Load(identity) == typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly ||
            Assembly.Load(identity) == typeof(Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions).Assembly);
        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Architecture", "SocialTelemetry.Api.csproj"));
        var packages = project.Descendants().Where(element => element.Name.LocalName == "PackageReference").ToArray();
        Assert.Equal(new[] { "FastEndpoints", "FastEndpoints.Swagger", "Microsoft.AspNetCore.OpenApi",
                "Microsoft.EntityFrameworkCore.Design", "Serilog.AspNetCore" },
            packages.Select(element => element.Attribute("Include")?.Value).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "../SocialTelemetry.Application/SocialTelemetry.Application.csproj",
                "../SocialTelemetry.Domain/SocialTelemetry.Domain.csproj", "../SocialTelemetry.Infrastructure/SocialTelemetry.Infrastructure.csproj" },
            project.Descendants().Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => element.Attribute("Include")?.Value.Replace('\\', '/')).Order(StringComparer.Ordinal));
        var design = Assert.Single(packages, element => element.Attribute("Include")?.Value == "Microsoft.EntityFrameworkCore.Design");
        Assert.Equal("all", design.Elements().Single(element => element.Name.LocalName == "PrivateAssets").Value);
        Assert.Null(ApiAssembly.GetType("SocialTelemetry.Api.Infrastructure.AI.AiContextBuilder"));
        Assert.Null(ApiAssembly.GetType("SocialTelemetry.Api.Features.Analysis.AnalyzeInteraction.AnalysisContract"));
    }

    [Fact]
    public void Migrated_HTTP_features_cannot_reach_concrete_mechanisms_or_service_location()
    {
        var migratedTypes = ApiAssembly.GetTypes().Where(IsMigratedType).ToArray();
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.People.Create.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.PersonFacts.Add.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.UserProfile.Update.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.Interactions.PutTranscript.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.Interactions.TranscribeAttachment.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.AiConnection.RequireLocalAiRequestAttribute));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.LocalProfiles.Get.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.AiConnection.Callback.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.AiConnection.LocalAiConnectionMiddleware));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Common.Profiles.LocalProfileMiddleware));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.Analysis.AnalyzeInteraction.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.LocalAccess.SignIn.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.LocalAccess.Recover.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Common.Profiles.LocalAccessHttpMiddleware));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Common.Exceptions.GlobalExceptionHandler));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Common.Exceptions.FailurePresentation));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Common.Result<>));
        Assert.Equal(typeof(Attribute), typeof(Api.Features.AiConnection.RequireLocalAiRequestAttribute).BaseType);
        Assert.Contains(migratedTypes, type => type.IsNested && type.Name.Contains("HandleAsync", StringComparison.Ordinal));
        foreach (var type in migratedTypes)
            InspectType(type);
    }

    [Theory]
    [InlineData(nameof(ForbiddenPersistenceCall))]
    [InlineData(nameof(ForbiddenServiceLocationCall))]
    [InlineData(nameof(ForbiddenReflectionCall))]
    [InlineData(nameof(ForbiddenTypeLookup))]
    public void Guard_rejects_real_forbidden_member_calls(string methodName)
    {
        var method = typeof(JournalFeatureArchitectureTests).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => InspectBody(method));
    }

    private static Guid ForbiddenPersistenceCall(AppDbContext database) => database.ContextId.InstanceId;
    private static object? ForbiddenServiceLocationCall(IServiceProvider services, Type type) => services.GetService(type);
    private static object? ForbiddenReflectionCall(ConstructorInfo constructor) => constructor.Invoke([]);
    private static MethodInfo? ForbiddenTypeLookup(Type type) => type.GetMethod("ToString");
    private static string ExceptionTypeIdentity(Exception exception) => exception.GetType().Name;

    [Fact]
    public void Guard_accepts_exception_identity_metadata_without_reflection_lookup_or_invocation()
    {
        var method = typeof(JournalFeatureArchitectureTests).GetMethod(nameof(ExceptionTypeIdentity), BindingFlags.NonPublic | BindingFlags.Static)!;
        InspectBody(method);
    }

    private static bool IsMigratedType(Type type) => type.Namespace is { } name &&
        (name == "SocialTelemetry.Api.Features" || name.StartsWith("SocialTelemetry.Api.Features.", StringComparison.Ordinal) ||
         name == "SocialTelemetry.Api.Common" || name.StartsWith("SocialTelemetry.Api.Common.", StringComparison.Ordinal));

    private static void InspectType(Type type)
    {
        if (type.BaseType is { } baseType) AssertAllowedType(baseType);
        // Framework-owned base interfaces are not declarations by the HTTP slice.
        // Calls into them are still checked through actual IL member tokens below.
        var inheritedInterfaces = type.BaseType?.GetInterfaces() ?? [];
        foreach (var implementedInterface in type.GetInterfaces().Except(inheritedInterfaces))
            AssertAllowedType(implementedInterface);
        foreach (var field in type.GetFields(DeclaredMembers)) AssertAllowedType(field.FieldType);
        foreach (var property in type.GetProperties(DeclaredMembers)) AssertAllowedType(property.PropertyType);
        foreach (var attribute in type.GetCustomAttributesData()) AssertAllowedType(attribute.AttributeType);
        foreach (var method in type.GetMethods(DeclaredMembers).Cast<MethodBase>().Concat(type.GetConstructors(DeclaredMembers)))
        {
            if (method is MethodInfo methodInfo) AssertAllowedType(methodInfo.ReturnType);
            foreach (var parameter in method.GetParameters()) AssertAllowedType(parameter.ParameterType);
            foreach (var attribute in method.GetCustomAttributesData()) AssertAllowedType(attribute.AttributeType);
            try { InspectBody(method); }
            catch (Xunit.Sdk.XunitException exception)
            {
                Assert.Fail($"Dependency in {method.DeclaringType}.{method.Name}: {exception.Message}");
            }
        }
    }

    private static void AssertAllowedType(Type type)
    {
        if (type.HasElementType) AssertAllowedType(type.GetElementType()!);
        foreach (var argument in type.GetGenericArguments()) AssertAllowedType(argument);
        if (type.IsGenericParameter) return;
        var assembly = type.Assembly;
        Assert.NotSame(InfrastructureAssembly, assembly);
        if (assembly == ApiAssembly)
            Assert.True(IsMigratedType(type), $"Migrated feature depends on legacy Api type {type}.");
        else
            Assert.True(assembly == ApplicationAssembly || assembly == DomainAssembly ||
                        FrameworkDirectories.Contains(Path.GetDirectoryName(assembly.Location)) ||
                        TransportAssemblies.Contains(assembly), $"Unexpected feature dependency: {type} ({assembly.GetName().Name}).");
    }

    private static void InspectBody(MethodBase method)
    {
        var body = method.GetMethodBody();
        if (body is null) return;
        foreach (var variable in body.LocalVariables) AssertAllowedType(variable.LocalType);
        foreach (var clause in body.ExceptionHandlingClauses)
            if (clause.Flags == ExceptionHandlingClauseOptions.Clause && clause.CatchType is { } exceptionType)
                AssertAllowedType(exceptionType);
        var bytes = body.GetILAsByteArray()!;
        var position = 0;
        while (position < bytes.Length)
        {
            var first = bytes[position++];
            var instruction = Instructions[first == 0xfe ? (short)(0xfe00 | bytes[position++]) : first];
            if (instruction.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                var member = method.Module.ResolveMember(BitConverter.ToInt32(bytes, position),
                    method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null)!;
                if (member is Type referencedType) AssertAllowedType(referencedType);
                else
                {
                    if (member.DeclaringType is { } declaringType) AssertAllowedType(declaringType);
                    Assert.False(IsServiceLocationOrReflection(member), $"Forbidden bypass in {method}: {member}.");
                    if (member is MethodInfo calledMethod)
                    {
                        AssertAllowedType(calledMethod.ReturnType);
                        foreach (var argument in calledMethod.GetGenericArguments()) AssertAllowedType(argument);
                    }
                    if (member is MethodBase called)
                        foreach (var parameter in called.GetParameters()) AssertAllowedType(parameter.ParameterType);
                    if (member is FieldInfo field) AssertAllowedType(field.FieldType);
                }
            }
            position += instruction.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, position),
                _ => 4
            };
        }
    }

    private static bool IsServiceLocationOrReflection(MemberInfo member)
    {
        // Name describes an already obtained exception type for the existing privacy-safe log.
        // It neither discovers a type/member nor constructs, invokes or resolves anything.
        if (member.DeclaringType == typeof(MemberInfo) && member.Name == "get_Name") return false;
        return member.DeclaringType == typeof(IServiceProvider) ||
        member.DeclaringType == typeof(Activator) ||
        // typeof/record equality inspect identity only; ldtoken's target is checked above.
        (member.DeclaringType == typeof(Type) &&
         member.Name is not ("GetTypeFromHandle" or "op_Equality" or "op_Inequality")) ||
        member.DeclaringType?.Namespace == "System.Reflection" ||
        member.DeclaringType?.Namespace == "Microsoft.Extensions.DependencyInjection" ||
        (member.Name is "Resolve" or "TryResolve" or "CreateScope" &&
         member.DeclaringType is { } type && TransportAssemblies.Contains(type.Assembly));
    }
}
