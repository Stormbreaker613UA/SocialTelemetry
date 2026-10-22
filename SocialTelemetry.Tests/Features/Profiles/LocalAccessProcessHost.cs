using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SocialTelemetry.Tests.Features.Profiles;

// Real disposable HTTPS process; certificate trust is confined to this client's exact certificate.
internal sealed class LocalAccessProcessHost : IAsyncDisposable
{
    private readonly string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SocialTelemetry.LocalAccess.Host"));
    private readonly string root;
    private readonly string certificateFile;
    private readonly string certificatePassword = Guid.NewGuid().ToString("N");
    private readonly string certificateHash;
    private readonly string? postgres;
    private Process? process;
    private Task<string>? standardOutput;
    private Task<string>? standardError;
    public Uri Address { get; }

    public LocalAccessProcessHost(string? postgres)
    {
        this.postgres = postgres;
        root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Address = new Uri("https://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SocialTelemetry disposable test host", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        certificateHash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        certificateFile = Path.Combine(root, "test-only.pfx");
        File.WriteAllBytes(certificateFile, certificate.Export(X509ContentType.Pfx, certificatePassword));
    }

    public HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
                request.RequestUri?.Authority == Address.Authority && certificate is not null &&
                certificate.GetCertHashString(HashAlgorithmName.SHA256) == certificateHash
        };
        var client = new HttpClient(handler) { BaseAddress = Address };
        client.DefaultRequestHeaders.Add("X-SocialTelemetry-Local", "1");
        return client;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (process is not null) throw new InvalidOperationException("The owned host is already running.");
        var api = typeof(Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(api)!, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(api);
        start.Environment["ASPNETCORE_URLS"] = Address.ToString();
        start.Environment["ASPNETCORE_CONTENTROOT"] = start.WorkingDirectory;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Kestrel__Certificates__Default__Path"] = certificateFile;
        start.Environment["Kestrel__Certificates__Default__Password"] = certificatePassword;
        start.Environment["ApplicationData__RootDirectory"] = root;
        start.Environment["Persistence__Provider"] = postgres is null ? "Sqlite" : "PostgreSql";
        start.Environment["ConnectionStrings__Default"] = postgres ?? "";
        start.Environment["Persistence__SqliteFile"] = "host.db";
        start.Environment["AttachmentStorage__LocalDirectory"] = Path.Combine(root, "attachments");
        start.Environment["LocalAccess__DataDirectory"] = Path.Combine(root, "local-access");
        start.Environment["ChatGpt__DataDirectory"] = Path.Combine(root, "unused-provider");
        process = Process.Start(start) ?? throw new InvalidOperationException("Owned test host did not start.");
        standardOutput = process.StandardOutput.ReadToEndAsync();
        standardError = process.StandardError.ReadToEndAsync();
        using var readiness = CreateClient();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (process.HasExited) throw new InvalidOperationException("Owned HTTPS test host exited before readiness: " + process.ExitCode);
            try
            {
                using var response = await readiness.GetAsync("/local-profiles", cancellationToken);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
        throw new TimeoutException("Owned HTTPS host did not become ready.");
    }

    public async Task StopAbruptlyAsync()
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (standardOutput is not null) await standardOutput;
            if (standardError is not null) await standardError;
        }
        finally { process.Dispose(); process = null; }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAbruptlyAsync();
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            new DirectoryInfo(resolved).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Refusing cleanup outside the generated test root.");
        Directory.Delete(resolved, recursive: true);
    }
}
