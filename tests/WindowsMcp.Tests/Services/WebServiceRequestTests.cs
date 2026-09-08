using FluentAssertions;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// <c>RequestAsync</c> — the body of the <c>http_request</c> tool — against a real HTTP server on
/// loopback. Everything above this method is mocked in <c>WebToolsTests</c>
/// (<c>Mock&lt;IWebService&gt;</c> answering with a hand-written <c>HttpResponseDto</c>), so
/// nothing else proves that the headers are actually sent, that the body reaches the server, or
/// that the response headers a caller reads are the ones that came back. C-5 rewrote the scheme
/// check this method runs first, which is what brought it into scope.
/// </summary>
[Trait("Category", "Integration")]
public class WebServiceRequestTests : IClassFixture<LocalHttpServerFixture>
{
    private readonly LocalHttpServerFixture _fixture;

    public WebServiceRequestTests(LocalHttpServerFixture fixture) => _fixture = fixture;

    // allowPrivateIps: the fixture binds to 127.0.0.1, which the SSRF guard blocks by design.
    private static WebService Service() => new(allowPrivateIps: true);

    [Fact]
    public async Task RequestAsync_reports_the_status_the_server_answered_with()
    {
        var result = await Service().RequestAsync(_fixture.UrlFor("/echo"), "GET", null, null);

        result.Status.Should().Be(201, "the status is the server's, not a 200 assumed by the client");
        result.Headers.Should().ContainKey("X-Echo-Method").WhoseValue.Should().Be("GET");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task RequestAsync_sends_the_method_it_was_given(string method)
    {
        var result = await Service().RequestAsync(_fixture.UrlFor("/echo"), method, null, null);

        result.Headers["X-Echo-Method"].Should().Be(method,
            "the tool advertises GET|POST|PUT|DELETE|PATCH and each has to reach the server as itself");
    }

    /// <summary>
    /// The headers are the reason the tool takes a <c>headers_json</c> at all: an Authorization
    /// header that is silently dropped turns every authenticated call into a 401 the caller cannot
    /// explain. <c>TryAddWithoutValidation</c> is what carries a header the .NET client would
    /// otherwise reject as belonging on the content.
    /// </summary>
    [Fact]
    public async Task RequestAsync_sends_every_header_it_was_given()
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer s3cr3t-token",
            ["X-Custom"] = "custom value",
        };

        var result = await Service().RequestAsync(_fixture.UrlFor("/echo"), "GET", headers, null);

        result.Headers["X-Echo-Auth"].Should().Be("Bearer s3cr3t-token");
        result.Headers["X-Echo-Custom"].Should().Be("custom value");
    }

    /// <summary>
    /// The body reaches the server byte for byte, as UTF-8 JSON — the content type the tool
    /// documents for POST/PUT/PATCH. Non-ASCII is the case a wrong encoding destroys silently.
    /// </summary>
    [Fact]
    public async Task RequestAsync_sends_the_body_as_utf8_json()
    {
        const string body = "{\"name\":\"caf\\u00e9\",\"n\":1}";

        var result = await Service().RequestAsync(_fixture.UrlFor("/echo"), "POST", null, body);

        result.Body.Should().Be(body, "the server got exactly what the caller wrote");
        result.Headers["X-Echo-Content-Type"].Should().StartWith("application/json",
            "the tool documents the body as JSON");
        result.Headers["X-Echo-Content-Type"].Should().ContainEquivalentOf("utf-8",
            "a body of non-ASCII sent in the ANSI codepage arrives as mojibake");
    }

    [Fact]
    public async Task RequestAsync_sends_no_body_when_none_was_given()
    {
        var result = await Service().RequestAsync(_fixture.UrlFor("/echo"), "GET", null, null);

        result.Body.Should().BeEmpty();
        result.Headers["X-Echo-Content-Type"].Should().Be("(none)",
            "a GET with no body must not acquire a content type from nowhere");
    }

    /// <summary>
    /// The reported headers are the response's own headers AND its content headers in one map —
    /// a caller reading Content-Type off the result would otherwise never find it, because
    /// <c>HttpResponseMessage.Headers</c> does not carry it.
    /// </summary>
    [Fact]
    public async Task RequestAsync_reports_the_response_and_content_headers_together()
    {
        var result = await Service().RequestAsync(_fixture.UrlFor("/echo"), "POST", null, "payload");

        result.Headers.Should().ContainKey("Content-Type").WhoseValue.Should().StartWith("text/plain",
            "Content-Type is a CONTENT header; a map built from response.Headers alone omits it");
        result.Headers.Should().ContainKey("X-Echo-Method", "and the response's own headers are there too");
    }

    /// <summary>
    /// F5: the scheme is refused before anything is sent, for <c>http_request</c> exactly as for
    /// <c>scrape</c> — <c>HttpClient</c> answers a <c>file:</c> URL with a
    /// <c>NotSupportedException</c>, which is not caller-facing and reaches the client as "An
    /// error occurred invoking 'http_request'." The unit sibling is
    /// <c>WebServiceTests.RequestAsync_refuses_any_scheme_but_http_and_https</c>; this one proves
    /// the refusal still comes first with a live client behind it.
    /// </summary>
    [Fact]
    public async Task RequestAsync_refuses_a_file_url_before_it_sends_anything()
    {
        Func<Task> act = () => Service().RequestAsync("file:///C:/Windows/win.ini", "GET", null, null);

        var thrown = await act.Should().ThrowAsync<ArgumentException>();
        thrown.WithMessage("*file*", "the message names the scheme that was refused");
        thrown.WithMessage("*http*", "and the two that are fetched");
    }

    /// <summary>
    /// A 404 is an ANSWER for <c>http_request</c> — unlike <c>scrape</c>, which cannot convert a
    /// page it did not get. The tool exists to let a caller see status codes, so a failure status
    /// must come back as a result, not as an exception.
    /// </summary>
    [Fact]
    public async Task RequestAsync_returns_a_failure_status_as_a_result_rather_than_throwing()
    {
        var result = await Service().RequestAsync(_fixture.UrlFor("/missing"), "GET", null, null);

        result.Status.Should().Be(404);
    }
}
