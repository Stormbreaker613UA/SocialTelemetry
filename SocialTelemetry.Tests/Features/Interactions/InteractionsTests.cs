using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Tests.Features.People;
using CreateInteraction = SocialTelemetry.Api.Features.Interactions.Create;
using GetInteraction = SocialTelemetry.Api.Features.Interactions.GetById;
using GetInteractionsForPerson = SocialTelemetry.Api.Features.Interactions.GetForPerson;
using UpdateInteraction = SocialTelemetry.Api.Features.Interactions.Update;
using CreatePerson = SocialTelemetry.Api.Features.People.Create;

namespace SocialTelemetry.Tests.Features.Interactions;

public sealed class InteractionsTests : IClassFixture<PeopleApiFixture>
{
    private readonly PeopleApiFixture fixture;

    public InteractionsTests(PeopleApiFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task Create_with_one_participant_returns_created_interaction()
    {
        var (userProfileId, firstPersonId, _) = await CreatePeopleAsync();
        var request = CreateRequest(userProfileId, "Coffee", [firstPersonId], DateTimeOffset.UtcNow);

        using var response = await fixture.Client.PostAsJsonAsync("/interactions", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var interaction = await response.Content.ReadFromJsonAsync<CreateInteraction.Response>(TestContext.Current.CancellationToken);
        Assert.NotNull(interaction);

        var savedInteraction = await GetInteractionAsync(interaction.Id);
        Assert.Equal("Coffee", savedInteraction.Title);
        Assert.Single(savedInteraction.Participants);
        Assert.Equal(firstPersonId, savedInteraction.Participants[0].Id);
    }

    [Fact]
    public async Task Create_with_multiple_participants_persists_all_participants()
    {
        var (userProfileId, firstPersonId, secondPersonId) = await CreatePeopleAsync();
        var request = CreateRequest(userProfileId, "Dinner", [firstPersonId, secondPersonId], DateTimeOffset.UtcNow);

        using var response = await fixture.Client.PostAsJsonAsync("/interactions", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var interaction = await response.Content.ReadFromJsonAsync<CreateInteraction.Response>(TestContext.Current.CancellationToken);
        Assert.NotNull(interaction);

        var savedInteraction = await GetInteractionAsync(interaction.Id);
        Assert.Collection(
            savedInteraction.Participants,
            participant => Assert.Equal("Alex", participant.DisplayName),
            participant => Assert.Equal("Morgan", participant.DisplayName));
    }

    [Fact]
    public async Task Create_returns_not_found_for_missing_participant()
    {
        var userProfileId = await fixture.CreateUserProfileAsync();
        var request = CreateRequest(userProfileId, "Missing participant", [Guid.NewGuid()], DateTimeOffset.UtcNow);

        using var response = await fixture.Client.PostAsJsonAsync("/interactions", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetById_returns_interaction_participants()
    {
        var (userProfileId, firstPersonId, secondPersonId) = await CreatePeopleAsync();
        var interactionId = await CreateInteractionAsync(
            CreateRequest(userProfileId, "Walk", [firstPersonId, secondPersonId], DateTimeOffset.UtcNow));

        using var response = await fixture.Client.GetAsync($"/interactions/{interactionId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var interaction = await response.Content.ReadFromJsonAsync<GetInteraction.Response>(TestContext.Current.CancellationToken);
        Assert.NotNull(interaction);
        Assert.Equal(interactionId, interaction.Id);
        Assert.Equal(2, interaction.Participants.Count);
    }

    [Fact]
    public async Task GetForPerson_returns_only_interactions_with_that_person()
    {
        var (userProfileId, firstPersonId, secondPersonId) = await CreatePeopleAsync();
        var matchingInteractionId = await CreateInteractionAsync(
            CreateRequest(userProfileId, "Matching", [firstPersonId], DateTimeOffset.UtcNow));
        await CreateInteractionAsync(CreateRequest(userProfileId, "Other", [secondPersonId], DateTimeOffset.UtcNow));

        using var response = await fixture.Client.GetAsync($"/people/{firstPersonId}/interactions", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var interactions = await response.Content.ReadFromJsonAsync<GetInteractionsForPerson.Response>(TestContext.Current.CancellationToken);
        Assert.NotNull(interactions);
        var interaction = Assert.Single(interactions.Interactions);
        Assert.Equal(matchingInteractionId, interaction.Id);
    }

    [Fact]
    public async Task GetForPerson_orders_interactions_newest_first()
    {
        var (userProfileId, firstPersonId, _) = await CreatePeopleAsync();
        var olderInteractionId = await CreateInteractionAsync(
            CreateRequest(userProfileId, "Older", [firstPersonId], new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)));
        var newerInteractionId = await CreateInteractionAsync(
            CreateRequest(userProfileId, "Newer", [firstPersonId], new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero)));

        using var response = await fixture.Client.GetAsync($"/people/{firstPersonId}/interactions", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var interactions = await response.Content.ReadFromJsonAsync<GetInteractionsForPerson.Response>(TestContext.Current.CancellationToken);
        Assert.NotNull(interactions);
        Assert.Collection(
            interactions.Interactions,
            interaction => Assert.Equal(newerInteractionId, interaction.Id),
            interaction => Assert.Equal(olderInteractionId, interaction.Id));
    }

    [Fact]
    public async Task Update_replaces_basic_fields_and_participants()
    {
        var (userProfileId, firstPersonId, secondPersonId) = await CreatePeopleAsync();
        var interactionId = await CreateInteractionAsync(
            CreateRequest(userProfileId, "Original", [firstPersonId], DateTimeOffset.UtcNow));
        var request = new UpdateInteraction.Request
        {
            Id = interactionId,
            Title = "Updated",
            Description = "Updated description",
            UserThoughts = "Updated thoughts",
            OccurredAt = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero),
            ParticipantIds = [secondPersonId]
        };

        using var response = await fixture.Client.PutAsJsonAsync($"/interactions/{interactionId}", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var savedInteraction = await GetInteractionAsync(interactionId);
        Assert.Equal("Updated", savedInteraction.Title);
        Assert.Equal("Updated thoughts", savedInteraction.UserThoughts);
        var participant = Assert.Single(savedInteraction.Participants);
        Assert.Equal(secondPersonId, participant.Id);
    }

    [Fact]
    public async Task Delete_removes_interaction()
    {
        var (userProfileId, firstPersonId, _) = await CreatePeopleAsync();
        var interactionId = await CreateInteractionAsync(
            CreateRequest(userProfileId, "Delete me", [firstPersonId], DateTimeOffset.UtcNow));

        using var deleteResponse = await fixture.Client.DeleteAsync($"/interactions/{interactionId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        using var getResponse = await fixture.Client.GetAsync($"/interactions/{interactionId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_cascades_to_sourced_inferences_and_preserves_unrelated_inferences()
    {
        var (userProfileId, personId, _) = await CreatePeopleAsync();
        var interactionId = await CreateInteractionAsync(
            CreateRequest(userProfileId, "Source", [personId], DateTimeOffset.UtcNow));
        var otherInteractionId = await CreateInteractionAsync(
            CreateRequest(userProfileId, "Other source", [personId], DateTimeOffset.UtcNow));
        Guid?[] sourceIds = [interactionId, interactionId, otherInteractionId, null];
        var inferences = sourceIds.Select(sourceId => new PersonInference
        {
            Id = Guid.NewGuid(),
            PersonId = personId,
            SourceInteractionId = sourceId,
            Value = "Test inference",
            Confidence = 0.5m,
            CreatedAt = DateTimeOffset.UtcNow
        }).ToList();

        await using (var scope = fixture.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.PersonInferences.AddRange(inferences);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var deleteResponse = await fixture.Client.DeleteAsync($"/interactions/{interactionId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        using var getResponse = await fixture.Client.GetAsync($"/interactions/{interactionId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);

        await using var verificationScope = fixture.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var remainingInferenceIds = await verificationContext.PersonInferences
            .AsNoTracking()
            .Where(inference => inference.PersonId == personId)
            .Select(inference => inference.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        var expectedInferenceIds = inferences
            .Where(inference => inference.SourceInteractionId != interactionId)
            .Select(inference => inference.Id)
            .ToList();

        Assert.Equal(expectedInferenceIds.Order(), remainingInferenceIds.Order());
        Assert.True(await verificationContext.Interactions.AsNoTracking()
            .AnyAsync(interaction => interaction.Id == otherInteractionId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetById_returns_not_found_for_unknown_interaction()
    {
        using var response = await fixture.Client.GetAsync($"/interactions/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_and_update_reject_participants_from_another_profile(bool update)
    {
        var (userProfileId, personId, _) = await CreatePeopleAsync();
        var otherProfileId = await fixture.CreateUserProfileAsync();
        var otherPersonId = await CreatePersonAsync(otherProfileId, "Other profile");
        var interactionId = await CreateInteractionAsync(
            CreateRequest(userProfileId, "Original", [personId], DateTimeOffset.UtcNow));
        var request = CreateRequest(userProfileId, "Invalid", [otherPersonId], DateTimeOffset.UtcNow);
        using var response = update
            ? await fixture.Client.PutAsJsonAsync($"/interactions/{interactionId}", request, TestContext.Current.CancellationToken)
            : await fixture.Client.PostAsJsonAsync("/interactions", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_retains_existing_participants_and_normalizes_offset_timestamp()
    {
        var (userProfileId, firstPersonId, secondPersonId) = await CreatePeopleAsync();
        var occurredAt = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.FromHours(3));
        var interactionId = await CreateInteractionAsync(CreateRequest(userProfileId, "Original", [firstPersonId], occurredAt));
        var request = new UpdateInteraction.Request
        {
            Title = "Updated",
            Description = "Updated description",
            OccurredAt = occurredAt.AddHours(1),
            ParticipantIds = [firstPersonId, secondPersonId, firstPersonId]
        };

        using var response = await fixture.Client.PutAsJsonAsync($"/interactions/{interactionId}", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var interaction = await GetInteractionAsync(interactionId);
        Assert.Equal(occurredAt.AddHours(1).ToUniversalTime(), interaction.OccurredAt);
        Assert.Equal(2, interaction.Participants.Count);
        Assert.Contains(interaction.Participants, participant => participant.Id == firstPersonId);
        Assert.Contains(interaction.Participants, participant => participant.Id == secondPersonId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_and_update_reject_null_participant_collection(bool update)
    {
        var (userProfileId, personId, _) = await CreatePeopleAsync();
        var interactionId = await CreateInteractionAsync(
            CreateRequest(userProfileId, "Original", [personId], DateTimeOffset.UtcNow));
        var body = new
        {
            UserProfileId = userProfileId,
            Title = "Invalid",
            Description = "Description",
            OccurredAt = DateTimeOffset.UtcNow,
            ParticipantIds = (Guid[]?)null
        };
        using var response = update
            ? await fixture.Client.PutAsJsonAsync($"/interactions/{interactionId}", body, TestContext.Current.CancellationToken)
            : await fixture.Client.PostAsJsonAsync("/interactions", body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<(Guid UserProfileId, Guid FirstPersonId, Guid SecondPersonId)> CreatePeopleAsync()
    {
        var userProfileId = await fixture.CreateUserProfileAsync();
        var firstPersonId = await CreatePersonAsync(userProfileId, "Morgan");
        var secondPersonId = await CreatePersonAsync(userProfileId, "Alex");

        return (userProfileId, firstPersonId, secondPersonId);
    }

    private async Task<Guid> CreatePersonAsync(Guid userProfileId, string displayName)
    {
        return await fixture.CreatePersonAsync(new CreatePerson.Request
        {
            UserProfileId = userProfileId,
            DisplayName = displayName,
            RelationshipContext = RelationshipContext.Acquaintance
        });
    }

    private async Task<Guid> CreateInteractionAsync(CreateInteraction.Request request)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/interactions", request);
        response.EnsureSuccessStatusCode();

        var interaction = await response.Content.ReadFromJsonAsync<CreateInteraction.Response>();
        return interaction!.Id;
    }

    private async Task<GetInteraction.Response> GetInteractionAsync(Guid interactionId)
    {
        using var response = await fixture.Client.GetAsync($"/interactions/{interactionId}");
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<GetInteraction.Response>())!;
    }

    private static CreateInteraction.Request CreateRequest(
        Guid userProfileId,
        string title,
        IReadOnlyList<Guid> participantIds,
        DateTimeOffset occurredAt) => new()
        {
            UserProfileId = userProfileId,
            Title = title,
            Description = $"{title} description",
            OccurredAt = occurredAt,
            ParticipantIds = participantIds
        };
}
