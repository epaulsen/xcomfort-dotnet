var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/", () => "XComfortNet");
app.MapHealthChecks("/health");

app.Run();
