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
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Trace);
});

var ip = config["XComfort:Host"];
var authKey = config["XComfort:AuthKey"];


var bridge = await new XComfortBridgeFactory(ip,authKey,loggerFactory).CreateBridgeAsync();
await Task.Delay(TimeSpan.FromSeconds(1));

await bridge.ReadAllDataAsync();

await Task.Delay(TimeSpan.FromMinutes(10));
