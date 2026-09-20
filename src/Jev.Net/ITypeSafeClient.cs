using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Jev.Net;

/// <summary>
/// What <see cref="TypeSafeClient"/> does, as an interface — so code that depends on it can be handed a fake.
/// (To fake at the HTTP level instead, give the real client a <see cref="TypeSafeClientOptions.Handler"/>; to
/// build a genuine response for a fake to return, see <see cref="SystemOneResponse.FromHttpResponse(RawHttpResponse)"/>.)
/// </summary>
public interface ITypeSafeClient
{
    /// <summary>The Models API resource.</summary>
    IModelsResource Models { get; }

    /// <inheritdoc cref="TypeSafeClient.SystemOneAsync(JsonContent, IReadOnlyDictionary{string, Question}, SystemOneOptions?, CancellationToken)"/>
    Task<SystemOneResponse> SystemOneAsync(
        JsonContent state, IReadOnlyDictionary<string, Question> questions, SystemOneOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Answers decoded into your own <see cref="SystemOneResponse"/> subclass. Trim- and AOT-safe.</summary>
    Task<TResponse> SystemOneAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TResponse>(
        JsonContent state, IReadOnlyDictionary<string, Question> questions, SystemOneOptions? options = null,
        CancellationToken cancellationToken = default) where TResponse : SystemOneResponse, new();

    /// <summary>The response body read into any type of yours, using source-generated JSON metadata.</summary>
    Task<TResponse> SystemOneAsync<TResponse>(
        JsonContent state, IReadOnlyDictionary<string, Question> questions, SystemOneOptions? options,
        JsonTypeInfo<TResponse> responseTypeInfo, CancellationToken cancellationToken = default) where TResponse : class;

    /// <summary>The response body read into any type of yours by reflection — not trim- or AOT-safe.</summary>
    [RequiresUnreferencedCode(TypeSafeClient.ReflectionJson)]
    [RequiresDynamicCode(TypeSafeClient.ReflectionJson)]
    Task<TResponse> SystemOneAsync<TResponse>(
        JsonContent state, IReadOnlyDictionary<string, Question> questions, SystemOneOptions? options,
        JsonSerializerOptions responseSerializerOptions, CancellationToken cancellationToken = default) where TResponse : class;
}

/// <summary>The Models API resource, as an interface.</summary>
public interface IModelsResource
{
    /// <summary>List the models available to the account.</summary>
    Task<ListModelsResponse> ListAsync(RequestOptions? options = null, CancellationToken cancellationToken = default);
}
