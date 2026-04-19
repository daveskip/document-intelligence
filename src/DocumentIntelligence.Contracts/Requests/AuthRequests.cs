namespace DocumentIntelligence.Contracts.Requests;

public record RegisterRequest(string Email, string Password, string DisplayName);

public record LoginRequest(string Email, string Password);

public record RefreshTokenRequest(string RefreshToken);
