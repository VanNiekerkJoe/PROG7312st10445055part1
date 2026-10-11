using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SmartX.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Points at the SmartX.Api project. Change this if you run the API on a
// different port; check the console output when the API starts up.
const string apiBaseAddress = "https://localhost:5001";

builder.RootComponents.Add<SmartX.Client.App>("#app");

builder.Services.AddSingleton(_ => new TelemetryApiClient(apiBaseAddress));
builder.Services.AddSingleton(_ => new TelemetryHubClient($"{apiBaseAddress}/hubs/telemetry"));

// Part 2: command stream clients, plus one app-lifetime store that keeps
// device/command/feed state alive while the user navigates between pages.
builder.Services.AddSingleton(_ => new CommandApiClient(apiBaseAddress));
builder.Services.AddSingleton(_ => new CommandHubClient($"{apiBaseAddress}/hubs/commands"));
builder.Services.AddSingleton<LiveStateService>();

await builder.Build().RunAsync();
