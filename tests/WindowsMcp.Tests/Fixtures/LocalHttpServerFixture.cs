using System.Net;

namespace WindowsMcp.Tests.Fixtures;

public sealed class LocalHttpServerFixture : IDisposable
{
    private readonly HttpListener _listener;
    public string BaseUrl { get; }
    private readonly Task _serverTask;

    public LocalHttpServerFixture()
    {
        // Pick a random free port
        var tmp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tmp.Start();
        var port = ((IPEndPoint)tmp.LocalEndpoint).Port;
        tmp.Stop();

        BaseUrl = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _serverTask = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                // C-5: a real 404 status (the catch-all below answers "404" with status 200), so a
                // scrape can prove a failed fetch is a caller-facing refusal naming the URL.
                if (ctx.Request.Url!.AbsolutePath == "/missing")
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    continue;
                }

                // C-5: a 302 to /one, so a scrape can prove it reports the URL it ENDED at.
                if (ctx.Request.Url!.AbsolutePath == "/redirect")
                {
                    ctx.Response.StatusCode = 302;
                    ctx.Response.RedirectLocation = BaseUrl + "/one";
                    ctx.Response.Close();
                    continue;
                }

                // C-5: a path that answers only after a long delay, so a scrape with a short
                // client timeout can prove a host that does not respond in time is reported as a
                // TimeoutException. Answered on its own task: the accept loop must keep serving
                // the other paths while this one sleeps, or every test sharing the fixture stalls
                // behind it.
                if (ctx.Request.Url!.AbsolutePath == "/slow")
                {
                    var slow = ctx;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(3000);
                            var late = System.Text.Encoding.UTF8.GetBytes(
                                "<html><head><title>Slow</title></head><body><p>Eventually.</p></body></html>");
                            slow.Response.ContentType = "text/html";
                            await slow.Response.OutputStream.WriteAsync(late);
                            slow.Response.Close();
                        }
                        catch
                        {
                            // The client gave up (that is the point of this path) or the fixture
                            // was disposed while the delay was running.
                        }
                    });
                    continue;
                }

                // F9: a document nested N levels deep, so a scrape can prove that HTML deeper than
                // the converter's limit is refused with an answer rather than taken into a
                // recursive walk. The depth is in the path (/deep/301) so the test names the
                // number relative to WebService.MaxNestingDepth instead of the fixture hard-coding
                // one that can drift away from it.
                if (ctx.Request.Url!.AbsolutePath.StartsWith("/deep/", StringComparison.Ordinal)
                    && int.TryParse(ctx.Request.Url!.AbsolutePath["/deep/".Length..], out var levels)
                    && levels is > 0 and <= 100_000)
                {
                    var deep = System.Text.Encoding.UTF8.GetBytes(DeepPage(levels));
                    ctx.Response.ContentType = "text/html";
                    ctx.Response.OutputStream.Write(deep, 0, deep.Length);
                    ctx.Response.Close();
                    continue;
                }

                // C-5 GREEN: the request read back to the caller, so http_request's real service
                // path — headers forwarded, body sent, status and headers reported — can be proven
                // against a server instead of a mock. The method and a chosen request header come
                // back as RESPONSE headers; the request body comes back as the response body.
                if (ctx.Request.Url!.AbsolutePath == "/echo")
                {
                    string sent;
                    using (var reader = new StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8))
                        sent = reader.ReadToEnd();
                    ctx.Response.StatusCode = 201;
                    ctx.Response.AddHeader("X-Echo-Method", ctx.Request.HttpMethod);
                    ctx.Response.AddHeader("X-Echo-Auth", ctx.Request.Headers["Authorization"] ?? "(none)");
                    ctx.Response.AddHeader("X-Echo-Custom", ctx.Request.Headers["X-Custom"] ?? "(none)");
                    ctx.Response.AddHeader("X-Echo-Content-Type", ctx.Request.ContentType ?? "(none)");
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    var echoed = System.Text.Encoding.UTF8.GetBytes(sent);
                    ctx.Response.OutputStream.Write(echoed, 0, echoed.Length);
                    ctx.Response.Close();
                    continue;
                }

                var body = ctx.Request.Url!.AbsolutePath switch
                {
                    "/" => "<html><body><h1>Hello</h1></body></html>",
                    "/a5" => A5ProbePage,
                    "/one" => "<html><head><title>One</title></head><body><p>One</p></body></html>",
                    "/titled" => TitledPage,
                    _ => "404"
                };
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "text/html";
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
        });
    }

    public string UrlFor(string path) => BaseUrl + path;

    /// <summary>
    /// F9: a page whose body is <paramref name="levels"/> nested <c>&lt;div&gt;</c> elements around
    /// one word. Valid HTML that any browser renders — the depth is the only unusual thing about
    /// it, which is what makes it the input a converter that recurses per element cannot survive.
    /// </summary>
    internal static string DeepPage(int levels)
        => "<html><head><title>Deep</title></head><body>"
           + string.Concat(Enumerable.Repeat("<div>", levels))
           + "bottom"
           + string.Concat(Enumerable.Repeat("</div>", levels))
           + "</body></html>";

    /// <summary>
    /// C-5: a &lt;title&gt; that needs both of the rules the scrape result promises — an HTML entity
    /// to decode and whitespace (a newline, runs of spaces, leading and trailing padding) to
    /// collapse. Read as <c>A &amp; B C</c>.
    /// </summary>
    internal const string TitledPage =
        "<html><head><title>  A &amp; B\n  C </title></head><body><p>Body text.</p></body></html>";

    /// <summary>
    /// A-5 phase 1: the probe page <see cref="EdgeFixture"/> opens. One of each shape the DOM walk
    /// has to classify — Chromium maps them to Text / Hyperlink / Button / Edit / CheckBox /
    /// ComboBox / List+ListItem — a labelled input carrying a value, and a 3000 px spacer so the
    /// last paragraph is below the fold and must NOT appear in the page text until it is scrolled
    /// to (the off-screen rule the rest of the snapshot already applies, D-7).
    /// </summary>
    internal const string A5ProbePage = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>A5 Probe Page</title></head>
        <body>
          <h1>Probe heading</h1>
          <p>First paragraph of body text.</p>
          <p><span>inline span text</span></p>
          <p><a id="one" href="/one">A link to one</a></p>
          <p><button type="button">Press me</button></p>
          <p><label for="q">Search</label><input id="q" type="text" value="prefilled"></p>
          <p><label for="c">Tick</label><input id="c" type="checkbox" checked></p>
          <p><label for="s">Pick</label><select id="s"><option>Alpha</option><option>Beta</option></select></p>
          <ul><li>Item one</li><li>Item two</li></ul>
          <div style="height:3000px">tall spacer</div>
          <p>Last paragraph.</p>
        </body></html>
        """;

    public void Dispose()
    {
        _listener.Stop();
        try { _serverTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
    }
}
