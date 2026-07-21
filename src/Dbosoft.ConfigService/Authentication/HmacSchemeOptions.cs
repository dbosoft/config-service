using Microsoft.AspNetCore.Authentication;

namespace Dbosoft.ConfigService.Authentication;

public class HmacSchemeOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// Maximum allowed clock skew between client and server when validating
    /// the request date header. Defaults to the
    /// <see cref="ConfigServiceOptions.MaxClockSkew"/> value at startup.
    /// </summary>
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(15);
}
