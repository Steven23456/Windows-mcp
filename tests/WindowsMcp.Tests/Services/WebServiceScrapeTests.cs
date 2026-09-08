using FluentAssertions;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// C-5 (roadmap R8): <c>ScrapeAsync</c> against a real HTTP server on loopback. The private-IP
/// refusals are unit-tested in <see cref="WebServiceTests"/>; everything here needs a response to
/// read a title, a size and a cap out of, and a mocked <c>HttpClient</c> would prove none of it.
/// </summary>
[Trait("Category", "Integration")]
public class WebServiceScrapeTests : IClassFixture<LocalHttpServerFixture>
{
    private readonly LocalHttpServerFixture _fixture;

    public WebServiceScrapeTests(LocalHttpServerFixture fixture) => _fixture = fixture;

    // allowPrivateIps: the fixture binds to 127.0.0.1, which the SSRF guard blocks by design.
    private static WebService Service() => new(allowPrivateIps: true);

    [Fact]
    public async Task ScrapeAsync_reports_the_source_url_title_and_size()
    {
        var url = _fixture.UrlFor("/one");

        var result = await Service().ScrapeAsync(url);

        result.Source.Should().Be("http");
        result.Url.Should().Be(url, "the URL the content came from, after any redirect");
        result.Title.Should().Be("One");
        result.Content.Should().Contain("One");
        result.Chars.Should().Be(result.Content.Length, "nothing was cut, so the size is the content's size");
        result.Truncated.Should().BeFalse();
        result.Summarized.Should().BeFalse("summarizing is opt-in and happens in the tool layer");
        result.Model.Should().BeNull();
        result.Note.Should().BeNull();
    }

    [Fact]
    public async Task ScrapeAsync_reports_no_title_when_the_page_has_none()
    {
        var result = await Service().ScrapeAsync(_fixture.UrlFor("/"));

        result.Title.Should().BeNull("null says 'this page has no title', which \"\" does not");
        result.Content.Should().Contain("Hello");
    }

    [Fact]
    public async Task ScrapeAsync_decodes_the_entities_and_collapses_the_whitespace_in_a_title()
    {
        var result = await Service().ScrapeAsync(_fixture.UrlFor("/titled"));

        result.Title.Should().Be("A & B C");
    }

    [Fact]
    public async Task ScrapeAsync_reports_the_url_it_ended_up_at_after_a_redirect()
    {
        var result = await Service().ScrapeAsync(_fixture.UrlFor("/redirect"));

        result.Url.Should().Be(_fixture.UrlFor("/one"),
            "the URL the content came from, not the one the caller typed - a http:// that landed "
            + "on https:// has to say so");
        result.Title.Should().Be("One", "the content is the destination's, so the title is too");
    }

    /// <summary>
    /// <c>/titled</c>, not <c>/one</c>: <c>/one</c> converts to exactly <c>One</c>, so a cap of 3
    /// would cut nothing and the test would pass against a service that never truncated at all.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_cuts_the_content_at_max_chars_and_says_it_did()
    {
        var whole = await Service().ScrapeAsync(_fixture.UrlFor("/titled"));
        whole.Chars.Should().BeGreaterThan(3, "the page has to be longer than the cap for the cap to mean anything");

        var cut = await Service().ScrapeAsync(_fixture.UrlFor("/titled"), maxChars: 3);

        cut.Content.Length.Should().Be(3);
        cut.Content.Should().Be(whole.Content[..3], "the cut keeps the front of the text, not a re-fetch of it");
        cut.Truncated.Should().BeTrue();
        cut.Chars.Should().Be(whole.Chars, "Chars is the size BEFORE the cap - that is how a caller knows what it missed");
        cut.Chars.Should().BeGreaterThan(3);
    }

    [Fact]
    public async Task ScrapeAsync_does_not_truncate_when_the_cap_is_exactly_the_size()
    {
        var whole = await Service().ScrapeAsync(_fixture.UrlFor("/titled"));

        var atTheCap = await Service().ScrapeAsync(_fixture.UrlFor("/titled"), maxChars: whole.Chars);

        atTheCap.Truncated.Should().BeFalse("nothing was removed at exactly the cap");
        atTheCap.Content.Should().Be(whole.Content);
    }

    [Theory]
    [InlineData(0)]     // 0 is not "all"
    [InlineData(-1)]
    public async Task ScrapeAsync_refuses_a_non_positive_max_chars(int maxChars)
    {
        Func<Task> act = () => Service().ScrapeAsync(_fixture.UrlFor("/one"), maxChars);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*maxChars*");
    }

    // ---- C-5: a failed fetch is an answer, not a fault ------------------------------------------

    /// <summary>
    /// C-5: <c>InvalidOperationException</c> is on <c>ToolErrors.IsCallerFacing</c>, so the caller
    /// reads the real reason instead of the SDK's "An error occurred invoking 'scrape'." mask. The
    /// URL has to be in the message: with <c>source:dom</c> and redirects in play, "not found" on
    /// its own does not say what was not found.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_of_a_page_that_answers_404_is_a_caller_facing_refusal_naming_the_url()
    {
        var url = _fixture.UrlFor("/missing");

        Func<Task> act = () => Service().ScrapeAsync(url);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>(
            "a 404 is the server's answer, and the caller has to see it");
        thrown.WithMessage($"*{url}*", "the message names the URL that failed");
        thrown.WithMessage("*404*", "and the status that is the whole diagnosis");
        thrown.And.Should().NotBeOfType<HttpRequestException>(
            "HttpRequestException is not caller-facing; the SDK would mask it");
    }

    [Fact]
    public async Task ScrapeAsync_of_a_host_that_refuses_the_connection_is_a_caller_facing_refusal_naming_the_url()
    {
        var url = $"http://127.0.0.1:{UnusedPort()}/anything";

        Func<Task> act = () => Service().ScrapeAsync(url);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.WithMessage($"*{url}*",
            "a refused connection names the URL too - the caller may have several in flight");
    }

    // ---- C-5: a host that does not answer in time is a timeout, not a cancellation --------------

    /// <summary>
    /// C-5: <c>TaskCanceledException</c> is what <c>HttpClient</c> throws when its own clock runs
    /// out, and it is NOT on <c>ToolErrors.IsCallerFacing</c> — a caller would see the SDK's
    /// "An error occurred invoking 'scrape'." and learn nothing. <c>TimeoutException</c> is, so the
    /// answer says which URL failed to answer. The fixture's <c>/slow</c> takes ~3 s; the client is
    /// given 300 ms, so only the client's clock can end this call.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_of_a_host_that_does_not_answer_in_time_is_a_timeout_naming_the_url()
    {
        var url = _fixture.UrlFor("/slow");
        var svc = new WebService(allowPrivateIps: true, httpTimeout: TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Func<Task> act = () => svc.ScrapeAsync(url);

        var thrown = await act.Should().ThrowAsync<TimeoutException>(
            "the client's own clock ran out - a TaskCanceledException here would be masked by the SDK");
        thrown.WithMessage($"*{url}*", "the message names the URL that did not answer");
        thrown.WithMessage("*did not respond*");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "the 300 ms client timeout bounds the call; the server's 3-second answer must not be waited for");
    }

    /// <summary>
    /// C-5: the message carries the budget as well as the URL — "did not respond" alone leaves the
    /// caller unable to tell a slow page from a dead host. One second renders as <c>1s</c>.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_timeout_names_the_seconds_the_client_waited()
    {
        var svc = new WebService(allowPrivateIps: true, httpTimeout: TimeSpan.FromSeconds(1));

        Func<Task> act = () => svc.ScrapeAsync(_fixture.UrlFor("/slow"));

        var thrown = await act.Should().ThrowAsync<TimeoutException>();
        thrown.WithMessage("*within 1s*", "the caller is told how long it waited before giving up");
    }

    /// <summary>
    /// The <c>HttpClient</c> is one instance per service (C-5), so a timed-out request must not
    /// leave it unusable for the next call — the service is a process singleton and every later
    /// scrape would inherit the damage.
    /// </summary>
    [Fact]
    public async Task A_timed_out_request_does_not_stop_the_same_service_fetching_the_next_page()
    {
        var svc = new WebService(allowPrivateIps: true, httpTimeout: TimeSpan.FromMilliseconds(300));

        Func<Task> slow = () => svc.ScrapeAsync(_fixture.UrlFor("/slow"));
        await slow.Should().ThrowAsync<TimeoutException>();

        var result = await svc.ScrapeAsync(_fixture.UrlFor("/one"));

        result.Title.Should().Be("One", "the same client answers the next page normally");
        result.Content.Should().Contain("One");
    }

    /// <summary>
    /// C-5: the two halves of the same <c>catch</c>. The client's clock is a
    /// <c>TimeoutException</c>; the CALLER's token stays an <c>OperationCanceledException</c>, so a
    /// cancelled call is never reported as a timeout the caller did not ask for.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_with_an_already_cancelled_token_is_a_cancellation_not_a_timeout()
    {
        var svc = new WebService(allowPrivateIps: true, httpTimeout: TimeSpan.FromMilliseconds(300));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => svc.ScrapeAsync(_fixture.UrlFor("/slow"), 100000, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the caller cancelled; reporting that as a TimeoutException would blame the host for the caller's own decision");
    }

    [Fact]
    public async Task ScrapeAsync_cancelled_while_the_request_is_in_flight_is_a_cancellation_not_a_timeout()
    {
        // A generous client timeout, so only the caller's token can end this: if the two were
        // confused, the result would be a TimeoutException naming a host that was answering fine.
        var svc = new WebService(allowPrivateIps: true, httpTimeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Func<Task> act = () => svc.ScrapeAsync(_fixture.UrlFor("/slow"), 100000, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the caller's cancellation reaches the caller as cancellation, not as the client's timeout");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the token ended it, not the 30-second client clock");
    }

    // ---- F5: the scheme refusal does not touch an http(s) fetch ---------------------------------

    /// <summary>
    /// F5: the refusal is "everything except http and https", so the two that ARE served must go
    /// through untouched — including a scheme the caller spelled in upper case, which
    /// <see cref="Uri"/> normalises and a naive ordinal comparison would refuse.
    /// </summary>
    [Theory]
    [InlineData("http")]
    [InlineData("HTTP")]
    public async Task ScrapeAsync_still_fetches_an_http_url_whatever_the_case_of_the_scheme(string scheme)
    {
        var url = scheme + _fixture.UrlFor("/one")["http".Length..];

        var result = await Service().ScrapeAsync(url);

        result.Title.Should().Be("One", "http is one of the two schemes that are served");
    }

    /// <summary>
    /// F5: https gets past the scheme check too. Nothing on loopback speaks TLS, so the proof is
    /// that the call fails at the CONNECTION — the fetch was attempted — and not at validation.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_of_an_https_url_is_not_refused_by_the_scheme_check()
    {
        var url = $"https://127.0.0.1:{UnusedPort()}/anything";

        Func<Task> act = () => Service().ScrapeAsync(url);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>(
            "https is served: the call got as far as trying to connect");
        thrown.And.Should().NotBeOfType<ArgumentException>("https is not a refused scheme");
        thrown.WithMessage($"*{url}*");
    }

    // ---- F9: HTML too deep to convert is refused, not survived by luck -------------------------

    /// <summary>
    /// F9: <c>ReverseMarkdown</c> walks the document recursively, so nesting depth is stack depth
    /// and a hostile (or merely generated) page can end the SERVER, not just the call — a
    /// <c>StackOverflowException</c> cannot be caught and takes the process with it. The depth is
    /// checked before the conversion and refused with an answer the caller can act on.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_of_html_nested_past_the_limit_is_refused_naming_the_url_and_the_limit()
    {
        var url = _fixture.UrlFor($"/deep/{WebService.MaxNestingDepth + 1}");

        Func<Task> act = () => Service().ScrapeAsync(url);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>(
            "a refusal is caller-facing (ToolErrors); a crash is not an answer at all");
        thrown.WithMessage($"*{url}*", "the caller may have several fetches in flight");
        thrown.WithMessage($"*{WebService.MaxNestingDepth}*", "and the limit it went past");
    }

    /// <summary>
    /// F9: exactly at the limit converts. A guard that refuses the last legal document is a guard
    /// that will be tuned down until it stops guarding.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_of_html_nested_exactly_at_the_limit_still_converts()
    {
        var result = await Service().ScrapeAsync(_fixture.UrlFor($"/deep/{WebService.MaxNestingDepth}"));

        result.Title.Should().Be("Deep");
        result.Content.Should().Contain("bottom", "the innermost text is the page's whole content");
    }

    // ---- F12: no credentials in the URL that comes back ----------------------------------------

    /// <summary>
    /// F12: <c>Url</c> is the response's request URI, and a URI keeps the <c>user:password</c> the
    /// caller put in it. The result is serialized to the client, logged, and quoted back by the
    /// model — so a password sent in a URL must not be handed back out in the answer.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_does_not_report_the_credentials_the_caller_put_in_the_url()
    {
        var target = _fixture.UrlFor("/one");
        var withCredentials = target.Replace("http://", "http://alice:s3cr3t@", StringComparison.Ordinal);

        var result = await Service().ScrapeAsync(withCredentials);

        result.Url.Should().NotContain("s3cr3t", "the password must not survive into the result");
        result.Url.Should().NotContain("alice", "nor the user name that goes with it");
        result.Url.Should().Be(target, "everything else about the URL is unchanged");
        result.Title.Should().Be("One", "the page was still fetched");
    }

    /// <summary>
    /// F12: <c>user@host</c> with no password is the same leak in a smaller form — a user name is
    /// still an identity the model should not be handed back and quote. The <c>UserInfo</c> of
    /// this URI is "alice", which the naive "split on the colon" reading does not see at all.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_strips_a_user_name_that_came_without_a_password()
    {
        var target = _fixture.UrlFor("/one");
        var withUser = target.Replace("http://", "http://alice@", StringComparison.Ordinal);

        var result = await Service().ScrapeAsync(withUser);

        result.Url.Should().NotContain("alice", "a user name on its own is still a credential");
        result.Url.Should().NotContain("@");
        result.Url.Should().Be(target);
    }

    /// <summary>
    /// F12: stripping the credentials is a surgical edit, not a rebuild of the URL from its host
    /// and path. The query string is frequently the only thing that identifies the page (a search,
    /// an article id), so a rewrite that dropped it would report the wrong URL for the content.
    /// The <c>%20</c> pins the other half of the same promise: the strip rebuilds the URI through
    /// <see cref="UriBuilder"/> and reports <c>AbsoluteUri</c>, so the escapes the caller sent
    /// come back as the caller sent them.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_keeps_the_query_string_when_it_strips_the_credentials()
    {
        var target = _fixture.UrlFor("/one?q=hello%20world&page=2");
        var withCredentials = target.Replace("http://", "http://alice:s3cr3t@", StringComparison.Ordinal);

        var result = await Service().ScrapeAsync(withCredentials);

        result.Url.Should().NotContain("s3cr3t").And.NotContain("alice");
        result.Url.Should().Be(target, "the query is part of what was fetched, not part of the credentials");
        result.Url.Should().Contain("q=hello%20world",
            "the rebuild that drops the credentials keeps the query's escapes intact");
        result.Url.Should().NotContain("hello world",
            "a %20 decoded into a raw space is a URL the caller cannot paste back or re-fetch");
        result.Title.Should().Be("One", "the query did not change which page was served");
    }

    /// <summary>
    /// F12: the URL of a page fetched WITHOUT credentials passes through untouched — the strip
    /// runs on every result, so a rewrite that damaged the ordinary URL would damage every scrape.
    /// "Untouched" covers the escapes: the reported URL is <c>Uri.AbsoluteUri</c>, the wire form,
    /// and not <c>Uri.ToString()</c>, the display form, which decodes a <c>%20</c> into a space and
    /// leaves a string that is no longer a URL.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_reports_a_query_string_unchanged_when_there_are_no_credentials()
    {
        var target = _fixture.UrlFor("/one?q=hello%20world");

        var result = await Service().ScrapeAsync(target);

        result.Url.Should().Be(target);
        result.Url.Should().EndWith("?q=hello%20world", "the escape the caller sent is the escape reported back");
        result.Url.Should().NotContain("hello world", "a decoded space is not what was asked for or fetched");
    }

    /// <summary>
    /// The reported <c>Url</c> is <c>Uri.AbsoluteUri</c>, the URI's wire form, which keeps every
    /// escape the caller sent — not <c>Uri.ToString()</c>, the display form, which decodes the ones
    /// it judges cosmetic. The model quotes this string back and re-fetches it, so both classes
    /// have to survive: a <c>%26</c> decoded to <c>&amp;</c> splits the query in two and asks the
    /// server for something else (<c>?q=a&amp;b</c> is not <c>?q=a%26b</c>), and a <c>%20</c> or a
    /// <c>%C3%A9</c> decoded in place leaves a string that is no longer a URL to paste back.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_reports_an_escaped_query_without_changing_what_it_means()
    {
        var target = _fixture.UrlFor("/one?q=a%26b%2Fc%20caf%C3%A9&page=2");

        var result = await Service().ScrapeAsync(target);

        result.Url.Should().Be(target,
            "a %26 decoded to & is a different query: the caller would be handed a URL that asks "
            + "for something else than the one that was fetched");
        result.Url.Should().NotContain("a&b", "the escape that holds the query together survives");
        result.Url.Should().NotContain("c caf", "and so does the one that only looks cosmetic");
        result.Url.Should().NotContain("é", "a decoded UTF-8 escape is the same damage in another alphabet");
    }

    // ---- the SSRF gate lets a public address through --------------------------------------------

    /// <summary>
    /// F5: the address check is a refusal for PRIVATE addresses only. Every other test here runs
    /// with <c>allowPrivateIps: true</c> and returns before the resolution, so this is the one
    /// that walks the resolved addresses and finds nothing to refuse. 192.0.2.1 is TEST-NET-1
    /// (RFC 5737) — a literal, so no DNS traffic, public by every rule in
    /// <c>IsPrivateAddress</c>, and routed nowhere, so the fetch that follows fails on its own
    /// clock rather than on the guard.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_of_a_public_address_is_not_refused_by_the_private_ip_guard()
    {
        var svc = new WebService(allowPrivateIps: false, httpTimeout: TimeSpan.FromSeconds(2));

        Func<Task> act = () => svc.ScrapeAsync("http://192.0.2.1/page");

        var thrown = await act.Should().ThrowAsync<Exception>("nothing is listening on TEST-NET-1");
        thrown.Which.Message.Should().NotContain("private IP",
            "192.0.2.1 is a public address: the guard has to let it past and let the fetch fail");
        thrown.Which.Should().Match(e => e is TimeoutException || e is InvalidOperationException,
            "an unreachable host is the client's clock or a failed connection, either way an answer");
    }

    /// <summary>
    /// F5: a host that does not resolve is not a refusal either — the guard cannot check addresses
    /// it never got, so it lets the request through and the fetch reports the failure as a
    /// caller-facing answer naming the URL. A <c>SocketException</c> escaping the guard would be
    /// masked by the SDK.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_of_a_host_that_does_not_resolve_is_a_caller_facing_refusal_naming_the_url()
    {
        // .invalid is reserved by RFC 2606 precisely so it can never resolve.
        var url = $"http://{Guid.NewGuid():N}.invalid/page";
        var svc = new WebService(allowPrivateIps: false, httpTimeout: TimeSpan.FromSeconds(5));

        Func<Task> act = () => svc.ScrapeAsync(url);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>(
            "a name that does not exist is an answer the caller can act on");
        thrown.WithMessage($"*{url}*", "which URL failed is the whole of the answer");
        thrown.Which.Message.Should().NotContain("private IP", "it resolved to nothing, not to a private address");
    }

    /// <summary>A loopback port nothing is listening on: bound to find a free one, then released.</summary>
    private static int UnusedPort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
