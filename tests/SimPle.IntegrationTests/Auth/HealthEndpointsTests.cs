using System.Net;
using FluentAssertions;
using SimPle.Api.Middleware;

namespace SimPle.IntegrationTests.Auth;

public sealed class HealthEndpointsTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    [Fact]
    public async Task LiveProbe_ReturnsHealthy_WhenTheProcessCanServeHttp()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"status\":\"healthy\"}");
    }

    [Fact]
    public async Task ReadyProbe_ReturnsGenericHealthyStatus_WithoutDependencyDetails()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Be("{\"status\":\"healthy\"}")
            .And.NotContain("database")
            .And.NotContain("storage")
            .And.NotContain("worker");
    }

    [Fact]
    public async Task CorrelationId_EchoesSafeCallerValue_AndReplacesUnsafeInput()
    {
        using var client = _factory.CreateClient();
        using var safeRequest = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        safeRequest.Headers.Add(CorrelationIdMiddleware.HeaderName, "release-42.trace_1");

        var safeResponse = await client.SendAsync(safeRequest);
        safeResponse.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Should().ContainSingle()
            .Which.Should().Be("release-42.trace_1");

        using var unsafeRequest = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        unsafeRequest.Headers.Add(CorrelationIdMiddleware.HeaderName, "bad<script");

        var unsafeResponse = await client.SendAsync(unsafeRequest);
        unsafeResponse.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Should().ContainSingle()
            .Which.Should().NotBe("bad<script");
    }

    public void Dispose() => _factory.Dispose();
}
