using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        // Provider failure events can contain database details and private values.
        .Filter.ByExcluding(Matching.FromSource("Microsoft.EntityFrameworkCore"))
        .Filter.ByExcluding(Matching.FromSource("Npgsql"))
        .WriteTo.Console();
});

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is required.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddFastEndpoints();
builder.Services.SwaggerDocument();
builder.Services.AddScoped<IAiClient, OpenAiClient>();
builder.Services.AddScoped<IAttachmentStorage, LocalAttachmentStorage>();
builder.Services.AddSingleton<AiContextBuilder>();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseFastEndpoints();
app.UseSwaggerGen();

app.Run();

public partial class Program;
