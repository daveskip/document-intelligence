namespace DocumentIntelligence.Contracts.Responses;

public record AuthResponse(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiry, UserDto User);

public record UserDto(string Id, string Email, string DisplayName);
