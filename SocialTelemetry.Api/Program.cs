using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is required.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddFastEndpoints();
builder.Services.SwaggerDocument();
builder.Services.AddScoped<IAiClient, OpenAiClient>();
builder.Services.AddScoped<IAttachmentStorage, LocalAttachmentStorage>();
builder.Services.AddSingleton<AiContextBuilder>();

var app = builder.Build();

app.UseHttpsRedirection();
app.UseFastEndpoints();
app.UseSwaggerGen();

app.Run();

public partial class Program;
