using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OpenMind.CRM.Application.Services.Interfaces;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace OpenMind.CRM.Application.Services;

public class JwtTokenService(IConfiguration configuration, ILogger<JwtTokenService> logger) : IJwtTokenService
{
    private readonly string _secretKey = configuration["JWT:Secret"] ?? throw new InvalidOperationException("JWT Secret is not configured");
    private readonly string _issuer = configuration["JWT:Issuer"] ?? throw new InvalidOperationException("JWT Issuer is not configured");
    private readonly string _audience = configuration["JWT:Audience"] ?? throw new InvalidOperationException("JWT Audience is not configured");
    private readonly int _expirationHours = int.Parse(configuration["JWT:ExpirationHours"] ?? "24");

    public string GenerateToken(int userId, string email)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Email, email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_expirationHours),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public int? GetUserIdFromToken(string token)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(_secretKey);

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);
            var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return int.TryParse(userIdClaim, out var userId) ? userId : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating JWT token");
            return null;
        }
    }

    public bool ValidateToken(string token)
    {
        return GetUserIdFromToken(token).HasValue;
    }
}