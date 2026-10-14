using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Oidc.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Oidc.Authentication;

/// <summary>Imports Pocket ID profile pictures into Jellyfin under a bounded, same-issuer policy.</summary>
public sealed class AvatarSyncService
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumDimension = 4096;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProviderManager _providerManager;
    private readonly IUserManager _userManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly IServerApplicationPaths _applicationPaths;

    /// <summary>Initializes a new instance of the <see cref="AvatarSyncService"/> class.</summary>
    public AvatarSyncService(
        IHttpClientFactory httpClientFactory,
        IProviderManager providerManager,
        IUserManager userManager,
        IImageProcessor imageProcessor,
        IServerApplicationPaths applicationPaths)
    {
        _httpClientFactory = httpClientFactory;
        _providerManager = providerManager;
        _userManager = userManager;
        _imageProcessor = imageProcessor;
        _applicationPaths = applicationPaths;
    }

    /// <summary>Imports an avatar when configured and allowed by the profile ownership mode.</summary>
    public async Task ImportIfConfiguredAsync(
        PluginConfiguration configuration,
        VerifiedOidcIdentity identity,
        User user,
        CancellationToken cancellationToken)
    {
        var mode = configuration.AvatarSyncMode;
        if (string.IsNullOrWhiteSpace(identity.PictureUrl)
            || (mode != "ImportOnce" && mode != "AlwaysUpdate")
            || (mode == "ImportOnce" && user.ProfileImage is not null))
        {
            return;
        }

        var issuer = new Uri(identity.Issuer);
        if (!Uri.TryCreate(identity.PictureUrl, UriKind.Absolute, out var imageUri)
            || imageUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(imageUri.IdnHost, issuer.IdnHost, StringComparison.OrdinalIgnoreCase)
            || imageUri.Port != issuer.Port
            || !string.IsNullOrEmpty(imageUri.UserInfo))
        {
            return;
        }

        var (imageBytes, mimeType, extension) = await DownloadImageAsync(imageUri, issuer, cancellationToken).ConfigureAwait(false);
        if (!TryReadImageDimensions(imageBytes, mimeType, out var width, out var height)
            || width is <= 0 or > MaximumDimension
            || height is <= 0 or > MaximumDimension)
        {
            return;
        }

        var userDirectory = Path.Combine(_applicationPaths.UserConfigurationDirectoryPath, user.Id.ToString("N"));
        Directory.CreateDirectory(userDirectory);
        var targetPath = Path.Combine(userDirectory, "oidc-profile-" + Guid.NewGuid().ToString("N") + extension);
        var previousPath = user.ProfileImage?.Path;
        await using (var stream = new MemoryStream(imageBytes, writable: false))
        {
            await _providerManager.SaveImage(stream, mimeType, targetPath).ConfigureAwait(false);
        }

        try
        {
            // Ask Jellyfin's own image decoder to reject corrupt or unsupported image payloads.
            _ = _imageProcessor.GetImageDimensions(targetPath);
            user.ProfileImage = new ImageInfo(targetPath);
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(targetPath);
            throw;
        }

        if (previousPath is not null && !string.Equals(previousPath, targetPath, StringComparison.Ordinal))
        {
            var normalizedRoot = Path.GetFullPath(_applicationPaths.UserConfigurationDirectoryPath) + Path.DirectorySeparatorChar;
            var normalizedOld = Path.GetFullPath(previousPath);
            if (normalizedOld.StartsWith(normalizedRoot, StringComparison.Ordinal)) TryDelete(normalizedOld);
        }
    }

    private async Task<(byte[] Bytes, string MimeType, string Extension)> DownloadImageAsync(Uri uri, Uri issuer, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("OidcAvatar");
        for (var redirects = 0; redirects <= 2; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(uri.IdnHost, issuer.IdnHost, StringComparison.OrdinalIgnoreCase)
                || uri.Port != issuer.Port)
            {
                throw new InvalidOperationException("The profile image redirect left the Pocket ID issuer host.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (redirects == 2 || response.Headers.Location is null)
                {
                    throw new InvalidOperationException("The profile image redirected too many times.");
                }

                uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumBytes)
            {
                throw new InvalidOperationException("The profile image exceeds the 2 MiB limit.");
            }

            var mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            var (extension, expectedMime) = mime switch
            {
                "image/png" => (".png", "image/png"),
                "image/jpeg" => (".jpg", "image/jpeg"),
                _ => (string.Empty, string.Empty)
            };
            if (extension.Length == 0) throw new InvalidOperationException("Pocket ID returned an unsupported profile image type.");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumBytes) throw new InvalidOperationException("The profile image exceeds the 2 MiB limit.");
                buffer.Write(chunk, 0, read);
            }

            return (buffer.ToArray(), expectedMime, extension);
        }

        throw new InvalidOperationException("The profile image could not be downloaded.");
    }

    private static bool TryReadImageDimensions(byte[] bytes, string mimeType, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (mimeType == "image/png" && bytes.Length >= 24
            && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)));
            height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)));
            return true;
        }

        if (mimeType != "image/jpeg" || bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;
        var offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset++] != 0xff) return false;
            var marker = bytes[offset++];
            while (marker == 0xff && offset < bytes.Length) marker = bytes[offset++];
            if (marker is 0xd8 or 0xd9 or 0x01 or >= 0xd0 and <= 0xd7) continue;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
            if (length < 2 || offset + length > bytes.Length) return false;
            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
            {
                if (length < 7) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2));
                return true;
            }
            offset += length;
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
