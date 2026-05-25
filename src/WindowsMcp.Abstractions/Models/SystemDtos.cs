namespace WindowsMcp.Abstractions.Models;

public record WmiResultDto(string ClassName, object[] Rows);
