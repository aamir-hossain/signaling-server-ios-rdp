using System.Net;
using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket endpoint");
        return;
    }

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[8192];
    var seg = new ArraySegment<byte>(buffer);

    Console.WriteLine("Client connected to /ws");

    while (webSocket.State == WebSocketState.Open)
    {
        var result = await webSocket.ReceiveAsync(seg, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            Console.WriteLine("Client disconnected");
            break;
        }

        var received = Encoding.UTF8.GetString(buffer, 0, result.Count);
        Console.WriteLine($"Received: {received}");

        // Echo back (or implement signaling routing)
        var outgoing = Encoding.UTF8.GetBytes($"ack: {received}");
        await webSocket.SendAsync(new ArraySegment<byte>(outgoing), WebSocketMessageType.Text, true, CancellationToken.None);
    }
});

app.Run("http://0.0.0.0:5050");
