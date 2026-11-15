using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeadlockLab.Http;

/// A tiny HTTP server on localhost, so the HTTP scenarios need nothing outside the process.
/// It can answer normally, or start a big response and never finish it, or never answer at all.
internal sealed class LabServer
{
    public enum Mode { Answer, StallBody, Silent }

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Mode _mode;
    private readonly List<TcpClient> _keep = [];

    public LabServer(Mode mode)
    {
        _mode = mode;
        _listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "lab-http-server" }.Start();
    }

    public Uri BaseAddress => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

    private void AcceptLoop()
    {
        while (true)
        {
            var client = _listener.AcceptTcpClient();
            lock (_keep) _keep.Add(client);
            ThreadPool.UnsafeQueueUserWorkItem(_ => Serve(client), null);
        }
    }

    private void Serve(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];
            while (true)
            {
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n"))
                {
                    var n = stream.Read(buffer);
                    if (n == 0) return;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }
                switch (_mode)
                {
                    case Mode.Silent:
                        return; // read the request, never answer, keep the socket open
                    case Mode.StallBody when request.ToString().Contains("/products/"):
                        // Promise a large body, send a little of it, and stop: the client's connection stays busy
                        // until someone reads (or disposes) the response.
                        stream.Write(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 1000000\r\n\r\n{\"id\":1,"));
                        return;
                    default:
                        var body = "{\"ok\":true}";
                        stream.Write(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}"));
                        break;
                }
            }
        }
        catch (IOException)
        {
        }
    }
}
