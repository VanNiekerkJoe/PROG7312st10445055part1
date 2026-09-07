namespace SmartX.Client.Services;

public sealed record TreeNodeDto(
    string Id,
    string Name,
    string NodeType,
    string? MacAddress,
    string? Category,
    List<string> Attachments,
    List<TreeNodeDto> Children);

public sealed record ValidationResultDto(bool IsValid, List<string> Errors);
