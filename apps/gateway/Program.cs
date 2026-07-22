// Gateway: accounts, character storage, and routing between world-servers.
// Phase 3 fills this in — for now it exposes health and a stub world registry
// so the client's connection flow has something to talk to.

using Ashfall.Proto;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", proto = ProtocolVersion.Current }));

// Stub registry. Phase 3 replaces this with live world-server registration.
app.MapGet("/worlds", () => Results.Ok(new[]
{
    new { id = "continent-a", host = "127.0.0.1", port = 9050 },
}));

app.Run();
