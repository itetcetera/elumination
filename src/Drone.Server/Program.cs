using Drone.Server.Hosting;
using Drone.Server.Hubs;
using Drone.Server.Identity;
using Drone.Server.Rooms;
using Drone.Server.Wire;
using Microsoft.Extensions.FileProviders;

// DRONE's game server: rooms with invite codes, the browsers' SignalR hub at /hub, and the client's
// files. The rules come from settings/drone.json (found above the app, or in DRONE_SETTINGS_DIR).
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RoomRegistry>();
builder.Services.AddSingleton<IIdentityService, GuestIdentityService>();
builder.Services.Configure<TickOptions>(builder.Configuration.GetSection("Ticks"));
builder.Services.AddHostedService<RoomTicker>();
builder.Services.AddSignalR().AddJsonProtocol(o => EventWire.Configure(o.PayloadSerializerOptions));
// A client dev server runs on another port.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(origin => new Uri(origin).Host is "localhost" or "127.0.0.1")
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
app.UseCors();
// The browser client: the folder in "ClientPath", else client/ at the repository root, else wwwroot.
var clientPath = builder.Configuration["ClientPath"]
    ?? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "client"));
var files = Directory.Exists(clientPath) ? new PhysicalFileProvider(clientPath) : builder.Environment.WebRootFileProvider;
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
app.MapHub<GameHub>("/hub");
app.MapGet("/health", () => "ok");
// Invite links (/r/ABC123) open the client, which reads the code from the address.
app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });
app.Run();

public partial class Program;
