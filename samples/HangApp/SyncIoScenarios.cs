using System.Net;
using System.Net.Sockets;

namespace HangApp;

/// Thread pool work that reads from a socket synchronously, from a mail server that never sends its greeting:
/// every pool thread ends up parked in Socket.Receive and the rest of the queue never runs.
static class SyncIoStarvationScenario
{
    public static void Run()
    {
        ThreadPool.SetMinThreads(8, 8);
        ThreadPool.SetMaxThreads(8, 8);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var servers = new List<Socket>();
        for (var i = 0; i < 12; i++)
        {
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(listener.LocalEndpoint);
            // The server side of the connection is kept open but never writes anything.
            servers.Add(listener.AcceptSocket());
            var feed = new MailFeed(client);
            ThreadPool.QueueUserWorkItem(_ => feed.ReadGreeting());
        }
        Ready.SignalLater(1500);
        Thread.Sleep(Timeout.Infinite);
        GC.KeepAlive(servers);
    }

    private sealed class MailFeed(Socket socket)
    {
        public int ReadGreeting()
        {
            var buffer = new byte[512];
            return socket.Receive(buffer);
        }
    }
}
