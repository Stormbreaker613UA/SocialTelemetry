using Microsoft.Extensions.Logging.Abstractions;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Tests.Infrastructure.Storage;

public sealed class LocalFileStorageTests
{
    [Fact]
    public async Task Separate_storage_areas_preserve_staging_and_isolate_deletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "SocialTelemetry-files", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new LocalFileStorage(Path.Combine(root, "first"), NullLogger<LocalFileStorage>.Instance);
            var second = new LocalFileStorage(Path.Combine(root, "second"), NullLogger<LocalFileStorage>.Instance);
            using var content = new MemoryStream(new byte[] { 1, 2, 3 });
            var key = await first.SaveAsync(content, TestContext.Current.CancellationToken);
            Assert.False(await first.ExistsAsync(key, TestContext.Current.CancellationToken));
            Assert.Single(first.EnumerateStagedFiles());
            Assert.True(await first.CompleteUploadAsync(key, TestContext.Current.CancellationToken));
            await second.DeleteAsync(key, TestContext.Current.CancellationToken);
            await using var result = await first.OpenReadAsync(key, TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            using var buffer = new MemoryStream();
            await result.CopyToAsync(buffer, TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 1, 2, 3 }, buffer.ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
