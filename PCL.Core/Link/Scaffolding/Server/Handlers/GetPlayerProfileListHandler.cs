using PCL.Core.Link.Scaffolding.Server.Abstractions;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Utils;

namespace PCL.Core.Link.Scaffolding.Server.Handlers;

public class GetPlayerProfileListHandler : IRequestHandler
{
    /// <inheritdoc />
    public string RequestType { get; } = "c:player_profiles_list";

    private static readonly JsonSerializerOptions _JsonOptions = new(JsonCompat.SerializerOptions)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <inheritdoc />
    public Task<(byte Status, ReadOnlyMemory<byte> Body)> HandleAsync(ReadOnlyMemory<byte> requestBody,
        IServerContext context, string sessionId, CancellationToken ct)
    {
        var allProfiles = context.PlayerProfiles;

        var responseBody = JsonSerializer.SerializeToUtf8Bytes(allProfiles, _JsonOptions);

        return Task.FromResult(((byte)0, new ReadOnlyMemory<byte>(responseBody)));
    }
}
