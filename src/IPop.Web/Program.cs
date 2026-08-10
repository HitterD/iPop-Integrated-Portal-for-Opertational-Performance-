using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using IPop.Web;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.Configuration["ApiBase"] ?? builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<IPop.Web.Services.AuthState>();
builder.Services.AddRadzenComponents();

await builder.Build().RunAsync();
