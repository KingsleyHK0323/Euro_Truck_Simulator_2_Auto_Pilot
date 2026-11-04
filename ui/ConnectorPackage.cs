// Edited by: OpenAI-Codex_Responsible: Source_Code
// Edited by: OpenAI-Codex_Source_Code
using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ETS2AP.Ui;

internal sealed record ConnectorReleaseAsset(string Version, Uri DownloadUri, string Sha256, long Size);

internal static class ConnectorReleaseAssetSelector
{
    private const string RepositoryPath = "/KingsleyHK0323/Euro_Truck_Simulator_2_Auto_Pilot/releases/download/";
    private static readonly Regex TagPattern = new("^v(?<version>[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?)$", RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new("^sha256:(?<digest>[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);
    public static ConnectorReleaseAsset SelectLatest(string releasesJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(releasesJson, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("GitHub release response is not an array.");

            foreach (JsonElement release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object ||
                    !release.TryGetProperty("draft", out JsonElement draft) ||
                    draft.ValueKind != JsonValueKind.False)
                    continue;

                string tag = ReadString(release, "tag_name");
                Match tagMatch = TagPattern.Match(tag);
                if (!tagMatch.Success)
                    throw new InvalidDataException("Latest published release has an unsupported tag.");

                string version = tagMatch.Groups["version"].Value;
                string expectedName = $"ETS2AP_DLL_V{version}.zip";
                if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Latest published release has no asset list.");

                JsonElement[] matches = assets.EnumerateArray()
                    .Where(asset => asset.ValueKind == JsonValueKind.Object &&
                        asset.TryGetProperty("name", out JsonElement name) &&
                        name.ValueKind == JsonValueKind.String &&
                        string.Equals(name.GetString(), expectedName, StringComparison.Ordinal))
                    .ToArray();
                if (matches.Length != 1)
                    throw new InvalidDataException("Latest published release must contain exactly one matching connector ZIP.");

                JsonElement asset = matches[0];
                string digestText = ReadString(asset, "digest");
                Match digestMatch = DigestPattern.Match(digestText);
                if (!digestMatch.Success)
                    throw new InvalidDataException("Connector ZIP has no valid SHA-256 digest.");

                long size = asset.GetProperty("size").GetInt64();
                if (size is <= 0 or > ConnectorPackage.MaximumArchiveBytes)
                    throw new InvalidDataException("Connector ZIP size is outside the supported limit.");

                string urlText = ReadString(asset, "browser_download_url");
                if (!Uri.TryCreate(urlText, UriKind.Absolute, out Uri? downloadUri) ||
                    downloadUri.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                    downloadUri.UserInfo.Length != 0 || downloadUri.Query.Length != 0 || downloadUri.Fragment.Length != 0)
                    throw new InvalidDataException("Connector ZIP download URL is not trusted.");

                string expectedPath = RepositoryPath + tag + "/" + expectedName;
                if (!string.Equals(downloadUri.AbsolutePath, expectedPath, StringComparison.Ordinal))
                    throw new InvalidDataException("Connector ZIP URL does not match this repository and release.");

                return new ConnectorReleaseAsset(version, downloadUri,
                    digestMatch.Groups["digest"].Value.ToLowerInvariant(), size);
            }

            throw new InvalidDataException("No published connector release was found.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("GitHub release metadata is invalid.", error);
        }
        catch (KeyNotFoundException error)
        {
            throw new InvalidDataException("GitHub release metadata is incomplete.", error);
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidDataException("GitHub release metadata has an invalid field type.", error);
        }
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"GitHub release metadata is missing {name}.");
        return value.GetString() ?? throw new InvalidDataException($"GitHub release metadata is missing {name}.");
    }
}

internal static class ConnectorPackage
{
    internal const long MaximumArchiveBytes = 32L * 1024 * 1024;
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumDllBytes = 16 * 1024 * 1024;
    private const int MaximumThirdPartyLicenseBytes = 16 * 1024;
    private const string SdkLicensePath = "licenses/scs-sdk-mit.txt";
    private static readonly HashSet<string> AllowedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ets2ap_plugin.dll",
        "ets2ap_input.dll"
    };
    private static readonly Regex VersionPattern = new("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    public static IReadOnlyDictionary<string, byte[]> ReadValidated(byte[] archive, string expectedVersion)
    {
        using var stream = new MemoryStream(archive, writable: false);
        return ReadValidated(stream, expectedVersion);
    }

    public static IReadOnlyDictionary<string, byte[]> ReadValidated(Stream archive, string expectedVersion)
    {
        if (archive.Length is <= 0 or > MaximumArchiveBytes || !VersionPattern.IsMatch(expectedVersion))
            throw new InvalidDataException("Connector ZIP or version is outside the supported limits.");

        try
        {
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string path = entry.FullName;
                if (path.Contains('\\') || path.StartsWith("/", StringComparison.Ordinal) ||
                    path.Split('/').Any(part => part is ".." or ".") ||
                    !entries.TryAdd(path, entry))
                    throw new InvalidDataException("Connector ZIP contains an unsafe or duplicate path.");

                if (path is not "manifest.json" and not "plugins/" and
                    not "plugins/ets2ap_plugin.dll" and not "plugins/ets2ap_input.dll" and
                    not SdkLicensePath)
                    throw new InvalidDataException("Connector ZIP contains an unexpected file.");
            }

            if (!entries.TryGetValue("manifest.json", out ZipArchiveEntry? manifestEntry) ||
                manifestEntry.Length is <= 0 or > MaximumManifestBytes)
                throw new InvalidDataException("Connector ZIP manifest is missing or too large.");

            using JsonDocument manifest = JsonDocument.Parse(ReadEntry(manifestEntry, MaximumManifestBytes),
                new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = manifest.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("format", out JsonElement format) || format.GetInt32() != 1 ||
                !root.TryGetProperty("version", out JsonElement versionElement) || versionElement.GetString() != expectedVersion ||
                !root.TryGetProperty("files", out JsonElement filesElement) || filesElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Connector ZIP manifest is invalid or has the wrong version.");

            var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement file in filesElement.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object ||
                    !file.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String ||
                    !file.TryGetProperty("sha256", out JsonElement hashElement) || hashElement.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Connector ZIP manifest has an invalid file record.");

                string name = nameElement.GetString() ?? string.Empty;
                string hash = hashElement.GetString() ?? string.Empty;
                if (!AllowedNames.Contains(name) || !DigestPattern.IsMatch(hash) || !declared.TryAdd(name, hash))
                    throw new InvalidDataException("Connector ZIP manifest contains an invalid or duplicate DLL.");
            }

            bool hasSdkLicense = entries.TryGetValue(SdkLicensePath, out ZipArchiveEntry? sdkLicenseEntry);
            if (root.TryGetProperty("third_party_licenses", out JsonElement thirdPartyLicenses))
            {
                if (thirdPartyLicenses.ValueKind != JsonValueKind.Array || thirdPartyLicenses.GetArrayLength() != 1)
                    throw new InvalidDataException("Connector ZIP manifest has an invalid third-party license list.");
                JsonElement license = thirdPartyLicenses[0];
                if (license.ValueKind != JsonValueKind.Object ||
                    !license.TryGetProperty("path", out JsonElement licensePath) ||
                    licensePath.ValueKind != JsonValueKind.String || licensePath.GetString() != SdkLicensePath ||
                    !license.TryGetProperty("sha256", out JsonElement licenseHash) ||
                    licenseHash.ValueKind != JsonValueKind.String ||
                    !DigestPattern.IsMatch(licenseHash.GetString() ?? string.Empty) ||
                    !hasSdkLicense || sdkLicenseEntry is null ||
                    sdkLicenseEntry.Length is <= 0 or > MaximumThirdPartyLicenseBytes)
                    throw new InvalidDataException("Connector ZIP third-party license record is invalid.");

                byte[] sdkLicenseBytes = ReadEntry(sdkLicenseEntry, MaximumThirdPartyLicenseBytes);
                string actualLicenseHash = Convert.ToHexString(SHA256.HashData(sdkLicenseBytes));
                if (!string.Equals(actualLicenseHash, licenseHash.GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Connector ZIP third-party license digest does not match its manifest.");
            }
            else if (hasSdkLicense)
            {
                throw new InvalidDataException("Connector ZIP contains an undeclared third-party license file.");
            }

            if (!declared.ContainsKey("ets2ap_plugin.dll") || declared.Count is < 1 or > 2 ||
                entries.Keys.Count(path => path.StartsWith("plugins/ets2ap_", StringComparison.Ordinal)) != declared.Count)
                throw new InvalidDataException("Connector ZIP must declare the main plugin and only supported DLLs.");

            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach ((string name, string expectedHash) in declared)
            {
                string entryPath = "plugins/" + name;
                if (!entries.TryGetValue(entryPath, out ZipArchiveEntry? entry) ||
                    entry.Length is < 2 or > MaximumDllBytes)
                    throw new InvalidDataException("Connector ZIP is missing a supported DLL or it is too large.");

                byte[] bytes = ReadEntry(entry, MaximumDllBytes);
                if (bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                    throw new InvalidDataException("Connector package entry is not a Windows DLL image.");

                string actualHash = Convert.ToHexString(SHA256.HashData(bytes));
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Connector DLL does not match its manifest digest.");
                result.Add(name, bytes);
            }

            return result;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Connector ZIP manifest is not valid JSON.", error);
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidDataException("Connector ZIP manifest has an invalid field type.", error);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("Connector ZIP is malformed.", error);
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, int maximumBytes)
    {
        using Stream source = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        byte[] buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Connector ZIP entry exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        if (output.Length != entry.Length)
            throw new InvalidDataException("Connector ZIP entry has an inconsistent length.");
        return output.ToArray();
    }
}
