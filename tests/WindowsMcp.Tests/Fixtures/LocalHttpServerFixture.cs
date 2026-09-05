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
                var body = ctx.Request.Url!.AbsolutePath switch
                {
                    "/" => "<html><body><h1>Hello</h1></body></html>",
                    "/a5" => A5ProbePage,
                    "/one" => "<html><head><title>One</title></head><body><p>One</p></body></html>",
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
