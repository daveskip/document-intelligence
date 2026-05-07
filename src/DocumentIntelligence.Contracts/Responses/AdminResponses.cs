namespace DocumentIntelligence.Contracts.Responses;

public record AdminUserDto(string Id, string Email, string DisplayName, DateTimeOffset CreatedAt, IReadOnlyList<string> Roles);
