using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Domain.Users;
using SocialTelemetry.Api.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using CreatePerson = SocialTelemetry.Api.Features.People.Create;
using GetPerson = SocialTelemetry.Api.Features.People.GetById;
using GetPeople = SocialTelemetry.Api.Features.People.GetAll;
using UpdatePerson = SocialTelemetry.Api.Features.People.Update;

namespace SocialTelemetry.Tests.Features.People;

public sealed class PeopleCrudTests : IClassFixture<PeopleApiFixture>
{
    private readonly PeopleApiFixture fixture;

    public PeopleCrudTests(PeopleApiFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task Create_returns_created_and_persists_person()
    {
        var userProfileId = await fixture.CreateUserProfileAsync();
        var request = CreateRequest(userProfileId, "Morgan");

        using var response = await fixture.Client.PostAsJsonAsync("/people", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var createdPerson = await response.Content.ReadFromJsonAsync<CreatePerson.Response>();
        Assert.NotNull(createdPerson);

        var persistedPerson = await fixture.FindPersonAsync(createdPerson.Id);
        Assert.NotNull(persistedPerson);
        Assert.Equal("Morgan", persistedPerson.DisplayName);
        Assert.Equal(userProfileId, persistedPerson.UserProfileId);
    }

    [Fact]
    public async Task GetById_returns_created_person()
    {
        var userProfileId = await fixture.CreateUserProfileAsync();
        var personId = await fixture.CreatePersonAsync(CreateRequest(userProfileId, "Casey"));

        using var response = await fixture.Client.GetAsync($"/people/{personId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var person = await response.Content.ReadFromJsonAsync<GetPerson.Response>();
        Assert.NotNull(person);
        Assert.Equal(personId, person.Id);
        Assert.Equal("Casey", person.DisplayName);
        Assert.Equal(userProfileId, person.UserProfileId);
    }

    [Fact]
    public async Task GetById_returns_not_found_for_unknown_person()
    {
        using var response = await fixture.Client.GetAsync($"/people/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_returns_people_for_user_profile()
    {
        var userProfileId = await fixture.CreateUserProfileAsync();
        await fixture.CreatePersonAsync(CreateRequest(userProfileId, "Taylor"));
        await fixture.CreatePersonAsync(CreateRequest(userProfileId, "Alex"));

        using var response = await fixture.Client.GetAsync($"/people?UserProfileId={userProfileId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var people = await response.Content.ReadFromJsonAsync<GetPeople.Response>();
        Assert.NotNull(people);
        Assert.Collection(
            people.People,
            person => Assert.Equal("Alex", person.DisplayName),
            person => Assert.Equal("Taylor", person.DisplayName));
    }

    [Fact]
    public async Task Update_changes_persisted_person()
    {
        var userProfileId = await fixture.CreateUserProfileAsync();
        var personId = await fixture.CreatePersonAsync(CreateRequest(userProfileId, "Old name"));
        var request = new UpdatePerson.Request
        {
            Id = personId,
            DisplayName = "Updated name",
            Age = 30,
            Gender = "Non-binary",
            Description = "Updated description",
            RelationshipContext = RelationshipContext.Friend,
            HowWeMet = "At work",
            Notes = "Updated notes"
        };

        using var response = await fixture.Client.PutAsJsonAsync($"/people/{personId}", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persistedPerson = await fixture.FindPersonAsync(personId);
        Assert.NotNull(persistedPerson);
        Assert.Equal("Updated name", persistedPerson.DisplayName);
        Assert.Equal(30, persistedPerson.Age);
        Assert.Equal(RelationshipContext.Friend, persistedPerson.RelationshipContext);
    }

    [Fact]
    public async Task Delete_removes_person()
    {
        var userProfileId = await fixture.CreateUserProfileAsync();
        var personId = await fixture.CreatePersonAsync(CreateRequest(userProfileId, "Jordan"));

        using var response = await fixture.Client.DeleteAsync($"/people/{personId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await fixture.FindPersonAsync(personId));
    }

    [Fact]
    public async Task Delete_returns_not_found_for_unknown_person()
    {
        using var response = await fixture.Client.DeleteAsync($"/people/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static CreatePerson.Request CreateRequest(Guid userProfileId, string displayName) => new()
    {
        UserProfileId = userProfileId,
        DisplayName = displayName,
        Description = "Test person",
        RelationshipContext = RelationshipContext.Acquaintance
    };
}

public sealed class PeopleApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("socialtelemetry_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private PeopleWebApplicationFactory? application;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await database.StartAsync();

        application = new PeopleWebApplicationFactory(database.GetConnectionString());
        await using var scope = application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Database.MigrateAsync();

        Client = application.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        application?.Dispose();
        await database.DisposeAsync();
    }

    public async Task<Guid> CreateUserProfileAsync()
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userProfile = new UserProfile
        {
            Id = Guid.NewGuid(),
            DisplayName = "Test user"
        };

        dbContext.UserProfiles.Add(userProfile);
        await dbContext.SaveChangesAsync();

        return userProfile.Id;
    }

    public async Task<Guid> CreatePersonAsync(CreatePerson.Request request)
    {
        using var response = await Client.PostAsJsonAsync("/people", request);
        response.EnsureSuccessStatusCode();

        var createdPerson = await response.Content.ReadFromJsonAsync<CreatePerson.Response>();
        return createdPerson!.Id;
    }

    public async Task<Person?> FindPersonAsync(Guid personId)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.People
            .AsNoTracking()
            .SingleOrDefaultAsync(person => person.Id == personId);
    }

    private sealed class PeopleWebApplicationFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = connectionString
                });
            });
        }
    }
}
