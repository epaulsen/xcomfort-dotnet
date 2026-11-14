var builder = DistributedApplication.CreateBuilder(args);

var mqtt = builder.AddContainer("mqtt", "eclipse-mosquitto", "2")
    .WithBindMount("mosquitto/mosquitto.conf", "/mosquitto/config/mosquitto.conf", isReadOnly: true)
    .WithVolume("xcomfort-mosquitto-data", "/mosquitto/data")
    .WithEndpoint(targetPort: 1883, scheme: "tcp", name: "tcp");

builder.AddContainer("homeassistant", "ghcr.io/home-assistant/home-assistant", "stable")
    .WithBindMount("homeassistant", "/config")
    .WithHttpEndpoint(port: 8123, targetPort: 8123, name: "http")
    .WithHttpHealthCheck("/")
    .WaitFor(mqtt);

var mqttEndpoint = mqtt.GetEndpoint("tcp");

builder.AddProject<Projects.XComfortNet_Web>("web", launchProfileName: "http")
    .WithEnvironment("Mqtt__Host", mqttEndpoint.Property(EndpointProperty.Host))
    .WithEnvironment("Mqtt__Port", mqttEndpoint.Property(EndpointProperty.Port))
    .WithHttpHealthCheck("/health")
    .WaitFor(mqtt);

builder.Build().Run();
