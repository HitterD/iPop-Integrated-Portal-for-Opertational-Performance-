var builder = WebApplication.CreateBuilder(args);
// Serves IPop.Web's wwwroot + _framework straight from the build output in every environment
// (the default only wires this up in Development).
builder.WebHost.UseStaticWebAssets();
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

var app = builder.Build();
IPop.WebHost.HostPipeline.Configure(app);
app.Run();
