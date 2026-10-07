namespace SocialTelemetry.Tests.Infrastructure.Persistence;

// Concurrent in-process host startup races DataAnnotations' cached TimeSpan RangeAttribute conversion.
// Database concurrency is exercised explicitly with separate connections inside the tests.
[CollectionDefinition("SQLite", DisableParallelization = true)]
public sealed class SqliteTestCollection;
