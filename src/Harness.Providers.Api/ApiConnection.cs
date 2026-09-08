using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Harness.Providers.Api;

public enum ApiProtocol { Responses, Anthropic, Gemini, ChatCompletions }

// These are API contracts, not lists of model versions or reasoning levels.
public sealed record ApiProviderDefinition(string Id, string Name, string Endpoint, ApiProtocol Protocol, bool KeyOptional = false)
{
    public static IReadOnlyList<ApiProviderDefinition> All { get; } =
    [
        new("openai-api", "OpenAI API", "https://api.openai.com/v1/", ApiProtocol.Responses),
        new("anthropic-api", "Anthropic API", "https://api.anthropic.com/v1/", ApiProtocol.Anthropic),
        new("gemini-api", "Google Gemini API", "https://generativelanguage.googleapis.com/v1beta/", ApiProtocol.Gemini),
        new("xai-api", "xAI / Grok API", "https://api.x.ai/v1/", ApiProtocol.Responses),
        new("mistral-api", "Mistral API", "https://api.mistral.ai/v1/", ApiProtocol.ChatCompletions),
        new("deepseek-api", "DeepSeek API", "https://api.deepseek.com/", ApiProtocol.ChatCompletions),
        new("openrouter-api", "OpenRouter", "https://openrouter.ai/api/v1/", ApiProtocol.ChatCompletions),
        new("ollama-local", "Ollama (local)", "http://localhost:11434/v1/", ApiProtocol.ChatCompletions, true),
        new("llama-cpp-local", "llama.cpp (local)", "http://localhost:8080/v1/", ApiProtocol.ChatCompletions, true),
        new("local-api", "Local / OpenAI-compatible", "http://localhost:1234/v1/", ApiProtocol.ChatCompletions, true)
    ];
    public override string ToString() => Name;
}

public sealed record ApiConnection(string Id, string ProviderId, string Name, string Endpoint)
{
    public ApiProviderDefinition Definition => ApiProviderDefinition.All.Single(provider => provider.Id == ProviderId);
    public Uri BaseUri
    {
        get
        {
            var uri = new Uri(Endpoint.TrimEnd('/') + '/', UriKind.Absolute);
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
                || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
                throw new InvalidOperationException("Use HTTPS, or HTTP on localhost. URLs cannot contain credentials, query strings, or fragments.");
            // Prevent mistakenly forwarding a hosted provider's credential to an arbitrary endpoint.
            if (!Definition.KeyOptional && uri != new Uri(Definition.Endpoint))
                throw new InvalidOperationException("Hosted providers use their official API endpoint. Choose a compatible connection for a custom endpoint.");
            return uri;
        }
    }
    public override string ToString() => Name;
}

public sealed class ApiTransport : IDisposable
{
    private readonly HttpClient _http;
    private readonly ApiConnection _connection;
    private readonly string _key;

    public ApiTransport(ApiConnection connection, string key, HttpMessageHandler? handler = null)
    {
        _connection = connection;
        _key = key;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = connection.BaseUri, Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<HttpResponseMessage> SendAsync(string path, JsonNode? body, CancellationToken cancellationToken,
        IReadOnlyList<string>? betaFeatures = null)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        switch (_connection.Definition.Protocol)
        {
            case ApiProtocol.Anthropic:
                request.Headers.Add("x-api-key", _key);
                request.Headers.Add("anthropic-version", "2023-06-01");
                if (betaFeatures is { Count: > 0 }) request.Headers.Add("anthropic-beta", string.Join(',', betaFeatures));
                break;
            case ApiProtocol.Gemini:
                request.Headers.Add("x-goog-api-key", _key);
                break;
            default:
                if (!string.IsNullOrWhiteSpace(_key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
                break;
        }
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;
        // Error bodies can echo prompts, credentials, or arbitrary proxy HTML. Never log them.
        var status = response.StatusCode;
        var retry = response.Headers.RetryAfter?.ToString();
        response.Dispose();
        throw new InvalidOperationException($"{_connection.Name}: HTTP {(int)status}. " + (status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Check this connection's API key and model permissions in Settings → Providers.",
            HttpStatusCode.TooManyRequests => $"API quota or rate limit reached.{(retry is null ? "" : $" Retry after {retry}.")}",
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "The provider rejected the request. This model may not support this API, attachments, tools, or selected options. No automatic retry was made.",
            HttpStatusCode.NotFound => "The endpoint or model is unavailable to this connection.",
            _ => "The provider request failed. No automatic retry was made."
        }));
    }

    public async Task<JsonObject> GetAsync(string path, CancellationToken cancellationToken,
        IReadOnlyList<string>? betaFeatures = null)
    {
        using var response = await SendAsync(path, null, cancellationToken, betaFeatures).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonObject> PostJsonAsync(string path, JsonNode body, CancellationToken cancellationToken,
        IReadOnlyList<string>? betaFeatures = null)
    {
        using var response = await SendAsync(path, body, cancellationToken, betaFeatures).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(string? MediaType, long ByteLength)> DownloadToFileAsync(
        string path,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? betaFeatures = null)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var response = await SendAsync(path, null, cancellationToken, betaFeatures).ConfigureAwait(false);
        var reported = response.Content.Headers.ContentLength;
        if (reported.HasValue && reported.Value > maximumBytes)
            throw new InvalidOperationException($"Provider artifact exceeds Harness's {maximumBytes / 1024 / 1024} MiB download limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maximumBytes)
                throw new InvalidOperationException($"Provider artifact exceeds Harness's {maximumBytes / 1024 / 1024} MiB download limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (response.Content.Headers.ContentType?.MediaType, total);
    }

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return (await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidOperationException("The provider returned an empty catalog.");
    }
    public void Dispose() => _http.Dispose();
}
