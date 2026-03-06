namespace OpenMind.CRM.Application.Dtos;

public class AuthUrlResponse
{
    public string AuthorizationUrl { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
}

public class EmailDto
{
    public string Id { get; set; } = string.Empty;
    public string ThreadId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public bool IsRead { get; set; }
    public string Provider { get; set; } = string.Empty;
}

public class CalendarEventDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }
    public string Location { get; set; } = string.Empty;
    public List<string> Attendees { get; set; } = new();
    public string Provider { get; set; } = string.Empty;
}
