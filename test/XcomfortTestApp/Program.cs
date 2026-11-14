// See https://aka.ms/new-console-template for more information

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using XComfortDotNet;

Console.WriteLine("Hello, World!");

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddJsonFile("appsettings.Development.json")
    .Build();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConfiguration(config.GetSection("Logging"));
    builder.AddConsole();
});

var ip = config["XComfort:Host"];
var authKey = config["XComfort:AuthKey"];
if (string.IsNullOrWhiteSpace(ip))
{
    throw new InvalidOperationException("Missing required configuration value 'XComfort:Host'.");
}
if (string.IsNullOrWhiteSpace(authKey))
{
    throw new InvalidOperationException("Missing required configuration value 'XComfort:AuthKey'.");
}

await using var bridge = new XComfortBridge(ip, authKey, loggerFactory);
await bridge.ConnectAsync();

await bridge.ReadAllDataAsync();

await Task.Delay(TimeSpan.FromMinutes(10));
