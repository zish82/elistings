using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Client;
using Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Use an explicit API base URL in development when the client runs on a different port.
var configuredApiBaseUrl = builder.Configuration["ApiBaseUrl"];
var apiBaseUri = !string.IsNullOrWhiteSpace(configuredApiBaseUrl)
	? new Uri(configuredApiBaseUrl, UriKind.Absolute)
	: new Uri(builder.HostEnvironment.BaseAddress, UriKind.Absolute);

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = apiBaseUri });
builder.Services.AddScoped<ListingService>();

await builder.Build().RunAsync();
