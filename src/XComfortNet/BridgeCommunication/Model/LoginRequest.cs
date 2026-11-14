using System.Text.Json.Serialization;

namespace XComfortDotNet.Model;

public class LoginRequest
{
    [JsonPropertyName("username")]
    public required string Username { get; set; }

    [JsonPropertyName("password")]
    public required string Password { get; set; }

    [JsonPropertyName("salt")]
    public required string Salt { get; set; }
}

public class TokenMessage
{
    [JsonPropertyName("token")]
    public required string Token { get; set; }
}