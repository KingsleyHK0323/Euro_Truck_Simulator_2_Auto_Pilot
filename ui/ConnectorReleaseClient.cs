// Edited by: OpenAI-Codex_Responsible: Source_Code
// Edited by: OpenAI-Codex_Source_Code
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ETS2AP.Ui;

internal sealed record DownloadedConnectorPackage(string Version, string Sha256, byte[] Archive);

internal sealed class ConnectorReleaseClient
{
    private const string ReleasesUrl = "https://api.github.com/repos/KingsleyHK0323/Euro_Truck_Simulator_2_Auto_Pilot/releases?per_page=30";
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private static readonly HttpClient DefaultClient = CreateClient();
    private readonly HttpClient _client;

    public ConnectorReleaseClient(HttpClient? client = null)
    {
        _client = client ?? DefaultClient;
    }

    public async Task<DownloadedConnectorPackage> DownloadLatestAsync(CancellationToken cancellationToken)
    {
        using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
        metadataRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        metadataRequest.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using HttpResponseMessage metadataResponse = await _client.SendAsync(metadataRequest,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        metadataResponse.EnsureSuccessStatusCode();
        string releasesJson = Encoding.UTF8.GetString(await ReadBoundedAsync(
            await metadataResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            MaximumMetadataBytes, cancellationToken).ConfigureAwait(false));
        ConnectorReleaseAsset asset = ConnectorReleaseAssetSelector.SelectLatest(releasesJson);

        using HttpResponseMessage packageResponse = await _client.GetAsync(asset.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        packageResponse.EnsureSuccessStatusCode();
        if (packageResponse.Content.Headers.ContentLength is long contentLength && contentLength != asset.Size)
            throw new InvalidDataException("Connector ZIP length does not match GitHub release metadata.");

        byte[] archive = await ReadBoundedAsync(
            await packageResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            checked((int)asset.Size), cancellationToken).ConfigureAwait(false);
        if (archive.LongLength != asset.Size)
            throw new InvalidDataException("Connector ZIP length does not match GitHub release metadata.");

        string actualDigest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actualDigest), Convert.FromHexString(asset.Sha256)))
            throw new InvalidDataException("Connector ZIP does not match the GitHub SHA-256 digest.");

        return new DownloadedConnectorPackage(asset.Version, actualDigest, archive);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ETS2AP-Desktop/0.1");
        return client;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("GitHub release metadata exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
