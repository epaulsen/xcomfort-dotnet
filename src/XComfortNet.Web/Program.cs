using XComfortNet.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddOptions<XComfortOptions>().BindConfiguration("XComfort")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Host) && !string.IsNullOrWhiteSpace(options.AuthKey),
        "Configure XComfort:Host and XComfort:AuthKey in local appsettings.Development.json.")
    .ValidateOnStart();
builder.Services.AddOptions<MqttOptions>().BindConfiguration("Mqtt")
    .Validate(options => options.IsValid(), "Configure a valid MQTT host, port and topic prefix.")
    .ValidateOnStart();
builder.Services.AddSingleton<LightBridgeService>();
builder.Services.AddHostedService(services => services.GetRequiredService<LightBridgeService>());

var app = builder.Build();

app.MapGet("/", () => "XComfortNet");
app.MapHealthChecks("/health");
app.MapGet("/api/light", (LightBridgeService bridge) => bridge.Status);
app.MapGet("/api/lights", (LightBridgeService bridge) => bridge.Status);
app.MapGet("/api/devices", (LightBridgeService bridge) => bridge.Status);

app.Run();
