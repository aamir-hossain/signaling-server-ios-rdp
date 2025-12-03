using System.Collections.Concurrent; // For thread-safe dictionary
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

// Store active connections (in production, use proper session management)
// Using ConcurrentDictionary for thread safety with multiple connections
var connections = new ConcurrentDictionary<string, WebSocket>();

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
    connections.TryAdd(sessionId, webSocket);
    
    var remoteIpAddress = context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    
    Console.WriteLine("========================================");
    Console.WriteLine($"✅ CLIENT CONNECTED: {sessionId}");
    Console.WriteLine($"📊 Total connections: {connections.Count}");
    Console.WriteLine($"🌐 Remote endpoint: {remoteIpAddress}");
    Console.WriteLine("========================================");

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
                connections.TryRemove(sessionId, out _); // Use TryRemove for ConcurrentDictionary
                Console.WriteLine("========================================");
                Console.WriteLine($"🔌 CLIENT DISCONNECTED: {sessionId}");
                Console.WriteLine($"📊 Remaining connections: {connections.Count}");
                Console.WriteLine("========================================");
                break;
            }

            var received = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Console.WriteLine("========================================");
            Console.WriteLine($"📩 MESSAGE RECEIVED from {sessionId}");
            Console.WriteLine($"📋 Message length: {received.Length} bytes");
            Console.WriteLine($"📋 Raw message (first 200 chars): {received.Substring(0, Math.Min(received.Length, 200))}...");
            
            // Parse message
            try
            {
                var message = JsonSerializer.Deserialize<JsonElement>(received);
                var messageType = message.GetProperty("type").GetString();
                Console.WriteLine($"📋 Message Type: {messageType}");
                
                if (message.TryGetProperty("sdp", out var sdpElement))
                {
                    var sdp = sdpElement.GetString();
                    Console.WriteLine($"📋 SDP Length: {sdp?.Length ?? 0} characters");
                    if (sdp != null && sdp.Contains("m=video"))
                    {
                        Console.WriteLine($"✅ SDP contains video media line (m=video)");
                    }
                }
                
                if (message.TryGetProperty("candidate", out var candidateElement))
                {
                    Console.WriteLine($"📋 ICE Candidate present");
                }

                // Optional: log remote-control style messages (input/command/diagnostics)
                if (message.TryGetProperty("input", out var inputElement) && inputElement.ValueKind != JsonValueKind.Null)
                {
                    Console.WriteLine("🎮 Remote input payload present");
                }
                if (message.TryGetProperty("command", out var commandElement) && commandElement.ValueKind != JsonValueKind.Null)
                {
                    Console.WriteLine("🧭 Remote command payload present");
                }
                if (message.TryGetProperty("diagnostics", out var diagElement) && diagElement.ValueKind != JsonValueKind.Null)
                {
                    Console.WriteLine("🩺 Diagnostics payload present");
                }

                // Route message to other connected clients (browser/app/broadcast)
                // In production, implement proper session pairing / roles
                int forwardedCount = 0;
                foreach (var (id, ws) in connections)
                {
                    if (id != sessionId && ws.State == WebSocketState.Open)
                    {
                        var outgoing = Encoding.UTF8.GetBytes(received);
                        await ws.SendAsync(new ArraySegment<byte>(outgoing), WebSocketMessageType.Text, true, CancellationToken.None);
                        Console.WriteLine($"📤 Forwarded {messageType} from {sessionId} to {id}");
                        forwardedCount++;
                    }
                }
                
                if (forwardedCount == 0)
                {
                    Console.WriteLine($"⚠️ No other clients connected to forward message to");
                    Console.WriteLine($"⚠️ Current connections: {connections.Count}");
                    foreach (var (id, ws) in connections)
                    {
                        Console.WriteLine($"   - {id}: State={ws.State}");
                    }
                }
                Console.WriteLine("========================================");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ERROR processing message: {ex.Message}");
                Console.WriteLine($"❌ Stack trace: {ex.StackTrace}");
                Console.WriteLine("========================================");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"WebSocket error: {ex.Message}");
    }
    finally
    {
        connections.TryRemove(sessionId, out _); // Ensure removal on any exit
        Console.WriteLine("========================================");
        Console.WriteLine($"🔌 CLIENT DISCONNECTED (finally block): {sessionId}");
        Console.WriteLine($"📊 Remaining connections: {connections.Count}");
        Console.WriteLine("========================================");
    }
});

app.Run("http://0.0.0.0:5050");

