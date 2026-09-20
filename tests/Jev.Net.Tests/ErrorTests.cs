using System.Text;

namespace Jev.Net.Tests;

/// <summary>Mirrors upstream <c>tests/test_errors.py</c>.</summary>
[TestClass]
public sealed class ErrorTests
{
    private static readonly Dictionary<string, string> RetryHeader = new(StringComparer.OrdinalIgnoreCase) { ["retry-after-ms"] = "125" };

    [TestMethod]
    public async Task The_Endpoint_In_An_Error_Omits_Credentials_Query_And_Fragment()
    {
        using var client = Clients.Create(new StubHandler(_ => Http.Json(400, """{"message": "Bad request"}""")),
            baseUrl: "https://user:password@example.test");

        var caught = (await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeBadRequestException>()).Which;

        caught.Endpoint.Should().Be("GET https://example.test/v1/models");
        caught.Message.Should().Be("GET https://example.test/v1/models: 400 Bad request");
    }

    [TestMethod]
    [DataRow("models")]
    [DataRow("system_one")]
    public async Task An_Api_Error_Carries_Its_Request_Context(string resource)
    {
        using var client = Clients.Create(_ => Http.Json(500, """{"message": "boom"}""", ("x-typesafe-request-id", "req-ctx")));
        var act = () => resource == "models" ? (Task)client.Models.ListAsync() : client.SystemOneAsync("x", Clients.OneQuestion);

        var caught = (await act.Should().ThrowAsync<TypeSafeInternalServerException>()).Which;

        var endpoint = resource == "models" ? "GET https://api.typesafe.ai/v1/models" : "POST https://api.typesafe.ai/v1/systemone";
        caught.Endpoint.Should().Be(endpoint);
        caught.Message.Should().Be($"{endpoint}: 500 boom (request_id=req-ctx)");
    }

    public static IEnumerable<object[]> ApiErrorFactories() =>
        new Func<string, TypeSafeApiException>[]
        {
            m => new TypeSafeApiException(429, Body(), RetryHeader, m),
            m => new TypeSafeBadRequestException(429, Body(), RetryHeader, m),
            m => new TypeSafeAuthenticationException(429, Body(), RetryHeader, m),
            m => new TypeSafePermissionDeniedException(429, Body(), RetryHeader, m),
            m => new TypeSafeNotFoundException(429, Body(), RetryHeader, m),
            m => new TypeSafeUnprocessableEntityException(429, Body(), RetryHeader, m),
            m => new TypeSafeRateLimitException(429, Body(), RetryHeader, m),
            m => new TypeSafeInternalServerException(429, Body(), RetryHeader, m),
        }.Select(factory => new object[] { factory });

    private static JsonNode Body() => JsonNode.Parse("""{"message": "Server explanation"}""")!;

    [TestMethod]
    [DynamicData(nameof(ApiErrorFactories))]
    public void A_Message_Override_Replaces_The_Derived_Message(Func<string, TypeSafeApiException> create)
    {
        foreach (var message in new[] { "A custom explanation", "" })
        {
            var error = create(message);
            error.Message.Should().Be(message.Length > 0 ? $"429 {message}" : "429");
            error.Status.Should().Be(429);
            error.RequestId.Should().BeNull();
            if (error is TypeSafeRateLimitException limited)
            {
                limited.RetryAfter.Should().Be(TimeSpan.FromMilliseconds(125));
            }
        }
    }

    [TestMethod]
    public void Headers_May_Be_Absent()
    {
        var error = new TypeSafeRateLimitException(429, null, null);
        error.Headers.Should().BeEmpty();
        error.RetryAfter.Should().BeNull();
        error.Message.Should().Be("429 status code (no body)");
    }

    public static IEnumerable<object[]> BodyEdgeCases() =>
    [
        [Array.Empty<byte>(), "400 status code (no body)"],
        ["null"u8.ToArray(), "400 status code (no body)"],
        ["[]"u8.ToArray(), "400 []"],
        ["42"u8.ToArray(), "400 42"],
        [(byte[])[.. "not JSON: "u8, 0xff], "400 not JSON: �"],
        [Encoding.UTF8.GetBytes(new string('x', 201)), "400 " + new string('x', 201)],
        [Encoding.UTF8.GetBytes("{\"unknown\":\"" + new string('x', 201) + "\"}"), "400 {\"unknown\":\"" + new string('x', 188) + "…"],
        ["""{"error":"","message":"ignored"}"""u8.ToArray(), """400 {"error":"","message":"ignored"}"""],
        ["""{"detail":[null,42,{"msg":4}]}"""u8.ToArray(), """400 {"detail":[null,42,{"msg":4}]}"""],
    ];

    [TestMethod]
    [DynamicData(nameof(BodyEdgeCases))]
    public async Task Error_Body_Edge_Cases(byte[] body, string message)
    {
        using var client = Clients.Create(_ => Http.Bytes(400, body));
        var caught = (await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeApiException>()).Which;
        caught.Message.Should().Be($"GET https://api.typesafe.ai/v1/models: {message}");
        caught.RequestId.Should().BeNull();
    }

    [TestMethod]
    public void The_Hierarchy_Lets_A_Caller_Catch_At_Any_Level()
    {
        typeof(TypeSafeApiException).Should().BeDerivedFrom<TypeSafeException>();
        typeof(TypeSafeRateLimitException).Should().BeDerivedFrom<TypeSafeApiException>();
        typeof(TypeSafeApiResponseValidationException).Should().BeDerivedFrom<TypeSafeApiException>();
        typeof(TypeSafeApiConnectionException).Should().BeDerivedFrom<TypeSafeException>();
        typeof(TypeSafeApiTimeoutException).Should().BeDerivedFrom<TypeSafeApiConnectionException>();
        typeof(TypeSafeApiConnectionException).Should().NotBeDerivedFrom<TypeSafeApiException>("there was no HTTP response");
    }
}
