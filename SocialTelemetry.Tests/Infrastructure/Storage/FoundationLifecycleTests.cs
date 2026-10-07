using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Domain.Users;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;
using SocialTelemetry.Tests.Features.People;

namespace SocialTelemetry.Tests.Infrastructure.Storage;

public sealed class FoundationLifecycleTests(PeopleApiFixture fixture) : IClassFixture<PeopleApiFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("attachment", AttachmentStatus.Ready)]
    [InlineData("interaction", AttachmentStatus.Ready)]
    [InlineData("recovery", AttachmentStatus.Ready)]
    [InlineData("attachment", AttachmentStatus.Pending)]
    public async Task Attachment_cleanup_preserves_a_file_owned_by_another_profile(string deletion, AttachmentStatus otherStatus)
    {
        var first = await SeedProfileAsync();
        var other = await SeedProfileAsync();
        string key;
        Guid firstAttachmentId;
        Guid otherAttachmentId;
        await using (var scope = fixture.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IAttachmentStorage>();
            using var bytes = new MemoryStream([1, 2, 3]);
            key = await storage.SaveAsync(bytes, "synthetic.png", "image/png", Cancellation);
            Assert.True(await storage.CompleteUploadAsync(key, Cancellation));
            var firstAttachment = Attachment(first.InteractionId, key, deletion == "recovery" ? AttachmentStatus.Deleting : AttachmentStatus.Ready);
            var otherAttachment = Attachment(other.InteractionId, key, otherStatus);
            firstAttachmentId = firstAttachment.Id;
            otherAttachmentId = otherAttachment.Id;
            database.InteractionAttachments.AddRange(firstAttachment, otherAttachment);
            await database.SaveChangesAsync(Cancellation);
        }
        if (deletion == "recovery")
        {
            using var restarted = fixture.WithServices(_ => { });
            using var client = restarted.CreateClient();
        }
        else
        {
            var route = deletion == "interaction" ? $"/interactions/{first.InteractionId}" :
                $"/interactions/{first.InteractionId}/attachments/{firstAttachmentId}";
            using var response = await fixture.Client.DeleteAsync(route, Cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        if (otherStatus == AttachmentStatus.Pending)
        {
            using var restarted = fixture.WithServices(_ => { });
            using var client = restarted.CreateClient();
        }
        Assert.Null(await fixture.FindAttachmentAsync(firstAttachmentId));
        using var download = await fixture.Client.GetAsync($"/interactions/{other.InteractionId}/attachments/{otherAttachmentId}/content", Cancellation);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await download.Content.ReadAsByteArrayAsync(Cancellation));
        using var deleted = await fixture.Client.DeleteAsync($"/interactions/{other.InteractionId}/attachments/{otherAttachmentId}", Cancellation);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        await using var verification = fixture.CreateAsyncScope();
        Assert.False(await verification.ServiceProvider.GetRequiredService<IAttachmentStorage>().ExistsAsync(key, Cancellation));
    }

    [Fact]
    public async Task Deleting_one_profile_preserves_shared_avatar_until_the_last_reference_is_removed()
    {
        var first = await SeedProfileAsync();
        var other = await SeedProfileAsync();
        string key;
        await using (var scope = fixture.CreateAsyncScope())
        {
            var storage = scope.ServiceProvider.GetRequiredKeyedService<IFileStorage>("avatars");
            using var content = new MemoryStream([1, 2, 3]);
            key = await storage.SaveAsync(content, Cancellation);
            Assert.True(await storage.CompleteUploadAsync(key, Cancellation));
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var personId in new[] { first.PersonId, other.PersonId })
            {
                var person = await database.People.SingleAsync(person => person.Id == personId, Cancellation);
                // Windows treats differently cased GUID keys as the same physical file.
                person.AvatarStorageKey = personId == other.PersonId && OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key;
                person.AvatarMimeType = "image/png";
            }
            await database.SaveChangesAsync(Cancellation);
        }
        await DeleteProfileAsync(first.ProfileId);
        await using var verification = fixture.CreateAsyncScope();
        var files = verification.ServiceProvider.GetRequiredKeyedService<IFileStorage>("avatars");
        Assert.True(await files.ExistsAsync(key, Cancellation));
        using var response = await fixture.Client.GetAsync($"/people/{other.PersonId}/avatar", Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await DeleteProfileAsync(other.ProfileId);
        Assert.False(await files.ExistsAsync(key, Cancellation));
    }

    [Fact]
    public async Task Profile_deletion_cascades_owned_graph_and_leaves_another_profile_intact()
    {
        var removed = await SeedProfileAsync();
        var retained = await SeedProfileAsync();
        await DeleteProfileAsync(removed.ProfileId);
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var graph in new[] { removed, retained })
        {
            var expected = graph == retained;
            Assert.Equal(expected, await database.UserProfiles.AnyAsync(profile => profile.Id == graph.ProfileId, Cancellation));
            Assert.Equal(expected, await database.People.AnyAsync(person => person.Id == graph.PersonId, Cancellation));
            Assert.Equal(expected, await database.PersonFacts.AnyAsync(fact => fact.PersonId == graph.PersonId, Cancellation));
            Assert.Equal(expected, await database.PersonInferences.AnyAsync(inference => inference.PersonId == graph.PersonId, Cancellation));
            Assert.Equal(expected, await database.PersonExternalConnections.AnyAsync(connection => connection.PersonId == graph.PersonId, Cancellation));
            Assert.Equal(expected, await database.UserProfileExternalConnections.AnyAsync(connection => connection.UserProfileId == graph.ProfileId, Cancellation));
            Assert.Equal(expected, await database.Interactions.AnyAsync(interaction => interaction.Id == graph.InteractionId, Cancellation));
            Assert.Equal(expected, await database.InteractionParticipants.AnyAsync(participant => participant.InteractionId == graph.InteractionId, Cancellation));
            Assert.Equal(expected, await database.InteractionAttachments.AnyAsync(attachment => attachment.InteractionId == graph.InteractionId, Cancellation));
            Assert.Equal(expected, await database.InteractionAnalyses.AnyAsync(analysis => analysis.Id == graph.AnalysisId, Cancellation));
            Assert.Equal(expected, await database.AnalysisConversationMessages.AnyAsync(message => message.InteractionAnalysisId == graph.AnalysisId, Cancellation));
            Assert.Equal(expected, await database.SuggestedProfileUpdates.AnyAsync(suggestion => suggestion.InteractionAnalysisId == graph.AnalysisId, Cancellation));
        }
    }

    private async Task DeleteProfileAsync(Guid profileId)
    {
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await database.UserProfiles.SingleAsync(profile => profile.Id == profileId, Cancellation);
        database.UserProfiles.Remove(profile);
        await database.SaveChangesAsync(Cancellation);
    }

    private async Task<ProfileGraph> SeedProfileAsync()
    {
        var profile = new UserProfile { Id = Guid.NewGuid(), DisplayName = "Synthetic foundation user" };
        var person = new Person { Id = Guid.NewGuid(), UserProfile = profile, DisplayName = "Synthetic participant" };
        var interaction = new Interaction { Id = Guid.NewGuid(), UserProfile = profile, Title = "Synthetic", Description = "Synthetic context", OccurredAt = DateTimeOffset.UtcNow };
        var analysis = new InteractionAnalysis { Id = Guid.NewGuid(), Interaction = interaction, Summary = "Historical synthetic summary" };
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.People.Add(person);
        database.PersonFacts.Add(new PersonFact { Id = Guid.NewGuid(), Person = person, Value = "Confirmed synthetic fact" });
        database.PersonInferences.Add(new PersonInference { Id = Guid.NewGuid(), Person = person, SourceInteraction = interaction, Value = "Tentative synthetic interpretation" });
        database.InteractionParticipants.Add(new InteractionParticipant { Interaction = interaction, Person = person });
        database.InteractionAttachments.Add(new InteractionAttachment { Id = Guid.NewGuid(), Interaction = interaction, Type = AttachmentType.Text, TextContent = "Synthetic evidence" });
        database.SuggestedProfileUpdates.Add(new SuggestedProfileUpdate { Id = Guid.NewGuid(), Person = person, InteractionAnalysis = analysis, Field = "Notes", SuggestedValue = "Synthetic proposal" });
        database.AnalysisConversationMessages.Add(new AnalysisConversationMessage { Id = Guid.NewGuid(), InteractionAnalysis = analysis, Role = "user", Content = "Synthetic historical message" });
        database.PersonExternalConnections.Add(new PersonExternalConnection { Id = Guid.NewGuid(), PersonId = person.Id, Platform = "example" });
        database.UserProfileExternalConnections.Add(new UserProfileExternalConnection { Id = Guid.NewGuid(), UserProfileId = profile.Id, Platform = "example" });
        await database.SaveChangesAsync(Cancellation);
        return new ProfileGraph(profile.Id, person.Id, interaction.Id, analysis.Id);
    }

    private static InteractionAttachment Attachment(Guid interactionId, string key, AttachmentStatus status) => new()
    {
        Id = Guid.NewGuid(), InteractionId = interactionId, Type = AttachmentType.Image,
        StorageKey = key, MimeType = "image/png", Status = status
    };

    private sealed record ProfileGraph(Guid ProfileId, Guid PersonId, Guid InteractionId, Guid AnalysisId);
}
