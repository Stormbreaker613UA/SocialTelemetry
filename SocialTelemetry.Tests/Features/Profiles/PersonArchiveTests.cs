using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Tests.Features.People;
using GetPerson = SocialTelemetry.Api.Features.People.GetById;
using GetPeople = SocialTelemetry.Api.Features.People.GetAll;

namespace SocialTelemetry.Tests.Features.Profiles;

public abstract class PersonArchiveTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture> where TFixture : PeopleApiFixture
{
    [Fact]
    public async Task Archive_is_idempotent_preserves_profile_and_history_and_filtering_is_explicit()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var profileId = await fixture.CreateUserProfileAsync();
        var personId = await fixture.CreatePersonAsync(new() { UserProfileId = profileId, DisplayName = "Synthetic" });
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var interaction = new Interaction
        {
            Id = Guid.NewGuid(), UserProfileId = profileId, Title = "Synthetic", Description = "History",
            Participants = [new InteractionParticipant { PersonId = personId }],
            Analyses = [new InteractionAnalysis { Id = Guid.NewGuid(), Summary = "Historical" }]
        };
        database.Interactions.Add(interaction);
        database.PersonFacts.Add(new PersonFact { Id = Guid.NewGuid(), PersonId = personId, Value = "Confirmed" });
        database.PersonInferences.Add(new PersonInference { Id = Guid.NewGuid(), PersonId = personId, Value = "Hypothesis", SourceInteractionId = interaction.Id });
        database.PersonExternalConnections.Add(new PersonExternalConnection { Id = Guid.NewGuid(), PersonId = personId, Platform = "custom" });
        await database.SaveChangesAsync(cancellation);

        using var archive = await fixture.Client.PutAsync($"/people/{personId}/archive", null, cancellation);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        var archived = await fixture.Client.GetFromJsonAsync<GetPerson.Response>($"/people/{personId}", cancellation);
        Assert.NotNull(archived);
        Assert.NotNull(archived.ArchivedAt);
        using var repeat = await fixture.Client.PutAsync($"/people/{personId}/archive", null, cancellation);
        var repeated = await fixture.Client.GetFromJsonAsync<GetPerson.Response>($"/people/{personId}", cancellation);
        Assert.Equal(archived.ArchivedAt, repeated?.ArchivedAt);

        var all = await fixture.Client.GetFromJsonAsync<GetPeople.Response>($"/people?UserProfileId={profileId}", cancellation);
        var active = await fixture.Client.GetFromJsonAsync<GetPeople.Response>($"/people?UserProfileId={profileId}&Archived=false", cancellation);
        var archivedList = await fixture.Client.GetFromJsonAsync<GetPeople.Response>($"/people?UserProfileId={profileId}&Archived=true", cancellation);
        Assert.Contains(all!.People, person => person.Id == personId);
        Assert.DoesNotContain(active!.People, person => person.Id == personId);
        Assert.Contains(archivedList!.People, person => person.Id == personId);
        Assert.True(await database.PersonFacts.AnyAsync(fact => fact.PersonId == personId, cancellation));
        Assert.True(await database.PersonInferences.AnyAsync(inference => inference.PersonId == personId, cancellation));
        Assert.True(await database.InteractionParticipants.AnyAsync(participant => participant.PersonId == personId, cancellation));
        Assert.True(await database.InteractionAnalyses.AnyAsync(analysis => analysis.InteractionId == interaction.Id, cancellation));
        Assert.True(await database.PersonExternalConnections.AnyAsync(connection => connection.PersonId == personId, cancellation));

        using var unarchive = await fixture.Client.DeleteAsync($"/people/{personId}/archive", cancellation);
        Assert.Equal(HttpStatusCode.NoContent, unarchive.StatusCode);
        var restored = await fixture.Client.GetFromJsonAsync<GetPerson.Response>($"/people/{personId}", cancellation);
        Assert.Null(restored?.ArchivedAt);
    }

    [Fact]
    public async Task Unknown_person_archive_and_unarchive_return_not_found()
    {
        var route = $"/people/{Guid.NewGuid()}/archive";
        using var archive = await fixture.Client.PutAsync(route, null, TestContext.Current.CancellationToken);
        using var unarchive = await fixture.Client.DeleteAsync(route, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, archive.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unarchive.StatusCode);
    }
}

public sealed class PersonArchiveTests(PeopleApiFixture fixture) : PersonArchiveTestsContract<PeopleApiFixture>(fixture);

[Collection("SQLite")]
public sealed class SqlitePersonArchiveTests(SqliteApiFixture fixture) : PersonArchiveTestsContract<SqliteApiFixture>(fixture);
