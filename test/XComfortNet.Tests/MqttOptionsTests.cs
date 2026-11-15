using System.Text;
using Microsoft.Extensions.Configuration;
using XComfortNet.Web;

namespace XComfortNet.Tests;

public class MqttOptionsTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void LeavesAnonymousConnectionsWithoutCredentials(string? username, string? password)
    {
        var options = new MqttOptions { Username = username, Password = password };

        Assert.True(options.IsValid());
        Assert.Null(options.BuildClientOptions("test/availability").Credentials);
    }

    [Theory]
    [InlineData("mqtt-user", "secret")]
    [InlineData("bruker", "påssord")]
    [InlineData("mqtt-user", "")]
    [InlineData("mqtt-user", null)]
    public void ConfiguresCredentialsFromSettings(string username, string? password)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Mqtt:Username"] = username,
                ["Mqtt:Password"] = password
            }).Build();
        var options = new MqttOptions();
        configuration.GetSection("Mqtt").Bind(options);

        Assert.True(options.IsValid());
        var clientOptions = options.BuildClientOptions("test/availability");
        var credentials = Assert.IsAssignableFrom<MQTTnet.IMqttClientCredentialsProvider>(clientOptions.Credentials);
        Assert.Equal(username, credentials.GetUserName(clientOptions));
        var bytes = credentials.GetPassword(clientOptions);
        Assert.Equal(password, bytes is null ? null : Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RejectsPasswordWithoutUsername(string? username)
    {
        var options = new MqttOptions { Username = username, Password = "secret" };

        Assert.False(options.IsValid());
    }
}
