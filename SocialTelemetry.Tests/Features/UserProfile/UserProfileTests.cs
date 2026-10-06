using System.Net;
using System.Net.Http.Json;
using SocialTelemetry.Tests.Features.People;
using GetUserProfile = SocialTelemetry.Api.Features.UserProfile.Get;
using UpdateUserProfile = SocialTelemetry.Api.Features.UserProfile.Update;

namespace SocialTelemetry.Tests.Features.UserProfile;

public sealed class UserProfileTests : IClassFixture<PeopleApiFixture>, IAsyncLifetime
{
    private readonly PeopleApiFixture fixture;

    public UserProfileTests(PeopleApiFixture fixture)
    {
        this.fixture = fixture;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async ValueTask InitializeAsync()
    {
        await fixture.ClearUserProfilesAsync();
    }

    [Fact]
    public async Task Get_returns_profile()
    {
        var userProfileId = await fixture.CreateUserProfileAsync("Morgan");

        using var response = await fixture.Client.GetAsync("/user-profile", TestContext.Current.CancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            Assert.Fail(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var userProfile = await response.Content.ReadFromJsonAsync<GetUserProfile.Response>(TestContext.Current.CancellationToken);
        Assert.NotNull(userProfile);
        Assert.Equal(userProfileId, userProfile.Id);
        Assert.Equal("Morgan", userProfile.DisplayName);
    }

    [Fact]
    public async Task Update_persists_changes()
    {
        var userProfileId = await fixture.CreateUserProfileAsync("Old name");
        var request = new UpdateUserProfile.Request
        {
            DisplayName = "Updated name",
            AboutMe = "Updated about me",
            CommunicationStyle = "Direct",
            Goals = "Learn",
            Preferences = "Concise",
            Boundaries = "No guessing",
            AiInstructions = "Ask for context"
        };

        using var updateResponse = await fixture.Client.PutAsJsonAsync("/user-profile", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        using var getResponse = await fixture.Client.GetAsync("/user-profile", TestContext.Current.CancellationToken);

        if (getResponse.StatusCode != HttpStatusCode.OK)
        {
            Assert.Fail(await getResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var userProfile = await getResponse.Content.ReadFromJsonAsync<GetUserProfile.Response>(TestContext.Current.CancellationToken);

        Assert.NotNull(userProfile);
        Assert.Equal(userProfileId, userProfile.Id);
        Assert.Equal("Updated name", userProfile.DisplayName);
        Assert.Equal("No guessing", userProfile.Boundaries);
    }
}
