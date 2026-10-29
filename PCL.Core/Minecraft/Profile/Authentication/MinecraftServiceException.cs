using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Profile.Authentication;

public enum MinecraftServiceOperation
{
    LoginWithXbox,
    Entitlements,
    Profile
}

/// <summary>Safe diagnostic details returned by Minecraft Services, without authentication payloads.</summary>
public sealed class MinecraftServiceException : HttpRequestException
{
    private const int MaxErrorBodyBytes = 16 * 1024;

    public MinecraftServiceOperation Operation { get; }
    public string Endpoint { get; }
    public string? ServiceError { get; }
    public string? ServiceMessage { get; }
    public string? ServicePath { get; }
    public string? RequestId { get; }
    public string? ResponseContentType { get; }
    public string? ClientId { get; }
    public bool ErrorBodyTruncated { get; }
    public bool IsAppRegistrationRejected { get; }

    private MinecraftServiceException(HttpStatusCode status, MinecraftServiceOperation operation,
        string? error, string? message, string? path, string? requestId, string? contentType,
        string? clientId, bool truncated)
        : base(BuildMessage(status, operation, error, message, path, requestId, contentType, clientId, truncated), null, status)
    {
        Operation = operation;
        Endpoint = GetEndpoint(operation);
        ServiceError = error;
        ServiceMessage = message;
        ServicePath = path;
        RequestId = requestId;
        ResponseContentType = contentType;
        ClientId = clientId;
        ErrorBodyTruncated = truncated;
        IsAppRegistrationRejected = status == HttpStatusCode.Forbidden && operation == MinecraftServiceOperation.LoginWithXbox &&
            (IndicatesInvalidRegistration(error) || IndicatesInvalidRegistration(message));
    }

    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, MinecraftServiceOperation operation,
        CancellationToken token, string? clientId = null)
    {
        if (response.IsSuccessStatusCode) return;
        throw await FromResponseAsync(response, operation, token, clientId).ConfigureAwait(false);
    }

    internal static async Task<MinecraftServiceException> FromResponseAsync(HttpResponseMessage response,
        MinecraftServiceOperation operation, CancellationToken token, string? clientId = null)
    {
        token.ThrowIfCancellationRequested();
        string? error = null, message = null, path = null;
        var contentType = Sanitize(response.Content.Headers.ContentType?.MediaType, 120);
        var requestId = ReadRequestId(response);
        var truncated = false;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[MaxErrorBodyBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false);
                if (count == 0) break;
                total += count;
            }
            truncated = total > MaxErrorBodyBytes;
            if (!truncated && total > 0)
            {
                using var json = JsonDocument.Parse(buffer.AsMemory(0, total), new JsonDocumentOptions { MaxDepth = 32 });
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var nestedError = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object
                        ? nested : default;
                    error = Sanitize(ReadString(root, "error") ?? ReadString(nestedError, "code"), 128);
                    var descriptions = new[]
                    {
                        ReadString(root, "errorMessage"), ReadString(root, "message"),
                        ReadString(root, "developerMessage"), ReadString(nestedError, "message")
                    }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal);
                    message = Sanitize(string.Join(" | ", descriptions), 2048);
                    path = SanitizePath(ReadString(root, "path"));
                    requestId ??= SafeGuid(ReadString(root, "requestId") ?? ReadString(root, "traceId"));
                }
            }
        }
        catch (JsonException) { /* HTML and malformed responses retain only status and response metadata. */ }
        catch (IOException) { /* Preserve the HTTP failure even if reading its diagnostic body fails. */ }

        return new MinecraftServiceException(response.StatusCode, operation, error, message, path, requestId,
            contentType, SafeGuid(clientId), truncated);
    }

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? SafeGuid(string? value) => Guid.TryParse(value, out var guid) ? guid.ToString() : null;

    private static string? ReadRequestId(HttpResponseMessage response)
    {
        foreach (var header in new[] { "x-request-id", "request-id", "x-ms-request-id", "x-correlation-id" })
        {
            if (!response.Headers.TryGetValues(header, out var values)) continue;
            foreach (var value in values)
                if (SafeGuid(value) is { } id) return id;
        }
        return null;
    }

    private static string? SanitizePath(string? value)
    {
        if (value is null || !value.StartsWith('/')) return null;
        return Sanitize(value.Split('?', '#')[0], 256);
    }

    private static string? Sanitize(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = Regex.Replace(value, @"(?i)\b(?:Bearer\s+|XBL3\.0\s+x=)[^\s""<>]+", "[redacted]");
        value = Regex.Replace(value, @"\beyJ[A-Za-z0-9_-]{6,}(?:\.[A-Za-z0-9_-]*){2,}", "[redacted]");
        value = Regex.Replace(value,
            @"(?i)(\b(?:access_token|refresh_token|device_code|user_code|identityToken|RpsTicket|client_secret|authorization|password|token)\b[""']?\s*[:=]\s*[""']?)[^\s""',}]+",
            "$1[redacted]");
        value = string.Concat(value.Select(character => char.IsControl(character) ? ' ' : character)).Trim();
        return value.Length <= limit ? value : value[..limit] + "…";
    }

    private static bool IndicatesInvalidRegistration(string? value)
    {
        if (value is null) return false;
        var normalized = Regex.Replace(value, "[^a-zA-Z]", "").ToLowerInvariant();
        return normalized.Contains("invalidappregistration", StringComparison.Ordinal) ||
               normalized.Contains("invalidapplicationregistration", StringComparison.Ordinal);
    }

    private static string GetEndpoint(MinecraftServiceOperation operation) => "https://api.minecraftservices.com/" + (operation switch
    {
        MinecraftServiceOperation.LoginWithXbox => "authentication/login_with_xbox",
        MinecraftServiceOperation.Entitlements => "entitlements/mcstore",
        MinecraftServiceOperation.Profile => "minecraft/profile",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    });

    private static string BuildMessage(HttpStatusCode status, MinecraftServiceOperation operation,
        string? error, string? message, string? path, string? requestId, string? contentType, string? clientId, bool truncated)
    {
        var result = new StringBuilder($"Minecraft {operation} failed: HTTP {(int)status} ({status}).");
        result.AppendLine().Append("Endpoint: ").Append(GetEndpoint(operation));
        if (error is not null) result.AppendLine().Append("Service error: ").Append(error);
        if (message is not null) result.AppendLine().Append("Service message: ").Append(message);
        if (path is not null) result.AppendLine().Append("Service path: ").Append(path);
        if (requestId is not null) result.AppendLine().Append("Request ID: ").Append(requestId);
        if (clientId is not null) result.AppendLine().Append("Client ID: ").Append(clientId);
        if (contentType is not null) result.AppendLine().Append("Response type: ").Append(contentType);
        if (truncated) result.AppendLine().Append("Error response exceeded the diagnostic size limit.");
        else if (error is null && message is null) result.AppendLine().Append("No structured JSON error message was returned.");
        return result.ToString();
    }
}
