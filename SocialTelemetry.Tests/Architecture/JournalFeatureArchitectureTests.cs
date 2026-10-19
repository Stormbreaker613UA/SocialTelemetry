using System.Reflection;
using System.Reflection.Emit;
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
    public void Migrated_HTTP_features_cannot_reach_concrete_mechanisms_or_service_location()
    {
        var migratedTypes = ApiAssembly.GetTypes().Where(IsMigratedType).ToArray();
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.People.Create.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.PersonFacts.Add.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.UserProfile.Update.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.Interactions.PutTranscript.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.Interactions.TranscribeAttachment.Endpoint));
        Assert.Contains(migratedTypes, type => type == typeof(Api.Features.AiConnection.RequireLocalAiRequestAttribute));
        Assert.Equal(typeof(Attribute), typeof(Api.Features.AiConnection.RequireLocalAiRequestAttribute).BaseType);
        Assert.Contains(migratedTypes, type => type.IsNested && type.Name.Contains("HandleAsync", StringComparison.Ordinal));
        foreach (var type in migratedTypes)
            InspectType(type);
    }

    [Theory]
    [InlineData(nameof(ForbiddenPersistenceCall))]
    [InlineData(nameof(ForbiddenServiceLocationCall))]
    [InlineData(nameof(ForbiddenReflectionCall))]
    public void Guard_rejects_real_forbidden_member_calls(string methodName)
    {
        var method = typeof(JournalFeatureArchitectureTests).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => InspectBody(method));
    }

    private static Guid ForbiddenPersistenceCall(AppDbContext database) => database.ContextId.InstanceId;
    private static object? ForbiddenServiceLocationCall(IServiceProvider services, Type type) => services.GetService(type);
    private static object? ForbiddenReflectionCall(ConstructorInfo constructor) => constructor.Invoke([]);

    private static bool IsMigratedType(Type type) =>
        // This existing shared HTTP marker is inspected too, not exempted from dependency checks.
        type == typeof(Api.Features.AiConnection.RequireLocalAiRequestAttribute) ||
        type.Namespace is { } name && new[] { "People", "PersonFacts", "UserProfile", "ProfileConnections", "Interactions" }
            .Any(feature => name == $"SocialTelemetry.Api.Features.{feature}" ||
                            name.StartsWith($"SocialTelemetry.Api.Features.{feature}.", StringComparison.Ordinal));

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
            InspectBody(method);
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

    private static bool IsServiceLocationOrReflection(MemberInfo member) =>
        member.DeclaringType == typeof(IServiceProvider) ||
        member.DeclaringType == typeof(Activator) ||
        // typeof/record equality inspect identity only; ldtoken's target is checked above.
        (member.DeclaringType == typeof(Type) &&
         member.Name is not ("GetTypeFromHandle" or "op_Equality" or "op_Inequality")) ||
        member.DeclaringType?.Namespace == "System.Reflection" ||
        member.DeclaringType?.Namespace == "Microsoft.Extensions.DependencyInjection" ||
        (member.Name is "Resolve" or "TryResolve" or "CreateScope" &&
         member.DeclaringType is { } type && TransportAssemblies.Contains(type.Assembly));
}
