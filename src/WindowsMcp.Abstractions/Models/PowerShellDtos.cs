namespace WindowsMcp.Abstractions.Models;

public record PSResult(
    bool Success,
    string Stdout,
    string Stderr,
    int ExitCode,
    string[] Errors);
