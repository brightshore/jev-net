using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Jev.Net.Internal;

namespace Jev.Net;

/// <summary>
/// An asynchronous HTTP client for the <a href="https://typesafe.ai">TypeSafe AI API</a>.
/// </summary>
/// <example>
/// <code>
/// await using var client = new TypeSafeClient();   // key from TYPESAFE_API_KEY
/// var result = await client.SystemOneAsync(
///     "I was charged twice. Please help.",
///     new Dictionary&lt;string, Question&gt;
///     {
///         ["billing"] = new Noul("Is this about billing?"),
///         ["tone"] = new Choice(["calm", "angry"], "What is the tone?"),
///     });
/// double billing = result.Nouls["billing"].Noul;      // probability of yes
/// string tone = result.Choices["tone"].Choice;
/// </code>
/// </example>
public sealed class TypeSafeClient : ITypeSafeClient, IDisposable, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Transport _transport;
    private bool _disposed;

    /// <summary>Create a client. The key comes from <paramref name="options"/> or <c>TYPESAFE_API_KEY</c>.</summary>
    /// <exception cref="TypeSafeException">The API key is missing or the timeout is invalid.</exception>
    /// <exception cref="ArgumentException">Both a handler and an HttpClient were supplied.</exception>
    public TypeSafeClient(TypeSafeClientOptions? options = null)
    {
        options ??= new TypeSafeClientOptions();
        if (options.Handler is not null && options.HttpClient is not null)
        {
            throw new ArgumentException("Handler and HttpClient are mutually exclusive.", nameof(options));
        }

        var config = Config.Resolve(options, options.HttpClient?.Timeout);
        var log = SdkLog.Create(options.LoggerFactory, options.EnvironmentReader ?? Environment.GetEnvironmentVariable);

        if (options.HttpClient is not null)
        {
            _http = options.HttpClient;
            _ownsHttp = options.DisposeHttpClient;
        }
        else
        {
            // The per-attempt deadline is ours - HttpClient's own timeout would otherwise cap a longer per-call one.
            _http = new HttpClient(options.Handler ?? CreateDefaultHandler(), disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _ownsHttp = true;
        }

        _transport = new Transport(_http, config, options.Retry ?? new RetryPolicy(), log, options);
        Models = new ModelsResource(_transport);
    }

    /// <summary>
    /// The handler behind a client that was given neither a <see cref="TypeSafeClientOptions.Handler"/> nor an
    /// <see cref="TypeSafeClientOptions.HttpClient"/>. Three settings, each a default that bites:
    /// </summary>
    internal static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        // A 302 is an error to report, as upstream - not something to follow with the bearer token attached.
        AllowAutoRedirect = false,
        // .NET's default is None: no Accept-Encoding goes out and nothing comes back compressed. httpx asks
        // for gzip/deflate by default, so without this the port is chattier than the library it ports.
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        // The default is Infinite: a long-lived client never re-resolves DNS, and keeps talking to an
        // address the service has moved away from.
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    /// <summary>The Models API resource.</summary>
    public ModelsResource Models { get; }

    IModelsResource ITypeSafeClient.Models => Models;

    /// <summary>Answer named questions about text or structured state.</summary>
    /// <param name="state">Text, a JSON object, or an array to evaluate.</param>
    /// <param name="questions">Nonempty mapping of names to questions. Names are yours: they key the answers.</param>
    /// <param name="options">Per-call overrides.</param>
    /// <param name="cancellationToken">Cancels the call, including a wait between retries.</param>
    /// <exception cref="TypeSafeException">Questions are empty or a score question's criteria list is empty.</exception>
    /// <exception cref="TypeSafeApiException">The server returned an unsuccessful response after any retries.</exception>
    /// <exception cref="TypeSafeApiConnectionException">The request could not connect or timed out after any retries.</exception>
    /// <exception cref="TypeSafeApiResponseValidationException">The response body did not match the response type.</exception>
    public Task<SystemOneResponse> SystemOneAsync(
        JsonContent state, IReadOnlyDictionary<string, Question> questions, SystemOneOptions? options = null,
        CancellationToken cancellationToken = default) =>
        SystemOneAsync<SystemOneResponse>(state, questions, options, cancellationToken);

    /// <summary>
    /// <see cref="SystemOneAsync(JsonContent, IReadOnlyDictionary{string, Question}, SystemOneOptions?, CancellationToken)"/>,
    /// decoded into your own <see cref="SystemOneResponse"/> subclass: each answer-typed property is filled
    /// from the answer of the same name, and is required unless marked <see cref="OptionalAnswerAttribute"/>.
    /// Trim- and AOT-safe.
    /// </summary>
    public Task<TResponse> SystemOneAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TResponse>(
        JsonContent state, IReadOnlyDictionary<string, Question> questions, SystemOneOptions? options = null,
        CancellationToken cancellationToken = default) where TResponse : SystemOneResponse, new() =>
        SendSystemOne(state, questions, options, raw => ResponseDecoder.SystemOne<TResponse>(raw, _transport.Log), cancellationToken);

    /// <summary>The response body read into any type of yours, using source-generated JSON metadata. Trim- and
    /// AOT-safe. The API spells its fields in snake_case: give your context
    /// <c>PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower</c>.</summary>
    /// <remarks><paramref name="options"/> stays the third parameter on every overload, and is not optional
    /// here: that is what keeps <c>SystemOneAsync&lt;T&gt;(state, questions, null, token)</c> unambiguous.</remarks>
    public Task<TResponse> SystemOneAsync<TResponse>(
        JsonContent state, IReadOnlyDictionary<string, Question> questions, SystemOneOptions? options,
        JsonTypeInfo<TResponse> responseTypeInfo, CancellationToken cancellationToken = default) where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(responseTypeInfo);
        return SendSystemOne(state, questions, options, raw => ResponseDecoder.Custom(raw, responseTypeInfo), cancellationToken);
    }

    /// <summary>The response body read into any type of yours by reflection — convenient, but NOT trim- or
    /// AOT-safe. <see cref="ResponseJson.SnakeCase"/> is the usual choice of options.</summary>
    [RequiresUnreferencedCode(ReflectionJson)]
    [RequiresDynamicCode(ReflectionJson)]
    public Task<TResponse> SystemOneAsync<TResponse>(
        JsonContent state, IReadOnlyDictionary<string, Question> questions, SystemOneOptions? options,
        JsonSerializerOptions responseSerializerOptions, CancellationToken cancellationToken = default) where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(responseSerializerOptions);
        return SendSystemOne(state, questions, options, raw => ResponseDecoder.Custom<TResponse>(raw, responseSerializerOptions), cancellationToken);
    }

    internal const string ReflectionJson =
        "Uses reflection-based System.Text.Json. In a trimmed or Native AOT app, use the overload that takes a JsonTypeInfo<T>.";

    private Task<TResponse> SendSystemOne<TResponse>(
        JsonContent state, IReadOnlyDictionary<string, Question> questions, SystemOneOptions? options,
        Func<RawHttpResponse, TResponse> decode, CancellationToken cancellationToken) where TResponse : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        if (state.IsNull)
        {
            throw new TypeSafeException("State must be text, a JSON object, or a JSON array; got null.");
        }

        Question.ValidateAll(questions);

        var encoded = new JsonObject();
        foreach (var (name, question) in questions)
        {
            encoded[name] = question.ToJson();
        }

        var body = new JsonObject
        {
            ["state"] = state.ToNode(),
            ["model"] = options?.Model ?? _transport.Config.DefaultModel,
            ["questions"] = encoded,
        };
        if (options?.ExtraBody is not null)
        {
            foreach (var (key, value) in options.ExtraBody)
            {
                body[key] = value?.DeepClone();
            }
        }

        var request = _transport.Prepare(HttpMethod.Post, Protocol.SystemOnePath, body, options?.Timeout, options?.ExtraHeaders);
        return _transport.SendAsync(request, options?.Retry, decode, cancellationToken);
    }

    /// <summary>Release network resources, including a supplied HttpClient unless told otherwise.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Access to the models available to the account, reached through <see cref="TypeSafeClient.Models"/>.</summary>
public sealed class ModelsResource : IModelsResource
{
    private readonly Transport _transport;

    internal ModelsResource(Transport transport) => _transport = transport;

    /// <summary>List the models available to the account.</summary>
    /// <exception cref="TypeSafeApiException">The server returned an unsuccessful response after any retries.</exception>
    /// <exception cref="TypeSafeApiConnectionException">The request could not connect or timed out after any retries.</exception>
    public Task<ListModelsResponse> ListAsync(RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _transport.SendAsync(
            _transport.Prepare(HttpMethod.Get, Protocol.ModelsPath, null, options?.Timeout, options?.ExtraHeaders),
            options?.Retry, ResponseDecoder.Models, cancellationToken);
}
