using System.Text.Json;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>
/// 历史桶内容的 wire 投影编码（SDK 代理与平台 adapter 同源共享）：单个回合（与参数包 inline
/// 桶的 <c>turns[]</c> 元素同形）与压缩视图投影条目（<c>kind</c> 判别的开放集合）的读写。
/// dataRequest/dataResponse 通道（按需反向拉取桶视图）与 inline 桶全量快照共用本编码，
/// 物理上保证两侧不会出现两份漂移的桶投影实现。
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public static class HistoryBucketWireProjection
{
    /// <summary>把一个回合写为 wire 投影（<c>messages[]</c> + 可选 <c>digest</c>/<c>turnOrdinal</c>/<c>metadata</c>）。</summary>
    /// <param name="writer">目标 writer（已位于回合对象值位置）。</param>
    /// <param name="turn">被投影的回合。</param>
    public static void WriteTurn(Utf8JsonWriter writer, HistoryTurn turn)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(turn);

        writer.WriteStartObject();
        writer.WriteStartArray("messages");
        foreach (var message in turn.Messages)
        {
            writer.WriteStartObject();
            writer.WriteString("role", SerializeRole(message.Role));
            writer.WriteString("content", message.Content);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (turn.Digest is not null)
            writer.WriteString("digest", turn.Digest);
        writer.WriteNumber("turnOrdinal", turn.TurnOrdinal);
        if (turn.Metadata is { Count: > 0 })
        {
            writer.WriteStartObject("metadata");
            foreach (var pair in turn.Metadata)
                writer.WriteString(pair.Key, pair.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    /// <summary>从 wire 投影还原一个回合。</summary>
    /// <param name="wireTurn">wire 回合元素。</param>
    public static HistoryTurn ReadTurn(JsonElement wireTurn)
    {
        var messages = wireTurn.GetProperty("messages").EnumerateArray()
            .Select(message => new ChatMessage(
                ParseRole(message.GetProperty("role").GetString()
                    ?? throw new ArgumentException("Wire history turn message requires a role.")),
                message.GetProperty("content").GetString()
                    ?? throw new ArgumentException("Wire history turn message requires a content.")))
            .ToArray();
        var digest = wireTurn.TryGetProperty("digest", out var digestElement) &&
                     digestElement.ValueKind == JsonValueKind.String
            ? digestElement.GetString()
            : null;
        var turnOrdinal = wireTurn.GetProperty("turnOrdinal").GetInt64();
        return new HistoryTurn(
            messages,
            digest,
            turnOrdinal,
            ReadMetadata(wireTurn));
    }

    /// <summary>
    /// 把一个压缩视图投影条目写为 wire 投影：<c>rawTurn</c> 内嵌回合、<c>digestSummary</c> 与
    /// <c>grandSummary</c> 为纯文本摘要——<c>kind</c> 判别集合对未来的投影策略保持开放。
    /// </summary>
    /// <param name="writer">目标 writer（已位于条目对象值位置）。</param>
    /// <param name="entry">被投影的条目。</param>
    public static void WriteProjectionEntry(Utf8JsonWriter writer, HistoryProjectionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(entry);

        writer.WriteStartObject();
        switch (entry)
        {
            case HistoryProjectionRawTurn rawTurn:
                writer.WriteString("kind", "rawTurn");
                writer.WritePropertyName("turn");
                WriteTurn(writer, rawTurn.Turn);
                break;
            case HistoryProjectionDigestSummary digestSummary:
                writer.WriteString("kind", "digestSummary");
                writer.WriteNumber("turnOrdinal", digestSummary.StartOrdinal);
                writer.WriteString("digest", digestSummary.Digest);
                break;
            case HistoryProjectionGrandSummary grandSummary:
                writer.WriteString("kind", "grandSummary");
                writer.WriteNumber("startOrdinal", grandSummary.StartOrdinal);
                writer.WriteNumber("endOrdinal", grandSummary.EndOrdinal);
                writer.WriteString("summary", grandSummary.Summary);
                break;
            default:
                throw new NotSupportedException(
                    $"History projection entry type '{entry.GetType().FullName}' has no wire projection.");
        }

        writer.WriteEndObject();
    }

    /// <summary>从 wire 投影还原一个压缩视图投影条目。</summary>
    /// <param name="wireEntry">wire 条目元素。</param>
    public static HistoryProjectionEntry ReadProjectionEntry(JsonElement wireEntry)
    {
        if (!wireEntry.TryGetProperty("kind", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Wire history projection entry requires a string 'kind' discriminator.");

        return kindElement.GetString() switch
        {
            "rawTurn" => new HistoryProjectionRawTurn(ReadTurn(wireEntry.GetProperty("turn"))),
            "digestSummary" => new HistoryProjectionDigestSummary(
                wireEntry.GetProperty("turnOrdinal").GetInt64(),
                wireEntry.GetProperty("digest").GetString()
                ?? throw new ArgumentException("Wire digest summary requires a digest.")),
            "grandSummary" => new HistoryProjectionGrandSummary(
                wireEntry.GetProperty("startOrdinal").GetInt64(),
                wireEntry.GetProperty("endOrdinal").GetInt64(),
                wireEntry.GetProperty("summary").GetString()
                ?? throw new ArgumentException("Wire grand summary requires a summary.")),
            _ => throw new NotSupportedException(
                $"Unknown history projection entry kind '{kindElement.GetString()}'."),
        };
    }

    private static Dictionary<string, string>? ReadMetadata(JsonElement wireTurn)
    {
        if (!wireTurn.TryGetProperty("metadata", out var metadataElement) ||
            metadataElement.ValueKind != JsonValueKind.Object)
            return null;

        return new Dictionary<string, string>(
            metadataElement.EnumerateObject()
                .Where(static property => property.Value.ValueKind == JsonValueKind.String)
                .Select(static property => new KeyValuePair<string, string>(
                    property.Name, property.Value.GetString()!)),
            StringComparer.Ordinal);
    }

    private static string SerializeRole(ChatMessageRole role) => role switch
    {
        ChatMessageRole.System => "system",
        ChatMessageRole.User => "user",
        ChatMessageRole.Assistant => "assistant",
        _ => throw new NotSupportedException($"Unknown chat message role '{role}'."),
    };

    private static ChatMessageRole ParseRole(string role) => role switch
    {
        "system" => ChatMessageRole.System,
        "user" => ChatMessageRole.User,
        "assistant" => ChatMessageRole.Assistant,
        _ => throw new NotSupportedException($"Unknown chat message role '{role}'."),
    };
}
