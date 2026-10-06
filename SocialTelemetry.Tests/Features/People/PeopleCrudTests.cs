using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using CreatePerson = SocialTelemetry.Api.Features.People.Create;
using DomainUserProfile = SocialTelemetry.Api.Domain.Users.UserProfile;
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_and_update_reject_invalid_name_or_relationship(bool update, bool invalidRelationship)
    {
        var userProfileId = await fixture.CreateUserProfileAsync();
        var personId = await fixture.CreatePersonAsync(CreateRequest(userProfileId, "Original"));
        var body = new
        {
            UserProfileId = userProfileId,
            DisplayName = invalidRelationship ? "Valid name" : new string('a', 201),
            RelationshipContext = invalidRelationship ? (RelationshipContext)99 : RelationshipContext.Friend
        };
        using var response = update
            ? await fixture.Client.PutAsJsonAsync($"/people/{personId}", body)
            : await fixture.Client.PostAsJsonAsync("/people", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
    private string? attachmentStorageDirectory;

    public HttpClient Client { get; private set; } = null!;
    public string AttachmentStorageDirectory => attachmentStorageDirectory
        ?? throw new InvalidOperationException("The fixture has not been initialized.");

    public WebApplicationFactory<Program> WithServices(Action<IServiceCollection> configureServices) =>
        (application ?? throw new InvalidOperationException("The fixture has not been initialized."))
        .WithWebHostBuilder(builder => builder.ConfigureServices(configureServices));

    public AsyncServiceScope CreateAsyncScope() =>
        (application ?? throw new InvalidOperationException("The fixture has not been initialized."))
        .Services.CreateAsyncScope();

    public async ValueTask InitializeAsync()
    {
        await database.StartAsync();

        attachmentStorageDirectory = Path.Combine(
            Path.GetTempPath(),
            "SocialTelemetry.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentStorageDirectory);

        // Startup recovery queries attachments, so migrate before starting the HTTP host.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(database.GetConnectionString())
            .Options;
        await using (var dbContext = new AppDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        application = new PeopleWebApplicationFactory(database.GetConnectionString(), attachmentStorageDirectory);
        Client = application.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Client?.Dispose();
            application?.Dispose();
        }
        finally
        {
            try
            {
                await database.DisposeAsync();
            }
            finally
            {
                if (attachmentStorageDirectory is not null && Directory.Exists(attachmentStorageDirectory))
                {
                    Directory.Delete(attachmentStorageDirectory, recursive: true);
                }
            }
        }
    }

    public async Task<Guid> CreateUserProfileAsync(string displayName = "Test user")
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userProfile = new DomainUserProfile
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName
        };

        dbContext.UserProfiles.Add(userProfile);
        await dbContext.SaveChangesAsync();

        return userProfile.Id;
    }

    public async Task ClearUserProfilesAsync()
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.UserProfiles.ExecuteDeleteAsync();
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

    public async Task<InteractionAttachment?> FindAttachmentAsync(Guid attachmentId)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.InteractionAttachments
            .AsNoTracking()
            .SingleOrDefaultAsync(attachment => attachment.Id == attachmentId);
    }

    private sealed class PeopleWebApplicationFactory(
        string connectionString,
        string attachmentStorageDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("AttachmentStorage:LocalDirectory", attachmentStorageDirectory);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
            });
        }
    }
}
