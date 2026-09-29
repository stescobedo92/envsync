using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EnvSync.Application.Abstractions;
using EnvSync.Application.Diagnostics;
using EnvSync.Domain;

namespace EnvSync.Providers.Vault;

/// <summary>
/// Reads one field of a KV v1/v2 secret over Vault's HTTP API. It speaks HTTP directly (a single GET) instead of pulling
/// in a client library, which keeps it free of reflection and Native AOT-safe. Redirects are never followed: the token is a
/// custom header that the framework would forward to whatever host the redirect names. Response bodies are read with a hard
/// size limit, and no failure while reading one can escape as an exception.
/// </summary>
public sealed class VaultSecretProvider : ISecretProvider, IDisposable
{
    /// <summary>No KV secret is anywhere near this size; a bigger reply is a misbehaving or hostile endpoint.</summary>
    private const long MaxBodyBytes = 1024 * 1024;

    private const int MaxDetailLength = 300;

    private readonly HttpClient _http;
    private readonly VaultOptions _options;
    private readonly Uri _baseAddress;

    public VaultSecretProvider(HttpClient http, VaultOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        _http = http;
        _options = options;
        _baseAddress = options.Address.AbsoluteUri.EndsWith('/') ? options.Address : new Uri(options.Address.AbsoluteUri + "/");
    }

    public async ValueTask<Result<SecretValue>> GetAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        if (reference.Field is null)
        {
            return Fail(reference, ErrorKind.FieldRequired, "A Vault secret holds several fields: write the reference as 'path#field'.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(reference.Path));
        request.Headers.Add("X-Vault-Token", _options.Token.Reveal());
        if (_options.Namespace is { Length: > 0 } vaultNamespace)
        {
            request.Headers.Add("X-Vault-Namespace", vaultNamespace);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            // The framework's own message often ends "see inner exception", and the inner one (bad certificate, wrong host name,
            // unsupported protocol) is the part that says what to fix.
            var inner = exception.InnerException is { } cause ? $" ({ErrorText.Summarize(cause.Message, MaxDetailLength)})" : string.Empty;
            return Fail(
                reference,
                ErrorKind.ProviderUnavailable,
                $"Could not reach Vault at {_options.Address.Host}: {ErrorText.Summarize(exception.Message, MaxDetailLength)}{inner}");
        }

        using (response)
        {
            return await ReadAsync(response, reference, cancellationToken);
        }
    }

    public void Dispose() => _http.Dispose();

    private Uri BuildUri(string path)
    {
        var relative = new StringBuilder("v1");

        AppendSegments(relative, _options.Mount);
        if (_options.KvVersion == 2)
        {
            relative.Append("/data");
        }

        AppendSegments(relative, path);
        return new Uri(_baseAddress, relative.ToString());
    }

    private static void AppendSegments(StringBuilder builder, string path)
    {
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append('/').Append(Uri.EscapeDataString(segment));
        }
    }

    private async ValueTask<Result<SecretValue>> ReadAsync(HttpResponseMessage response, SecretReference reference, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.OK)
        {
            return await ParseAsync(response, reference, cancellationToken);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // An empty "errors" list means no such secret. A non-empty one ("no handler for route ...") means the route itself is
            // wrong, which is a mount or kv-version mistake and must not be mistaken for a missing key.
            var routeProblem = await ReadReasonAsync(response, cancellationToken);
            return routeProblem.Length > 0
                ? Fail(reference, ErrorKind.ProviderMisconfigured, $"Vault says: {routeProblem}. Check the 'mount' and 'kv' settings.")
                : Fail(reference, ErrorKind.SecretNotFound, $"Secret '{reference.Path}' was not found under mount '{_options.Mount}'.");
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            var reason = await ReadReasonAsync(response, cancellationToken);
            return Fail(
                reference,
                ErrorKind.AuthenticationFailed,
                $"Vault denied the request (HTTP {status}){(reason.Length > 0 ? " - " + reason : string.Empty)}: the token is invalid, expired, or has no access to '{reference.Path}'.");
        }

        if (status is >= 300 and < 400)
        {
            var target = response.Headers.Location is { } location
                ? (location.IsAbsoluteUri ? location.Host : location.OriginalString)
                : "another address";
            return Fail(
                reference,
                ErrorKind.ProviderUnavailable,
                $"Vault redirected the request to '{ErrorText.Summarize(target, MaxDetailLength)}' (HTTP {status}). Redirects are not followed so the token is never sent to another host; set 'address' to that node.");
        }

        var explanation = await ReadReasonAsync(response, cancellationToken);
        return Fail(reference, ErrorKind.ProviderUnavailable, $"Vault answered HTTP {status}{(explanation.Length > 0 ? " - " + explanation : string.Empty)}.");
    }

    private async ValueTask<Result<SecretValue>> ParseAsync(HttpResponseMessage response, SecretReference reference, CancellationToken cancellationToken)
    {
        try
        {
            await response.Content.LoadIntoBufferAsync(MaxBodyBytes, cancellationToken);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

            if (!TryGetObject(document.RootElement, "data", out var data))
            {
                return Unexpected(reference);
            }

            if (_options.KvVersion == 2)
            {
                if (data.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Null)
                {
                    return Fail(reference, ErrorKind.SecretNotFound, $"The current version of '{reference.Path}' was deleted.");
                }

                if (!TryGetObject(data, "data", out data))
                {
                    return Unexpected(reference);
                }
            }

            return Extract(data, reference);
        }
        catch (JsonException)
        {
            return Unexpected(reference);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Fail(
                reference,
                ErrorKind.ProviderUnavailable,
                $"The response from Vault could not be read: {ErrorText.Summarize(exception.InnerException?.Message ?? exception.Message, MaxDetailLength)}");
        }
    }

    private static Result<SecretValue> Extract(JsonElement data, SecretReference reference)
    {
        var field = reference.Field!;

        if (!data.TryGetProperty(field, out var value))
        {
            return Fail(reference, ErrorKind.FieldNotFound, $"The secret has no field '{field}'.");
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => Result<SecretValue>.Success(new SecretValue(value.GetString()!)),
            JsonValueKind.Number => Result<SecretValue>.Success(new SecretValue(value.GetRawText())),
            JsonValueKind.True => Result<SecretValue>.Success(new SecretValue("true")),
            JsonValueKind.False => Result<SecretValue>.Success(new SecretValue("false")),
            _ => Fail(reference, ErrorKind.UnsupportedSecretType, $"The field '{field}' is not a scalar value (string, number or boolean)."),
        };
    }

    private static bool TryGetObject(JsonElement parent, string name, out JsonElement child)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out child)
            && child.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        child = default;
        return false;
    }

    /// <summary>
    /// Vault explains failures as <c>{"errors":["..."]}</c>. That never holds secret values, so it is kept, sanitized and bounded.
    /// Returns an empty string when there is nothing to say, including when the body itself cannot be read: a failure while
    /// explaining a failure must not change what the failure was.
    /// </summary>
    private static async ValueTask<string> ReadReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await response.Content.LoadIntoBufferAsync(MaxBodyBytes, cancellationToken);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("errors", out var errors)
                || errors.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var reasons = errors.EnumerateArray()
                .Where(static e => e.ValueKind == JsonValueKind.String)
                .Select(static e => e.GetString()!)
                .ToArray();

            return reasons.Length == 0 ? string.Empty : ErrorText.Summarize(string.Join("; ", reasons), MaxDetailLength);
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or IOException)
        {
            return string.Empty;
        }
    }

    private static Result<SecretValue> Unexpected(SecretReference reference) =>
        Fail(reference, ErrorKind.ProviderUnavailable, "Vault returned a response that does not look like a KV secret; check 'kv' and 'mount'.");

    private static Result<SecretValue> Fail(SecretReference reference, ErrorKind kind, string detail) =>
        Result<SecretValue>.Failure(new Error(kind, reference.Path, detail));
}
