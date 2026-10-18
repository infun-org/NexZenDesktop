using System;
using System.Text.Json.Serialization;

namespace NexZen.Core.NexShare.Models;

/// <summary>
/// The base envelope for all NexShare protocol messages over the wire.
/// Mirrors Android <c>WireEnvelope(type, version, messageId, seq, ackFor, senderId, timestamp, content)</c>.
/// </summary>
public class WireEnvelope
{
    /// <summary>Current protocol version (Android NexShareConstants.PROTOCOL_VERSION).</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// The type of the message (e.g. "hello", "text", "file_offer"). See <see cref="NexShareMessageTypes"/>.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Optional per-connection sequence number (Android: Long?, omitted when null).</summary>
    [JsonPropertyName("seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Seq { get; set; }

    [JsonPropertyName("senderId")]
    public string SenderId { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// The serialized JSON string of the actual message payload. Android expects this to be called "content".
    /// </summary>
    [JsonPropertyName("content")]
    public string Payload { get; set; } = string.Empty;

    [JsonPropertyName("ackFor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AckFor { get; set; }

    /// <summary>Shallow copy (used by <see cref="Crypto.EnvelopeCipher"/> so the input is never mutated).</summary>
    public WireEnvelope With(string payload) => new()
    {
        Type = Type, Version = Version, MessageId = MessageId, Seq = Seq,
        SenderId = SenderId, Timestamp = Timestamp, Payload = payload, AckFor = AckFor
    };
}
