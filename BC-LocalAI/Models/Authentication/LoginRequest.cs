using System.Diagnostics;
using System.Text.Json.Serialization;

namespace BC_LocalAI.Models.Authentication;

/// <summary>Inbound-only credentials; never serialize/log/destructure this object.</summary>
[DebuggerDisplay("LoginRequest [REDACTED]")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LoginRequest
{
    [JsonPropertyName("username")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string? Username { get; init; }

    [JsonPropertyName("password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string? Password { get; init; }

    public override string ToString() => "LoginRequest [REDACTED]";
}
