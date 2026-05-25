namespace WindowsMcp.Abstractions.Models;

public record HttpResponseDto(int Status, IDictionary<string, string> Headers, string Body);
