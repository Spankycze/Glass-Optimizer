using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

internal static class RuntimeDownloader
{
    internal const int MaxRedirects = 5;

    internal static bool SafeHttps(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0;

    internal static int ValidateLength(long count, long? expected, long limit)
    {
        return count < 0 || count > limit || expected is < 0 ||
               expected > limit || (expected.HasValue && count > expected.Value) ? 31 : 0;
    }

    internal static async Task<int> DownloadAsync(HttpClient client, Uri sourceUrl,
        string destination, ReadOnlyMemory<byte> sha512, long limit,
        Action<long, long?>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (!SafeHttps(sourceUrl)) return 30;
        if (!Path.IsPathFullyQualified(destination) ||
            !destination.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            sha512.Length != 64 || limit < 1) return 34;

        string? partial = null;
        try
        {
            var current = sourceUrl;
            for (int redirect = 0; redirect <= MaxRedirects; redirect++)
            {
                using var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    Uri? next = response.Headers.Location;
                    if (redirect == MaxRedirects || next is null) return 30;
                    if (!next.IsAbsoluteUri) next = new Uri(current, next);
                    if (!SafeHttps(next)) return 30;
                    current = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode) return 33;

                long? expected = response.Content.Headers.ContentLength;
                if (ValidateLength(0, expected, limit) != 0) return 31;
                onProgress?.Invoke(0, expected);
                partial = destination + "." + Guid.NewGuid().ToString("N") + ".part";
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                long count = 0;
                long lastNotified = 0;
                await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = new FileStream(partial, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, bufferSize: 65536))
                {
                    var buffer = new byte[65536];
                    int n;
                    while ((n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        count = checked(count + n);
                        if (ValidateLength(count, expected, limit) != 0) return 31;
                        digest.AppendData(buffer.AsSpan(0, n));
                        await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                        if (count - lastNotified >= 512 * 1024 || (expected.HasValue && count == expected.Value))
                        {
                            onProgress?.Invoke(count, expected);
                            lastNotified = count;
                        }
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                if (lastNotified != count) onProgress?.Invoke(count, expected);
                if (expected.HasValue && count != expected.Value) return 31;
                if (!CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), sha512.Span))
                    return 32;
                File.Move(partial, destination, overwrite: false);
                partial = null;
                return 0;
            }
            return 30;
        }
        catch (HttpRequestException) { return 33; }
        catch (OperationCanceledException) { return cancellationToken.IsCancellationRequested ? 35 : 33; }
        catch (IOException) { return 34; }
        catch (UnauthorizedAccessException) { return 34; }
        catch (OverflowException) { return 31; }
        finally
        {
            if (partial is not null)
            {
                try { File.Delete(partial); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
