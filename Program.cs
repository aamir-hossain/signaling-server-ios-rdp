using System.Collections.Concurrent; // For thread-safe dictionary
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

// Simple HTTP health endpoint to verify device-to-Mac reachability (firewall/Wi‑Fi).
// This does not participate in WebSocket signaling; it is purely for testing.
app.MapGet("/health", (HttpContext ctx) =>
{
    var remoteIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    return Results.Json(new
    {
        ok = true,
        serverTime = DateTimeOffset.UtcNow,
        remoteIp,
        note = "If you can load this from iPhone Safari, the network path is good."
    });
});

// Store active connections (in production, use proper session management)
// Using ConcurrentDictionary for thread safety with multiple connections
//
// Room routing:
// - Connections are grouped by roomId to prevent cross-talk between parallel sessions.
// - roomId is taken from the websocket request query string: /ws?roomId=abc
// - If omitted, we default to "default" for backward compatibility.
//
// Additionally, we maintain a lightweight "role" per connection:
// - web: browser client (Mac/Windows)
// - app: iOS main app (remote input/commands)
// - broadcast: iOS broadcast extension (WebRTC offer/candidates)
//
// Clients announce role by sending a JSON message: { "type":"hello", "role":"web" }
// This enables strict 1:1 routing within a room:
// - WebRTC signaling (offer/answer/candidate/control) routes only between web <-> broadcast
// - Remote-control messages (input/command/diagnostics) route only between web <-> app
var connections = new ConcurrentDictionary<string, (string RoomId, string Role, WebSocket Socket, string RemoteIp)>();

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
    
    var remoteIpAddress = context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    var roomId = context.Request.Query.TryGetValue("roomId", out var roomValues)
        ? (roomValues.ToString() ?? string.Empty).Trim()
        : string.Empty;
    if (string.IsNullOrWhiteSpace(roomId))
    {
        // Backward compatible default when client doesn't provide room.
        roomId = "default";
    }
    
    connections.TryAdd(sessionId, (roomId, "unknown", webSocket, remoteIpAddress));
    
    Console.WriteLine("========================================");
    Console.WriteLine($"✅ CLIENT CONNECTED: {sessionId}");
    Console.WriteLine($"🏷️ Room: {roomId}");
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
                Console.WriteLine($"🏷️ Room: {roomId}");
                Console.WriteLine($"📊 Remaining connections: {connections.Count}");
                Console.WriteLine("========================================");
                break;
            }

            var received = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Console.WriteLine("========================================");
            Console.WriteLine($"📩 MESSAGE RECEIVED from {sessionId}");
            Console.WriteLine($"🏷️ Room: {roomId}");
            Console.WriteLine($"📋 Message length: {received.Length} bytes");
            Console.WriteLine($"📋 Raw message (first 200 chars): {received.Substring(0, Math.Min(received.Length, 200))}...");
            
            // Parse message
            try
            {
                var message = JsonSerializer.Deserialize<JsonElement>(received);
                var messageType = message.GetProperty("type").GetString();
                Console.WriteLine($"📋 Message Type: {messageType}");
                
                // Handle role registration
                if (string.Equals(messageType, "hello", StringComparison.OrdinalIgnoreCase))
                {
                    var role = message.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : null;
                    role = (role ?? string.Empty).Trim().ToLowerInvariant();
                    if (role is not ("web" or "app" or "broadcast"))
                    {
                        Console.WriteLine($"⚠️ Invalid role in hello: '{role}'. Expected web|app|broadcast");
                    }
                    else
                    {
                        // Enforce one connection per role per room (replace older one).
                        foreach (var (id, info) in connections)
                        {
                            if (id == sessionId) continue;
                            if (info.RoomId == roomId && info.Role == role && info.Socket.State == WebSocketState.Open)
                            {
                                Console.WriteLine($"⚠️ Replacing existing role '{role}' in room '{roomId}'. Old session: {id}");
                                try
                                {
                                    await info.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "duplicate role in room", CancellationToken.None);
                                }
                                catch { /* ignore */ }
                                connections.TryRemove(id, out _);
                            }
                        }
                        
                        // Update role for current connection
                        connections.AddOrUpdate(
                            sessionId,
                            _ => (roomId, role, webSocket, remoteIpAddress),
                            (_, existing) => (existing.RoomId, role, existing.Socket, existing.RemoteIp)
                        );
                        Console.WriteLine($"👋 Role registered: {role} (room={roomId}, session={sessionId})");
                    }
                    
                    // Don't forward hello messages.
                    Console.WriteLine("========================================");
                    continue;
                }
                
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

                // Route message to other connected clients in the SAME ROOM, with strict role-based routing.
                int forwardedCount = 0;
                
                // Determine source role
                var sourceRole = connections.TryGetValue(sessionId, out var sourceInfo)
                    ? sourceInfo.Role
                    : "unknown";
                
                // Determine which target role we should forward to, based on message type
                // WebRTC signaling:
                var isWebRtc = messageType is "offer" or "answer" or "candidate" or "control";
                // Remote-control:
                var isRemoteControl = messageType is "input" or "command" or "diagnostics";
                
                string? targetRole = null;
                if (isWebRtc)
                {
                    targetRole = sourceRole == "web" ? "broadcast" : (sourceRole == "broadcast" ? "web" : null);
                }
                else if (isRemoteControl)
                {
                    targetRole = sourceRole == "web" ? "app" : (sourceRole == "app" ? "web" : null);
                }
                
                if (targetRole == null)
                {
                    Console.WriteLine($"⚠️ Not forwarding message type '{messageType}' from role '{sourceRole}'.");
                    Console.WriteLine("========================================");
                    continue;
                }
                
                foreach (var (id, info) in connections)
                {
                    if (id != sessionId &&
                        info.RoomId == roomId &&
                        info.Role == targetRole &&
                        info.Socket.State == WebSocketState.Open)
                    {
                        var outgoing = Encoding.UTF8.GetBytes(received);
                        await info.Socket.SendAsync(new ArraySegment<byte>(outgoing), WebSocketMessageType.Text, true, CancellationToken.None);
                        Console.WriteLine($"📤 Forwarded {messageType} from {sessionId}({sourceRole}) to {id}({targetRole}) (room={roomId})");
                        forwardedCount++;
                    }
                }
                
                if (forwardedCount == 0)
                {
                    Console.WriteLine($"⚠️ No other clients connected to forward message to");
                    Console.WriteLine($"⚠️ Current connections: {connections.Count}");
                    foreach (var (id, info) in connections)
                    {
                        Console.WriteLine($"   - {id}: Room={info.RoomId} Role={info.Role} State={info.Socket.State} Remote={info.RemoteIp}");
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
        Console.WriteLine($"🏷️ Room: {roomId}");
        Console.WriteLine($"📊 Remaining connections: {connections.Count}");
        Console.WriteLine("========================================");
    }
});

app.Run("http://0.0.0.0:5050");

