using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenMind.CRM.Application.Services.Interfaces;
using OpenMind.CRM.Application.Exceptions;
using System.Security.Claims;
using OpenMind.CRM.Application.Dtos;

namespace OpenMind.CRM.API.Controllers;

[ApiController]
[Route("api/google")]
[Authorize]
public class GoogleOAuthController(IGoogleOAuthIntegrationService googleService) : ControllerBase
{
    [HttpGet("authorize")]
    public ActionResult<AuthUrlResponse> GetAuthorizationUrl()
    {
        if (!Authorize(out var userId, out var unauthorizedResult))
        {
            return unauthorizedResult;
        }

        var authUrl = googleService.GenerateAuthorizationUrl(userId);
        
        return Ok(new AuthUrlResponse
        {
            AuthorizationUrl = authUrl,
            State = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(userId.ToString())),
            Provider = ((IOAuthService)googleService).ProviderName
        });
    }

    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> HandleCallback([FromQuery] string code, [FromQuery] string state)
    {
        var redirectUrl = await googleService.HandleAuthorizationCallbackAsync(code, state);
        return Redirect(redirectUrl);
    }

    [HttpGet("emails")]
    public async Task<ActionResult<List<EmailDto>>> GetEmails([FromQuery] int maxResults = 10)
    {
        if (!Authorize(out var userId, out var unauthorizedResult))
        {
            return unauthorizedResult;
        }

        try
        {
            var emails = await googleService.GetEmailsAsync(userId, maxResults);
            return Ok(emails);
        }
        catch (OAuthTokenExpiredException ex)
        {
            return StatusCode(403, new { 
                error = "token_expired", 
                message = ex.Message, 
                provider = ex.Provider,
                requiresReauthorization = true 
            });
        }
    }

    [HttpGet("calendar/events")]
    public async Task<ActionResult<List<CalendarEventDto>>> GetCalendarEvents(
        [FromQuery] DateTime? timeMin = null,
        [FromQuery] DateTime? timeMax = null,
        [FromQuery] int maxResults = 50)
    {
        if (!Authorize(out var userId, out var unauthorizedResult))
        {
            return unauthorizedResult;
        }

        try
        {
            var events = await googleService.GetCalendarEventsAsync(userId, timeMin, timeMax, maxResults);
            return Ok(events);
        }
        catch (OAuthTokenExpiredException ex)
        {
            return StatusCode(403, new { 
                error = "token_expired", 
                message = ex.Message, 
                provider = ex.Provider,
                requiresReauthorization = true 
            });
        }
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        if (!Authorize(out var userId, out var unauthorizedResult))
        {
            return unauthorizedResult;
        }

        var hasValidToken = await googleService.HasValidTokenAsync(userId);
        return Ok(new { Provider = ((IOAuthService)googleService).ProviderName, IsConnected = hasValidToken });
    }

    [HttpDelete("revoke")]
    public async Task<IActionResult> RevokeAccess()
    {
        if (!Authorize(out var userId, out var unauthorizedResult))
        {
            return unauthorizedResult;
        }

        var success = await googleService.RevokeTokenAsync(userId);
        return success ? Ok() : BadRequest("Failed to revoke Google access");
    }
    
    private bool Authorize(out int userId, out ActionResult unauthorizedResult)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim != null && int.TryParse(userIdClaim, out userId))
        {
            unauthorizedResult = null!;
            return true;
        }
        userId = 0;
        unauthorizedResult = Unauthorized();
        return false;
    }
}