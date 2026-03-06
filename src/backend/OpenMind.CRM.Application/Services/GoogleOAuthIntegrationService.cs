using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenMind.CRM.Application.Services.Interfaces;
using OpenMind.CRM.Application.Exceptions;
using OpenMind.CRM.Domain.Entities;
using System.Text;
using OpenMind.CRM.Application.Dtos;

namespace OpenMind.CRM.Application.Services;

public class GoogleOAuthIntegrationService : IGoogleOAuthIntegrationService
{
    private readonly IUserRepository _userRepository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GoogleOAuthIntegrationService> _logger;
    private readonly GoogleAuthorizationCodeFlow _authFlow;

    private static readonly string[] GoogleScopes =
    [
        GmailService.Scope.GmailReadonly,
        CalendarService.Scope.CalendarEventsReadonly
    ];

    public string ProviderName => "Google";

    public GoogleOAuthIntegrationService(IUserRepository userRepository, IConfiguration configuration, ILogger<GoogleOAuthIntegrationService> logger)
    {
        _userRepository = userRepository;
        _configuration = configuration;
        _logger = logger;

        _authFlow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = _configuration["Google:ClientId"],
                ClientSecret = _configuration["Google:ClientSecret"]
            },
            Scopes = GoogleScopes,
            DataStore = null
        });
    }

    public string GenerateAuthorizationUrl(int userId)
    {
        var redirectUri = _configuration["Google:RedirectUri"];
        var state = Convert.ToBase64String(Encoding.UTF8.GetBytes(userId.ToString()));

        var request = _authFlow.CreateAuthorizationCodeRequest(redirectUri);
        request.State = state;
        
        var authUrl = request.Build().ToString();
        
        // Add access_type=offline to get refresh token
        // Add prompt=consent to force consent screen and always get refresh token
        if (!authUrl.Contains("access_type="))
        {
            authUrl += "&access_type=offline";
        }
        if (!authUrl.Contains("prompt="))
        {
            authUrl += "&prompt=consent";
        }

        return authUrl;
    }

    public async Task<string> HandleAuthorizationCallbackAsync(string code, string state)
    {
        var successUrl = _configuration["Auth:RedirectUrls:Google:Success"] ?? throw new ArgumentNullException();
        var errorUrl = _configuration["Auth:RedirectUrls:Google:Error"] ?? throw new ArgumentNullException();
        
        try
        {
            var userIdBytes = Convert.FromBase64String(state);
            var userIdString = Encoding.UTF8.GetString(userIdBytes);
            if (!int.TryParse(userIdString, out var userId))
            {
                return errorUrl;
            }

            var redirectUri = _configuration["Google:RedirectUri"];

            var token = await _authFlow.ExchangeCodeForTokenAsync(userId.ToString(), code, redirectUri, CancellationToken.None);

            var existingToken = await _userRepository.GetOAuthTokenAsync(userId, this.ProviderName);

            if (existingToken != null)
            {
                existingToken.AccessToken = token.AccessToken;
                existingToken.RefreshToken = token.RefreshToken ?? existingToken.RefreshToken;
                existingToken.ExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresInSeconds ?? 3600);
                existingToken.UpdatedAt = DateTime.UtcNow;
                await _userRepository.UpdateOAuthTokenAsync(existingToken);
            }
            else
            {
                var oauthToken = new OAuthToken
                {
                    UserId = userId,
                    Provider = this.ProviderName,
                    AccessToken = token.AccessToken,
                    RefreshToken = token.RefreshToken ?? string.Empty,
                    ExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresInSeconds ?? 3600),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Scopes = string.Join(",", GoogleScopes)
                };
                
                await _userRepository.SaveOAuthTokenAsync(oauthToken);
            }

            return successUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Google authorization callback");
            return errorUrl;
        }
    }

    private async Task<List<GoogleEmailDto>> GetGoogleEmailsInternalAsync(int userId, int maxResults = 10)
    {
        var credential = await GetUserCredentialAsync(userId);
        if (credential == null)
        {
            return new List<GoogleEmailDto>();
        }

        var service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "OpenMind CRM"
        });

        try
        {
            var request = service.Users.Messages.List("me");
            request.MaxResults = maxResults;
            request.Q = "in:inbox";

            var response = await request.ExecuteAsync();
            var emails = new List<GoogleEmailDto>();

            if (response.Messages != null)
            {
                foreach (var message in response.Messages)
                {
                    var messageRequest = service.Users.Messages.Get("me", message.Id);
                    messageRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
                    var messageDetail = await messageRequest.ExecuteAsync();

                    var email = new GoogleEmailDto
                    {
                        Id = messageDetail.Id,
                        ThreadId = messageDetail.ThreadId
                    };

                    if (messageDetail.Payload?.Headers != null)
                    {
                        foreach (var header in messageDetail.Payload.Headers)
                        {
                            switch (header.Name?.ToLower())
                            {
                                case "subject":
                                    email.Subject = header.Value ?? "";
                                    break;
                                case "from":
                                    email.From = header.Value ?? "";
                                    break;
                                case "to":
                                    email.To = header.Value ?? "";
                                    break;
                                case "date":
                                    if (DateTime.TryParse(header.Value, out var date))
                                        email.Date = date;
                                    break;
                            }
                        }
                    }

                    email.Body = ExtractEmailBody(messageDetail.Payload ?? new Google.Apis.Gmail.v1.Data.MessagePart());
                    email.IsRead = !messageDetail.LabelIds?.Contains("UNREAD") ?? true;

                    emails.Add(email);
                }
            }

            return emails;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching emails for user {UserId}", userId);
            return [];
        }
    }

    private async Task<List<GoogleCalendarEventDto>> GetGoogleCalendarEventsInternalAsync(int userId, DateTime? timeMin = null, DateTime? timeMax = null)
    {
        var credential = await GetUserCredentialAsync(userId);
        if (credential == null)
        {
            return [];
        }

        var service = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "OpenMind CRM"
        });

        try
        {
            var request = service.Events.List("primary");
            request.TimeMinDateTimeOffset = timeMin ?? DateTime.UtcNow;
            request.TimeMaxDateTimeOffset = timeMax ?? DateTime.UtcNow.AddDays(30);
            request.ShowDeleted = false;
            request.SingleEvents = true;
            request.MaxResults = 50;
            request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

            var events = await request.ExecuteAsync();
            var calendarEvents = new List<GoogleCalendarEventDto>();

            if (events.Items != null)
            {
                foreach (var eventItem in events.Items)
                {
                    var calendarEvent = new GoogleCalendarEventDto
                    {
                        Id = eventItem.Id,
                        Summary = eventItem.Summary ?? "",
                        Description = eventItem.Description ?? "",
                        Location = eventItem.Location ?? "",
                        Start = eventItem.Start?.DateTimeDateTimeOffset?.DateTime ??
                                (DateTime.TryParse(eventItem.Start?.Date, out var startDate)
                                    ? startDate
                                    : (DateTime?)null),
                        End = eventItem.End?.DateTimeDateTimeOffset?.DateTime ??
                              (DateTime.TryParse(eventItem.End?.Date, out var endDate) ? endDate : (DateTime?)null),
                        Attendees = eventItem.Attendees?.Select(a => a.Email ?? "").ToList() ?? new List<string>()
                    };

                    calendarEvents.Add(calendarEvent);
                }
            }

            return calendarEvents;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving calendar events for user {UserId}", userId);
            return [];
        }
    }

    private async Task<UserCredential?> GetUserCredentialAsync(int userId)
    {
        var tokenInfo = await _userRepository.GetOAuthTokenAsync(userId, "Google");
        if (tokenInfo == null)
        {
            return null;
        }

        var token = new TokenResponse
        {
            AccessToken = tokenInfo.AccessToken,
            RefreshToken = tokenInfo.RefreshToken,
            ExpiresInSeconds = (long)(tokenInfo.ExpiresAt - DateTime.UtcNow).TotalSeconds
        };

        // Check if token needs refresh
        if (DateTime.UtcNow >= tokenInfo.ExpiresAt)
        {
            if (string.IsNullOrEmpty(tokenInfo.RefreshToken))
            {
                _logger.LogWarning("No refresh token available for user {UserId}, re-authorization required", userId);
                throw new OAuthTokenExpiredException("Google", "Access token expired and no refresh token available. Please re-authorize.");
            }
            
            try
            {
                var refreshedToken =
                    await _authFlow.RefreshTokenAsync(userId.ToString(), tokenInfo.RefreshToken, CancellationToken.None);

                tokenInfo.AccessToken = refreshedToken.AccessToken;
                if (!string.IsNullOrEmpty(refreshedToken.RefreshToken))
                    tokenInfo.RefreshToken = refreshedToken.RefreshToken;
                tokenInfo.ExpiresAt = DateTime.UtcNow.AddSeconds(refreshedToken.ExpiresInSeconds ?? 3600);
                tokenInfo.UpdatedAt = DateTime.UtcNow;

                await _userRepository.UpdateOAuthTokenAsync(tokenInfo);

                token = refreshedToken;
            }
            catch (TokenResponseException ex)
            {
                _logger.LogError(ex, "Failed to refresh token for user {UserId} - token may be revoked or expired", userId);
                // Delete the invalid token so user can re-authorize
                await _userRepository.DeleteOAuthTokenAsync(userId, "Google");
                throw new OAuthTokenExpiredException("Google", "Refresh token is invalid or expired. Please re-authorize your Google account.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh token for user {UserId}", userId);
                throw new OAuthTokenExpiredException("Google", "Failed to refresh access token. Please re-authorize your Google account.", ex);
            }
        }

        var userCredential = new UserCredential(_authFlow, userId.ToString(), token);
        return userCredential;
    }

    private string ExtractEmailBody(Google.Apis.Gmail.v1.Data.MessagePart payload)
    {
        if (payload?.Body?.Data != null)
        {
            var decodedBytes = Convert.FromBase64String(payload.Body.Data.Replace('-', '+').Replace('_', '/'));
            return Encoding.UTF8.GetString(decodedBytes);
        }

        if (payload?.Parts != null)
        {
            foreach (var part in payload.Parts)
            {
                if (part.MimeType == "text/plain" || part.MimeType == "text/html")
                {
                    var body = ExtractEmailBody(part);
                    if (!string.IsNullOrEmpty(body))
                        return body;
                }
            }
        }

        return "";
    }

    public async Task<bool> HasValidTokenAsync(int userId)
    {
        var tokenInfo = await _userRepository.GetOAuthTokenAsync(userId, "Google");
        return tokenInfo != null && !string.IsNullOrEmpty(tokenInfo.AccessToken) &&
               tokenInfo.ExpiresAt > DateTime.UtcNow;
    }

    public async Task<bool> RevokeTokenAsync(int userId)
    {
        try
        {
            await _userRepository.DeleteOAuthTokenAsync(userId, "Google");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to revoke Google token for user {UserId}", userId);
            return false;
        }
    }

    public async Task<List<EmailDto>> GetEmailsAsync(int userId, int maxResults = 10, string? pageToken = null)
    {
        var googleEmails = await GetGoogleEmailsInternalAsync(userId, maxResults);
        return googleEmails.Select(MapToGenericEmailDto).ToList();
    }

    public Task<EmailDto?> GetEmailByIdAsync(int userId, string emailId)
    {
        throw new NotImplementedException("GetEmailByIdAsync is not yet implemented for Google provider");
    }

    public Task<bool> MarkEmailAsReadAsync(int userId, string emailId, bool isRead = true)
    {
        throw new NotImplementedException("MarkEmailAsReadAsync is not yet implemented for Google provider");
    }

    public async Task<List<CalendarEventDto>> GetCalendarEventsAsync(int userId, DateTime? timeMin = null, DateTime? timeMax = null, int maxResults = 50)
    {
        var googleEvents = await GetGoogleCalendarEventsInternalAsync(userId, timeMin, timeMax);
        return googleEvents.Select(MapToGenericCalendarEventDto).ToList();
    }

    public Task<CalendarEventDto?> GetEventByIdAsync(int userId, string eventId)
    {
        throw new NotImplementedException("GetEventByIdAsync is not yet implemented for Google provider");
    }

    public async Task<CalendarEventDto> CreateEventAsync(int userId, CalendarEventDto eventDto)
    {
        // Implementation would require Google API call to create event
        // For now, throw not implemented
        throw new NotImplementedException();
    }

    public async Task<CalendarEventDto> UpdateEventAsync(int userId, string eventId, CalendarEventDto eventDto)
    {
        // Implementation would require Google API call to update event
        // For now, throw not implemented
        throw new NotImplementedException();
    }

    public Task<bool> DeleteEventAsync(int userId, string eventId)
    {
        throw new NotImplementedException("DeleteEventAsync is not yet implemented for Google provider");
    }

    // Mapping methods
    private EmailDto MapToGenericEmailDto(GoogleEmailDto googleEmail)
    {
        return new EmailDto
        {
            Id = googleEmail.Id,
            ThreadId = googleEmail.ThreadId,
            Subject = googleEmail.Subject,
            From = googleEmail.From,
            To = googleEmail.To,
            Body = googleEmail.Body,
            Date = googleEmail.Date,
            IsRead = googleEmail.IsRead,
            Provider = "Google"
        };
    }

    private CalendarEventDto MapToGenericCalendarEventDto(GoogleCalendarEventDto googleEvent)
    {
        return new CalendarEventDto
        {
            Id = googleEvent.Id,
            Title = googleEvent.Summary,
            Description = googleEvent.Description,
            Start = googleEvent.Start,
            End = googleEvent.End,
            Location = googleEvent.Location,
            Attendees = googleEvent.Attendees,
            Provider = "Google"
        };
    }
}