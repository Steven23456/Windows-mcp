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

    public void Dispose()
    {
        _listener.Stop();
        try { _serverTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
    }
}
