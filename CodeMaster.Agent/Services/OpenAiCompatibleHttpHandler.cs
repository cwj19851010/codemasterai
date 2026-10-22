using System.Collections.Generic;
using System.Text.Json;
using System.Net;
using System.Text;
using CodeMaster.Agent.Contracts;

namespace CodeMaster.Agent.Services;

internal sealed class OpenAiCompatibleHttpHandler : HttpMessageHandler
{
    private const string DefaultUserAgent = "CodeMaster/1.0";

    /// <summary>
    /// Shared transport. The proxy is intentionally left unset so the platform default applies
    /// (environment variables, then the operating system proxy). A system-level proxy such as
    /// a TUN device is handled by the operating system and needs nothing here.
    /// </summary>
    private static readonly HttpMessageInvoker SharedInvoker = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
    });

    private readonly IReadOnlyDictionary<string, string> _extraHeaders;
    private readonly string _compatibilityMode;
    private readonly bool _isQwenCompatible;

    public OpenAiCompatibleHttpHandler(
        string? extraHeadersJson,
        string? compatibilityMode = null,
        string? baseUrl = null,
        string? modelName = null)
    {
        _extraHeaders = ParseExtraHeaders(extraHeadersJson);
        _compatibilityMode = compatibilityMode ?? AiProviderCompatibilityModes.Standard;
        _isQwenCompatible = IsQwenCompatible(baseUrl, modelName);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ApplyHeaders(request, _extraHeaders);
        if (string.Equals(_compatibilityMode, AiProviderCompatibilityModes.OpenRouterReasoning, StringComparison.OrdinalIgnoreCase) &&
            request.Content is not null &&
            request.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.Content = new OpenRouterReasoningHttpContent(request.Content);
        }
        if (_isQwenCompatible &&
            request.Content is not null &&
            request.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.Content = new ToolCallArgumentsSanitizingHttpContent(
                new QwenForcedToolHttpContent(request.Content));
        }
        // Bound streaming responses with an idle timeout. Microsoft.Extensions.AI does not always
        // enforce NetworkTimeout on streaming bodies, so the inner stream can stay open forever.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCts.CancelAfter(AiChatClientFactory.StreamIdleTimeout);
        try
        {
            var response = await SharedInvoker.SendAsync(request, idleCts.Token);
            if (response.Content is not null && IsOpenAiResponse(response.Content.Headers.ContentType?.MediaType))
            {
                // Outermost wrapper: scrub malformed tool_call.arguments in the response body
                // before any inner wrapper reads the stream.
                response.Content = new ToolCallArgumentsSanitizingHttpContent(response.Content);
                if (response.Content is not null)
                    response.Content = new FinishReasonNormalizingHttpContent(response.Content);
            }
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Translate the idle timeout into the same exception type the framework already
            // expects for rate-limit / transient failures so its fallback path runs.
            throw new TimeoutException(
                $"Streaming response exceeded the {AiChatClientFactory.StreamIdleTimeout.TotalSeconds:N0}s idle timeout.");
        }
    }

    private static bool IsOpenAiResponse(string? mediaType)
        => mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ||
           string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

    internal static bool IsQwenCompatible(string? baseUrl, string? modelName)
        => (baseUrl?.Contains("qianwen", StringComparison.OrdinalIgnoreCase) == true) ||
           (baseUrl?.Contains("aliyuncs", StringComparison.OrdinalIgnoreCase) == true) ||
           (modelName?.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) == true);

    internal sealed class QwenForcedToolHttpContent : HttpContent
    {
        private readonly HttpContent _inner;

        public QwenForcedToolHttpContent(HttpContent inner)
        {
            _inner = inner;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            Headers.Remove("Content-Length");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var body = await _inner.ReadAsStringAsync();
            var transformed = DisableThinkingForQwen(body);
            var bytes = Encoding.UTF8.GetBytes(transformed);
            await stream.WriteAsync(bytes);
        }

        internal static string DisableThinkingForQwen(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return body;

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return body;

                using var output = new MemoryStream();
                using (var writer = new Utf8JsonWriter(output))
                {
                    writer.WriteStartObject();
                    var wroteThinking = false;
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (string.Equals(property.Name, "enable_thinking", StringComparison.OrdinalIgnoreCase))
                        {
                            writer.WriteBoolean(property.Name, false);
                            wroteThinking = true;
                        }
                        else
                        {
                            property.WriteTo(writer);
                        }
                    }

                    if (!wroteThinking)
                        writer.WriteBoolean("enable_thinking", false);
                    writer.WriteEndObject();
                }

                return Encoding.UTF8.GetString(output.ToArray());
            }
            catch (JsonException)
            {
                return body;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Several OpenAI-compatible gateways emit provider-specific values such as
    /// finish_reason="error". Microsoft.Extensions.AI intentionally accepts only
    /// the OpenAI finish-reason vocabulary and throws before CodeMaster can run
    /// its normal empty-response recovery. Normalize only unknown values while
    /// preserving all standard reasons and the remainder of the provider body.
    /// </summary>
    internal sealed class FinishReasonNormalizingHttpContent : HttpContent
    {
        private static readonly HashSet<string> SupportedFinishReasons = new(StringComparer.OrdinalIgnoreCase)
        {
            "stop",
            "length",
            "tool_calls",
            "content_filter",
            "function_call"
        };

        private readonly HttpContent _inner;

        public FinishReasonNormalizingHttpContent(HttpContent inner)
        {
            _inner = inner;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            Headers.Remove("Content-Length");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var body = await _inner.ReadAsStringAsync();
            var normalized = NormalizeFinishReasons(body);
            var bytes = Encoding.UTF8.GetBytes(normalized);
            await stream.WriteAsync(bytes);
        }

        internal static string NormalizeFinishReasons(string body)
        {
            if (string.IsNullOrEmpty(body))
                return body;

            var builder = new StringBuilder(body);
            var searchStart = 0;
            while (searchStart < builder.Length)
            {
                var propertyStart = builder.ToString().IndexOf("\"finish_reason\"", searchStart, StringComparison.OrdinalIgnoreCase);
                if (propertyStart < 0)
                    break;

                var colon = builder.ToString().IndexOf(':', propertyStart + 15);
                if (colon < 0)
                    break;

                var valueStart = colon + 1;
                while (valueStart < builder.Length && char.IsWhiteSpace(builder[valueStart]))
                    valueStart++;
                if (valueStart >= builder.Length || builder[valueStart] != '\"')
                {
                    searchStart = colon + 1;
                    continue;
                }

                valueStart++;
                var valueEnd = builder.ToString().IndexOf('\"', valueStart);
                if (valueEnd < 0)
                    break;

                var value = builder.ToString(valueStart, valueEnd - valueStart);
                if (!SupportedFinishReasons.Contains(value))
                    builder.Remove(valueStart, valueEnd - valueStart).Insert(valueStart, "stop");
                searchStart = valueStart + 4;
            }

            return builder.ToString();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Some upstream OpenAI-compatible models (notably qwen3.8-flash) emit tool calls whose
    /// <c>function.arguments</c> string contains prose, markdown fences, or partial JSON instead
    /// of strict JSON. Re-emitting the assistant message verbatim causes the next request to be
    /// rejected by the provider with <c>function.arguments ... must be in JSON format</c>.
    /// This wrapper scans each SSE event (and the non-streamed JSON body), extracts the first
    /// complete JSON value from every <c>tool_calls[i].function.arguments</c> string, and
    /// replaces it. If extraction fails the tool call is dropped to keep the conversation valid.
    /// </summary>
    internal sealed class ToolCallArgumentsSanitizingHttpContent : HttpContent
    {
        private readonly HttpContent _inner;

        public ToolCallArgumentsSanitizingHttpContent(HttpContent inner)
        {
            _inner = inner;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            Headers.Remove("Content-Length");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var body = await _inner.ReadAsStringAsync();
            var scrubbed = ToolCallArgumentsScrubber.Scrub(body);
            WriteBodyDump(scrubbed);
            var bytes = Encoding.UTF8.GetBytes(scrubbed);
            await stream.WriteAsync(bytes);
        }

        /// <summary>
        /// Opt-in diagnostic. Set CODEMASTER_AI_BODY_DUMP to a file path to capture every scrubbed
        /// request/response body, which is the only way to see what a provider actually rejected.
        /// </summary>
        private static void WriteBodyDump(string scrubbed)
        {
            var path = Environment.GetEnvironmentVariable("CODEMASTER_AI_BODY_DUMP");
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var slice = scrubbed.Length > 200000 ? scrubbed[..200000] : scrubbed;
                File.AppendAllText(path, "=== " + DateTime.UtcNow.ToString("o") + " ===\n" + slice + "\n");
            }
            catch
            {
                // Diagnostics must never break a chat request.
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    internal static class ToolCallArgumentsScrubber
    {
        public static string Scrub(string body)
        {
            if (string.IsNullOrEmpty(body)) return body;

            // Pure JSON body (no SSE framing) — apply sanitizer directly.
            var trimmed = body.TrimStart();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                return RewriteSinglePayload(body);
            }

            var sseEvents = SplitSseEvents(body);
            var changed = false;
            for (var i = 0; i < sseEvents.Count; i++)
            {
                var (prefix, payload, suffix) = sseEvents[i];
                var newPayload = ScrubPayload(payload);
                if (!ReferenceEquals(newPayload, payload))
                {
                    sseEvents[i] = (prefix, newPayload, suffix);
                    changed = true;
                }
            }
            if (!changed) return body;
            var sb = new StringBuilder(body.Length);
            foreach (var (prefix, payload, suffix) in sseEvents)
            {
                sb.Append(prefix);
                sb.Append(payload);
                sb.Append(suffix);
            }
            return sb.ToString();
        }

        private static string RewriteSinglePayload(string body)
        {
            try
            {
                return RewriteArguments(body);
            }
            catch (JsonException)
            {
                return body;
            }
        }

        private static List<(string prefix, string payload, string suffix)> SplitSseEvents(string body)
        {
            var events = new List<(string, string, string)>();
            var idx = 0;
            while (idx < body.Length)
            {
                var nextData = body.IndexOf("data:", idx, StringComparison.Ordinal);
                if (nextData < 0)
                {
                    if (idx < body.Length) events.Add((body.Substring(idx), string.Empty, string.Empty));
                    break;
                }
                if (nextData > idx) events.Add((body.Substring(idx, nextData - idx), string.Empty, string.Empty));
                var payloadStart = nextData + "data:".Length;
                var lineEnd = body.IndexOf('\n', payloadStart);
                var payloadEnd = lineEnd < 0 ? body.Length : lineEnd;
                var suffixEnd = payloadEnd;
                while (suffixEnd < body.Length && (body[suffixEnd] == '\n' || body[suffixEnd] == '\r')) suffixEnd++;
                events.Add((string.Empty, body.Substring(payloadStart, payloadEnd - payloadStart), body.Substring(payloadEnd, suffixEnd - payloadEnd)));
                idx = suffixEnd;
            }
            return events;
        }

        private static string ScrubPayload(string payload)
        {
            var trimmed = payload.Trim();
            if (trimmed.Length == 0 || trimmed == "[DONE]") return payload;

            string rewritten;
            try
            {
                rewritten = RewriteArguments(payload);
            }
            catch (JsonException)
            {
                return payload;
            }
            return rewritten;
        }

        private static string RewriteArguments(string payload)
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object && root.ValueKind != JsonValueKind.Array)
                return payload;

            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteRoot(root, writer);
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static void WriteRoot(JsonElement root, Utf8JsonWriter writer)
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object) WriteAnyObject(item, writer);
                    else item.WriteTo(writer);
                }
                writer.WriteEndArray();
                return;
            }

            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("choices"))
                {
                    writer.WriteStartArray("choices");
                    foreach (var choice in property.Value.EnumerateArray())
                    {
                        if (choice.ValueKind == JsonValueKind.Object) WriteChoiceObject(choice, writer);
                        else choice.WriteTo(writer);
                    }
                    writer.WriteEndArray();
                }
                else if (property.NameEquals("messages"))
                {
                    writer.WriteStartArray("messages");
                    foreach (var message in property.Value.EnumerateArray())
                    {
                        if (message.ValueKind == JsonValueKind.Object) WriteMessage(message, writer);
                        else message.WriteTo(writer);
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        private static void WriteAnyObject(JsonElement obj, Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            foreach (var property in obj.EnumerateObject())
            {
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        private static void WriteChoiceObject(JsonElement choice, Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            foreach (var property in choice.EnumerateObject())
            {
                if (property.NameEquals("message"))
                {
                    writer.WritePropertyName("message");
                    WriteMessage(property.Value, writer);
                }
                else if (property.NameEquals("delta"))
                {
                    writer.WritePropertyName("delta");
                    WriteMessage(property.Value, writer);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        private static void WriteMessage(JsonElement message, Utf8JsonWriter writer)
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                message.WriteTo(writer);
                return;
            }

            writer.WriteStartObject();
            foreach (var property in message.EnumerateObject())
            {
                if (property.NameEquals("tool_calls"))
                {
                    WriteToolCalls(property.Value, writer);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        private static void WriteToolCalls(JsonElement toolCalls, Utf8JsonWriter writer)
        {
            if (toolCalls.ValueKind != JsonValueKind.Array)
            {
                toolCalls.WriteTo(writer);
                return;
            }

            writer.WriteStartArray("tool_calls");
            foreach (var call in toolCalls.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object)
                {
                    call.WriteTo(writer);
                    continue;
                }
                if (!TryScrubToolCall(call, out var cleaned))
                {
                    continue;
                }
                cleaned.WriteTo(writer);
            }
            writer.WriteEndArray();
        }

        private static bool TryScrubToolCall(JsonElement call, out JsonElement cleaned)
        {
            cleaned = default;
            if (!call.TryGetProperty("function", out var functionEl) || functionEl.ValueKind != JsonValueKind.Object)
            {
                cleaned = call;
                return true;
            }

            var argumentsProp = functionEl.TryGetProperty("arguments", out var argsEl)
                ? argsEl
                : default;
            var originalArguments = argumentsProp.ValueKind == JsonValueKind.String
                ? argumentsProp.GetString() ?? string.Empty
                : argumentsProp.GetRawText();

            var (scrubbedArguments, ok) = TryExtractJson(originalArguments);
            if (!ok)
            {
                return false;
            }

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var property in call.EnumerateObject())
                {
                    if (property.NameEquals("function"))
                    {
                        w.WritePropertyName("function");
                        w.WriteStartObject();
                        foreach (var fProp in functionEl.EnumerateObject())
                        {
                            if (fProp.NameEquals("arguments"))
                            {
                                w.WriteString("arguments", scrubbedArguments);
                            }
                            else
                            {
                                fProp.WriteTo(w);
                            }
                        }
                        w.WriteEndObject();
                    }
                    else
                    {
                        property.WriteTo(w);
                    }
                }
                w.WriteEndObject();
            }
            using var doc = JsonDocument.Parse(ms.ToArray());
            cleaned = doc.RootElement.Clone();
            return true;
        }

        internal static (string json, bool ok) TryExtractJson(string raw)
        {
            if (raw is null) return (string.Empty, false);

            var trimmed = raw.Trim();
            if (trimmed.Length == 0) return ("{}", true);

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                // OpenAI-compatible providers only accept a JSON object here. A bare null, number,
                // string, or array parses fine but is rejected with "arguments must be in JSON
                // format", so normalise anything that is not an object to an empty object.
                return IsJsonObject(doc.RootElement) ? (trimmed, true) : ("{}", true);
            }
            catch (JsonException)
            {
                // fall through to recovery strategies
            }

            var fenced = StripMarkdownFences(trimmed);
            if (fenced is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(fenced);
                    if (IsJsonObject(doc.RootElement))
                        return (fenced, true);
                }
                catch (JsonException) { }
            }

            var span = trimmed.AsSpan();
            for (var i = 0; i < span.Length; i++)
            {
                var c = span[i];
                if (c != '{' && c != '[') continue;
                var closeChar = c == '{' ? '}' : ']';
                var end = ScanBalanced(span, i, c, closeChar);
                if (end > i)
                {
                    var candidate = trimmed.Substring(i, end - i + 1);
                    try
                    {
                        using var doc = JsonDocument.Parse(candidate);
                        if (IsJsonObject(doc.RootElement))
                            return (candidate, true);
                    }
                    catch (JsonException)
                    {
                    }
                }
            }

            return (string.Empty, false);
        }

        private static bool IsJsonObject(JsonElement element)
            => element.ValueKind == JsonValueKind.Object;

        private static string? StripMarkdownFences(string raw)
        {
            const string openFence = "```";
            var first = raw.IndexOf(openFence, StringComparison.Ordinal);
            if (first < 0) return null;
            var after = first + openFence.Length;
            var newline = raw.IndexOf('\n', after);
            if (newline < 0) return null;
            var contentStart = newline + 1;
            var lastClose = raw.LastIndexOf(openFence, StringComparison.Ordinal);
            if (lastClose <= contentStart) return null;
            return raw.Substring(contentStart, lastClose - contentStart).Trim();
        }

        private static int ScanBalanced(ReadOnlySpan<char> span, int start, char openChar, char closeChar)
        {
            var depth = 0;
            var inString = false;
            var escape = false;
            for (var i = start; i < span.Length; i++)
            {
                var c = span[i];
                if (inString)
                {
                    if (escape) { escape = false; continue; }
                    if (c == '\\') { escape = true; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == openChar) depth++;
                else if (c == closeChar)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }
    }

    internal sealed class OpenRouterReasoningHttpContent : HttpContent
    {
        private readonly HttpContent _inner;

        public OpenRouterReasoningHttpContent(HttpContent inner)
        {
            _inner = inner;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            Headers.Remove("Content-Length");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var body = await _inner.ReadAsStringAsync();
            var transformed = AddOpenRouterReasoning(body);
            var bytes = System.Text.Encoding.UTF8.GetBytes(transformed);
            await stream.WriteAsync(bytes);
        }

        internal static string AddOpenRouterReasoning(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return body;

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return body;

                using var output = new MemoryStream();
                using (var writer = new Utf8JsonWriter(output))
                {
                    writer.WriteStartObject();
                    var hasReasoning = false;
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        property.WriteTo(writer);
                        hasReasoning |= string.Equals(property.Name, "reasoning", StringComparison.OrdinalIgnoreCase);
                    }
                    if (!hasReasoning)
                    {
                        writer.WritePropertyName("reasoning");
                        writer.WriteStartObject();
                        writer.WriteBoolean("enabled", true);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }

                return System.Text.Encoding.UTF8.GetString(output.ToArray());
            }
            catch (JsonException)
            {
                return body;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    internal static IReadOnlyDictionary<string, string> ParseExtraHeaders(string? extraHeadersJson)
    {
        if (string.IsNullOrWhiteSpace(extraHeadersJson))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(extraHeadersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Extra headers must be a JSON object.");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name)) continue;
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    throw new ArgumentException($"Extra header '{property.Name}' must be a scalar value.");
                }

                headers[property.Name.Trim()] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }

            return headers;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Extra headers must be valid JSON.", ex);
        }
    }

    internal static void ApplyHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string> extraHeaders)
    {
        var userAgent = extraHeaders.TryGetValue("User-Agent", out var configuredUserAgent) &&
                        !string.IsNullOrWhiteSpace(configuredUserAgent)
            ? configuredUserAgent
            : DefaultUserAgent;

        // Some OpenAI-compatible gateways reject the OpenAI .NET SDK user-agent.
        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

        foreach (var (name, value) in extraHeaders)
        {
            if (string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
