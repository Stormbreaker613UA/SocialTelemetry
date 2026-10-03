using System.Net;
using System.Net.Http.Json;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Tests.Features.People;
using AddFact = SocialTelemetry.Api.Features.PersonFacts.Add;
using GetFacts = SocialTelemetry.Api.Features.PersonFacts.GetForPerson;
using UpdateFact = SocialTelemetry.Api.Features.PersonFacts.Update;
using CreatePerson = SocialTelemetry.Api.Features.People.Create;

namespace SocialTelemetry.Tests.Features.PersonFacts;

public sealed class PersonFactsTests : IClassFixture<PeopleApiFixture>
{
    private readonly PeopleApiFixture fixture;

    public PersonFactsTests(PeopleApiFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task Add_adds_fact_to_existing_person()
    {
        var personId = await CreatePersonAsync("Morgan");
        var request = new AddFact.Request
        {
            PersonId = personId,
            Value = "Prefers tea",
            Source = "Said so"
        };

        using var response = await fixture.Client.PostAsJsonAsync($"/people/{personId}/facts", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var fact = await response.Content.ReadFromJsonAsync<AddFact.Response>();
        Assert.NotNull(fact);
        Assert.Equal(personId, fact.PersonId);
        Assert.Equal("Prefers tea", fact.Value);
    }

    [Fact]
    public async Task Add_returns_not_found_for_missing_person()
    {
        var personId = Guid.NewGuid();
        var request = new AddFact.Request
        {
            PersonId = personId,
            Value = "Prefers tea"
        };

        using var response = await fixture.Client.PostAsJsonAsync($"/people/{personId}/facts", request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_changes_existing_fact()
    {
        var personId = await CreatePersonAsync("Casey");
        var factId = await AddFactAsync(personId, "Old value");
        var request = new UpdateFact.Request
        {
            PersonId = personId,
            Id = factId,
            Value = "Updated value",
            Source = "Confirmed later"
        };

        using var response = await fixture.Client.PutAsJsonAsync($"/people/{personId}/facts/{factId}", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var facts = await GetFactsAsync(personId);
        var fact = Assert.Single(facts);
        Assert.Equal("Updated value", fact.Value);
        Assert.Equal("Confirmed later", fact.Source);
    }

    [Fact]
    public async Task Delete_removes_existing_fact()
    {
        var personId = await CreatePersonAsync("Jordan");
        var factId = await AddFactAsync(personId, "Temporary fact");

        using var response = await fixture.Client.DeleteAsync($"/people/{personId}/facts/{factId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await GetFactsAsync(personId));
    }

    [Fact]
    public async Task Update_returns_not_found_for_missing_fact()
    {
        var personId = await CreatePersonAsync("Taylor");
        var request = new UpdateFact.Request
        {
            PersonId = personId,
            Id = Guid.NewGuid(),
            Value = "Missing fact"
        };

        using var response = await fixture.Client.PutAsJsonAsync($"/people/{personId}/facts/{request.Id}", request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetForPerson_returns_only_facts_for_that_person()
    {
        var firstPersonId = await CreatePersonAsync("Alex");
        var secondPersonId = await CreatePersonAsync("Sam");
        await AddFactAsync(firstPersonId, "First person's fact");

        var firstPersonFacts = await GetFactsAsync(firstPersonId);
        var secondPersonFacts = await GetFactsAsync(secondPersonId);

        var fact = Assert.Single(firstPersonFacts);
        Assert.Equal("First person's fact", fact.Value);
        Assert.Empty(secondPersonFacts);
    }

    private async Task<Guid> CreatePersonAsync(string displayName)
    {
        var userProfileId = await fixture.CreateUserProfileAsync();
        var request = new CreatePerson.Request
        {
            UserProfileId = userProfileId,
            DisplayName = displayName,
            RelationshipContext = RelationshipContext.Acquaintance
        };

        return await fixture.CreatePersonAsync(request);
    }

    private async Task<Guid> AddFactAsync(Guid personId, string value)
    {
        var request = new AddFact.Request
        {
            PersonId = personId,
            Value = value
        };

        using var response = await fixture.Client.PostAsJsonAsync($"/people/{personId}/facts", request);
        response.EnsureSuccessStatusCode();

        var fact = await response.Content.ReadFromJsonAsync<AddFact.Response>();
        return fact!.Id;
    }

    private async Task<IReadOnlyList<GetFacts.PersonFactResponse>> GetFactsAsync(Guid personId)
    {
        using var response = await fixture.Client.GetAsync($"/people/{personId}/facts");
        response.EnsureSuccessStatusCode();

        var facts = await response.Content.ReadFromJsonAsync<GetFacts.Response>();
        return facts!.Facts;
    }
}
