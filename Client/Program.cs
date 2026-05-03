using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Client;
using Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Use configured API base when provided; support both absolute and relative values.
var configuredApiBaseUrl = builder.Configuration["ApiBaseUrl"];
var hostBaseUri = new Uri(builder.HostEnvironment.BaseAddress, UriKind.Absolute);
var fallbackLocalApiUri = new Uri("http://localhost:5197/", UriKind.Absolute);
Uri apiBaseUri;

if (string.IsNullOrWhiteSpace(configuredApiBaseUrl))
{
	apiBaseUri = hostBaseUri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
		? fallbackLocalApiUri
		: hostBaseUri;
}
else if (Uri.TryCreate(configuredApiBaseUrl, UriKind.Absolute, out var absoluteApiUri))
{
	apiBaseUri = absoluteApiUri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
		? fallbackLocalApiUri
		: absoluteApiUri;
}
else
{
	apiBaseUri = hostBaseUri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
		? fallbackLocalApiUri
		: new Uri(hostBaseUri, configuredApiBaseUrl);
}

if (hostBaseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
	&& apiBaseUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
{
	apiBaseUri = new Uri(hostBaseUri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
}

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = apiBaseUri });
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ListingService>();

await builder.Build().RunAsync();
