using SmartX.Core.Storage;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Mesh;

public sealed record MeshNodeDto(
    string Id,
    string Label,
    bool IsGateway,
    string? Mac,
    string? Subnet,
    SensorCategory? Category,
    bool IsDown,
    string? DownReason,
    int HopsToGateway,
    string? NextHop);

public sealed record MeshEdgeDto(string A, string B, int Cost, bool InMst);

public sealed record MeshTopologyResponse(
    IReadOnlyList<MeshNodeDto> Nodes,
    IReadOnlyList<MeshEdgeDto> Edges,
    int MstCost,
    int KruskalCost,
    int ConnectedCount,
    int DownCount,
    int StrandedCount,
    IReadOnlyList<string> BfsOrder);

public sealed record MeshRouteResponse(
    string Query,
    bool Found,
    string? DeviceId,
    string? Mac,
    string Message,
    IReadOnlyList<string> NormalPath,
    int NormalCost,
    IReadOnlyList<string> LivePath,
    int LiveCost,
    bool Rerouted,
    IReadOnlyList<string> AlternativeHops);

public sealed record MeshDetour(
    string DeviceId,
    IReadOnlyList<string> OldPath,
    IReadOnlyList<string> NewPath,
    int OldCost,
    int NewCost);

public sealed record MeshSubnetResponse(
    string Subnet,
    string Algorithm,
    IReadOnlyList<string> VisitOrder,
    IReadOnlyList<string> Connected,
    IReadOnlyList<string> Down,
    IReadOnlyList<string> Stranded,
    IReadOnlyList<MeshDetour> Detours);

public sealed record HistoryRangeResponse(
    bool Found,
    string Message,
    string? DeviceId,
    string? Mac,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<HistoryPoint> Points,
    int Matched,
    int NodesVisited,
    int TotalStored,
    int TreeHeight,
    double ElapsedMicroseconds,
    double? Min,
    double? Max,
    double? Average);
