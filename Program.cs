using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

// Store active connections (in production, use proper session management)
var connections = new Dictionary<string, WebSocket>();

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket endpoint");
        return;
    }

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    var sessionId = Guid.NewGuid().ToString();
    connections[sessionId] = webSocket;
    
    Console.WriteLine($"Client connected: {sessionId}");

    var buffer = new byte[8192];
    var seg = new ArraySegment<byte>(buffer);

    try
    {
        while (webSocket.State == WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(seg, CancellationToken.None);
            
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                Console.WriteLine($"Client disconnected: {sessionId}");
                break;
            }

            var received = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Console.WriteLine($"Received from {sessionId}: {received}");

            // Parse message
            try
            {
                var message = JsonSerializer.Deserialize<JsonElement>(received);
                var messageType = message.GetProperty("type").GetString();

                // Route message to other connected clients (browser)
                // In production, implement proper session pairing
                foreach (var (id, ws) in connections)
                {
                    if (id != sessionId && ws.State == WebSocketState.Open)
                    {
                        var outgoing = Encoding.UTF8.GetBytes(received);
                        await ws.SendAsync(new ArraySegment<byte>(outgoing), WebSocketMessageType.Text, true, CancellationToken.None);
                        Console.WriteLine($"Forwarded {messageType} from {sessionId} to {id}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing message: {ex.Message}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"WebSocket error: {ex.Message}");
    }
    finally
    {
        connections.Remove(sessionId);
    }
});

app.Run("http://0.0.0.0:5050");
