using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Infrastructure.AI.Models;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Tests.Features.People;
using CreateInteraction = SocialTelemetry.Api.Features.Interactions.Create;
using CreatePerson = SocialTelemetry.Api.Features.People.Create;
using StoredSuggestion = SocialTelemetry.Api.Domain.People.SuggestedProfileUpdate;

namespace SocialTelemetry.Tests.Infrastructure.Persistence;

public abstract class AiReadinessTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture> where TFixture : PeopleApiFixture
{
    [Fact]
    public async Task Concurrent_analyses_preserve_separate_results_and_provenance()
    {
        var (interactionId, _, _) = await CreateInteractionAsync();
        var firstAnalysis = CreateAnalysis(interactionId);
        var secondAnalysis = CreateAnalysis(interactionId);
        await using var firstScope = fixture.CreateAsyncScope();
        await using var secondScope = fixture.CreateAsyncScope();
        var firstContext = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secondContext = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        firstContext.InteractionAnalyses.Add(firstAnalysis);
        secondContext.InteractionAnalyses.Add(secondAnalysis);

        await Task.WhenAll(firstContext.SaveChangesAsync(TestContext.Current.CancellationToken), secondContext.SaveChangesAsync(TestContext.Current.CancellationToken));

        var storedAnalyses = await firstContext.InteractionAnalyses.AsNoTracking()
            .Where(analysis => analysis.InteractionId == interactionId).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, storedAnalyses.Count);
        foreach (var analysis in storedAnalyses)
        {
            Assert.Equal("test-provider", analysis.Provider);
            Assert.Equal("test-model", analysis.Model);
            Assert.Equal("v1", analysis.SchemaVersion);
            Assert.NotEqual(default, analysis.CreatedAt);
            Assert.NotNull(analysis.ResultJson);
            var result = JsonSerializer.Deserialize<InteractionAnalysisResult>(analysis.ResultJson);
            Assert.NotNull(result);
            Assert.Equal("Translated text", result.Translation);
            Assert.Equal("Literal meaning", result.LiteralMeaning);
            Assert.Equal("Possible social meaning", result.SocialMeaning);
            var interpretation = Assert.Single(result.Interpretations);
            Assert.Equal(0.5m, interpretation.Confidence);
            Assert.Equal(interactionId, Assert.Single(interpretation.SupportingInteractionIds));
            Assert.Single(result.Uncertainties);
            Assert.Single(result.MissingContext);
            Assert.Single(result.SuggestedReplies);
            Assert.Single(result.SuggestedNextSteps);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deleting_interaction_or_analysis_removes_its_messages_and_suggestions(bool deleteInteraction)
    {
        var (interactionId, personId, _) = await CreateInteractionAsync();
        var (otherInteractionId, otherPersonId, _) = await CreateInteractionAsync();
        var firstAnalysis = CreateAnalysis(interactionId);
        var secondAnalysis = CreateAnalysis(interactionId);
        var otherAnalysis = CreateAnalysis(otherInteractionId);
        await using (var scope = fixture.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.SuggestedProfileUpdates.AddRange(
                CreateSuggestion(firstAnalysis, personId),
                CreateSuggestion(secondAnalysis, personId),
                CreateSuggestion(otherAnalysis, otherPersonId));
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        if (deleteInteraction)
        {
            using var response = await fixture.Client.DeleteAsync($"/interactions/{interactionId}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            // Analysis endpoints do not exist yet; exercise the database cascade directly.
            await using var scope = fixture.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await dbContext.InteractionAnalyses.Where(analysis => analysis.Id == firstAnalysis.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        Guid[] removedAnalysisIds = deleteInteraction ? [firstAnalysis.Id, secondAnalysis.Id] : [firstAnalysis.Id];
        await using var verificationScope = fixture.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verificationContext.InteractionAnalyses.AsNoTracking()
            .AnyAsync(analysis => removedAnalysisIds.Contains(analysis.Id), TestContext.Current.CancellationToken));
        Assert.False(await verificationContext.AnalysisConversationMessages.AsNoTracking()
            .AnyAsync(message => removedAnalysisIds.Contains(message.InteractionAnalysisId), TestContext.Current.CancellationToken));
        Assert.False(await verificationContext.SuggestedProfileUpdates.AsNoTracking()
            .AnyAsync(suggestion => removedAnalysisIds.Contains(suggestion.InteractionAnalysisId), TestContext.Current.CancellationToken));
        Assert.True(await verificationContext.InteractionAnalyses.AsNoTracking().AnyAsync(analysis => analysis.Id == otherAnalysis.Id, TestContext.Current.CancellationToken));
        Assert.True(await verificationContext.AnalysisConversationMessages.AsNoTracking()
            .AnyAsync(message => message.InteractionAnalysisId == otherAnalysis.Id, TestContext.Current.CancellationToken));
        Assert.True(await verificationContext.SuggestedProfileUpdates.AsNoTracking()
            .AnyAsync(suggestion => suggestion.InteractionAnalysisId == otherAnalysis.Id, TestContext.Current.CancellationToken));
        Assert.Equal(!deleteInteraction, await verificationContext.InteractionAnalyses.AsNoTracking()
            .AnyAsync(analysis => analysis.Id == secondAnalysis.Id, TestContext.Current.CancellationToken));
        Assert.Equal(!deleteInteraction, await verificationContext.AnalysisConversationMessages.AsNoTracking()
            .AnyAsync(message => message.InteractionAnalysisId == secondAnalysis.Id, TestContext.Current.CancellationToken));
        Assert.Equal(!deleteInteraction, await verificationContext.SuggestedProfileUpdates.AsNoTracking()
            .AnyAsync(suggestion => suggestion.InteractionAnalysisId == secondAnalysis.Id, TestContext.Current.CancellationToken));
        Assert.Equal(!deleteInteraction, await verificationContext.Interactions.AsNoTracking()
            .AnyAsync(interaction => interaction.Id == interactionId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_person_removes_person_owned_suggestions_and_inferences_but_preserves_shared_analysis()
    {
        var (interactionId, personId, otherPersonId) = await CreateInteractionAsync();
        var analysis = CreateAnalysis(interactionId);
        await using (var scope = fixture.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.SuggestedProfileUpdates.AddRange(CreateSuggestion(analysis, personId), CreateSuggestion(analysis, otherPersonId));
            foreach (var participantId in new[] { personId, otherPersonId })
            {
                dbContext.PersonInferences.Add(new PersonInference
                {
                    Id = Guid.NewGuid(),
                    PersonId = participantId,
                    SourceInteractionId = interactionId,
                    Value = "Tentative interpretation",
                    Confidence = 0.5m,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var response = await fixture.Client.DeleteAsync($"/people/{personId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verificationScope = fixture.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verificationContext.SuggestedProfileUpdates.AsNoTracking().AnyAsync(suggestion => suggestion.PersonId == personId, TestContext.Current.CancellationToken));
        Assert.False(await verificationContext.PersonInferences.AsNoTracking().AnyAsync(inference => inference.PersonId == personId, TestContext.Current.CancellationToken));
        Assert.False(await verificationContext.InteractionParticipants.AsNoTracking().AnyAsync(participant => participant.PersonId == personId, TestContext.Current.CancellationToken));
        Assert.True(await verificationContext.SuggestedProfileUpdates.AsNoTracking().AnyAsync(suggestion => suggestion.PersonId == otherPersonId, TestContext.Current.CancellationToken));
        Assert.True(await verificationContext.PersonInferences.AsNoTracking().AnyAsync(inference => inference.PersonId == otherPersonId, TestContext.Current.CancellationToken));
        Assert.True(await verificationContext.InteractionAnalyses.AsNoTracking().AnyAsync(storedAnalysis => storedAnalysis.Id == analysis.Id, TestContext.Current.CancellationToken));
        Assert.True(await verificationContext.AnalysisConversationMessages.AsNoTracking().AnyAsync(message => message.InteractionAnalysisId == analysis.Id, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(SuggestionStatus.Accepted)]
    [InlineData(SuggestionStatus.Rejected)]
    public async Task Suggestions_preserve_review_data_and_reject_a_stale_review(SuggestionStatus decision)
    {
        var (interactionId, personId, _) = await CreateInteractionAsync();
        var suggestion = CreateSuggestion(CreateAnalysis(interactionId), personId);
        await using var firstScope = fixture.CreateAsyncScope();
        await using var secondScope = fixture.CreateAsyncScope();
        var firstContext = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secondContext = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        firstContext.SuggestedProfileUpdates.Add(suggestion);
        await firstContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var staleSuggestion = await secondContext.SuggestedProfileUpdates.SingleAsync(storedSuggestion => storedSuggestion.Id == suggestion.Id, TestContext.Current.CancellationToken);
        Assert.Equal(SuggestionStatus.Pending, staleSuggestion.Status);
        Assert.Null(staleSuggestion.AcceptedValue);
        Assert.Null(staleSuggestion.ReviewedAt);
        Assert.False(await firstContext.PersonFacts.AsNoTracking().AnyAsync(fact => fact.PersonId == personId, TestContext.Current.CancellationToken));

        suggestion.Status = decision;
        suggestion.AcceptedValue = decision == SuggestionStatus.Accepted ? "User-edited value" : null;
        suggestion.ReviewedAt = DateTimeOffset.UtcNow;
        await firstContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        staleSuggestion.Status = SuggestionStatus.Rejected;
        staleSuggestion.ReviewedAt = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondContext.SaveChangesAsync(TestContext.Current.CancellationToken));

        var reviewedSuggestion = await firstContext.SuggestedProfileUpdates.AsNoTracking()
            .SingleAsync(storedSuggestion => storedSuggestion.Id == suggestion.Id, TestContext.Current.CancellationToken);
        Assert.Equal(decision, reviewedSuggestion.Status);
        Assert.Equal("Original suggestion", reviewedSuggestion.SuggestedValue);
        Assert.Equal(suggestion.AcceptedValue, reviewedSuggestion.AcceptedValue);
        Assert.NotNull(reviewedSuggestion.ReviewedAt);
    }

    private async Task<(Guid InteractionId, Guid PersonId, Guid OtherPersonId)> CreateInteractionAsync()
    {
        var profileId = await fixture.CreateUserProfileAsync();
        var personId = await fixture.CreatePersonAsync(new CreatePerson.Request { UserProfileId = profileId, DisplayName = "Morgan" });
        var otherPersonId = await fixture.CreatePersonAsync(new CreatePerson.Request { UserProfileId = profileId, DisplayName = "Alex" });
        using var response = await fixture.Client.PostAsJsonAsync("/interactions", new CreateInteraction.Request
        {
            UserProfileId = profileId,
            Title = "Test conversation",
            Description = "Test context",
            OccurredAt = DateTimeOffset.UtcNow,
            ParticipantIds = [personId, otherPersonId]
        });
        response.EnsureSuccessStatusCode();
        var interaction = await response.Content.ReadFromJsonAsync<CreateInteraction.Response>();
        Assert.NotNull(interaction);
        return (interaction.Id, personId, otherPersonId);
    }

    private static InteractionAnalysis CreateAnalysis(Guid interactionId) => new()
    {
        Id = Guid.NewGuid(),
        InteractionId = interactionId,
        Summary = "Test summary",
        Provider = "test-provider",
        Model = "test-model",
        SchemaVersion = "v1",
        ResultJson = JsonSerializer.Serialize(new InteractionAnalysisResult("Test summary")
        {
            Translation = "Translated text",
            LiteralMeaning = "Literal meaning",
            SocialMeaning = "Possible social meaning",
            Interpretations = [new AnalysisInterpretation("One possible meaning", 0.5m, [interactionId])],
            Uncertainties = ["Tone is uncertain"],
            MissingContext = ["Previous conversation"],
            SuggestedReplies = ["Could you clarify?"],
            SuggestedNextSteps = ["Ask for more context"]
        }),
        CreatedAt = DateTimeOffset.UtcNow,
        ConversationMessages = [new AnalysisConversationMessage
        {
            Id = Guid.NewGuid(),
            Role = "user",
            Content = "Test follow-up",
            CreatedAt = DateTimeOffset.UtcNow
        }]
    };

    private static StoredSuggestion CreateSuggestion(InteractionAnalysis analysis, Guid personId) => new()
    {
        Id = Guid.NewGuid(),
        PersonId = personId,
        InteractionAnalysisId = analysis.Id,
        InteractionAnalysis = analysis,
        Field = "Notes",
        SuggestedValue = "Original suggestion",
        CreatedAt = DateTimeOffset.UtcNow
    };
}

public sealed class AiReadinessTests(PeopleApiFixture fixture) : AiReadinessTestsContract<PeopleApiFixture>(fixture);

[Collection("SQLite")]
public sealed class SqliteAiReadinessTests(SqliteApiFixture fixture) : AiReadinessTestsContract<SqliteApiFixture>(fixture);
