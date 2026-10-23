using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moodle.Domain;

/// <summary>Evidence that an assignment submission was or was not delivered.</summary>
[JsonConverter(typeof(SubmissionDeliveryStateJsonConverter))]
public enum SubmissionDeliveryState
{
    Unknown = 0,
    NotSubmitted = 1,
    Submitted = 2
}

public sealed class SubmissionDeliveryStateJsonConverter : JsonConverter<SubmissionDeliveryState>
{
    public override SubmissionDeliveryState Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => reader.GetString() switch
    {
        "unknown" => SubmissionDeliveryState.Unknown,
        "not_submitted" => SubmissionDeliveryState.NotSubmitted,
        "submitted" => SubmissionDeliveryState.Submitted,
        var value => throw new JsonException($"Unknown submission delivery state '{value}'.")
    };

    public override void Write(Utf8JsonWriter writer, SubmissionDeliveryState value,
        JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        SubmissionDeliveryState.Unknown => "unknown",
        SubmissionDeliveryState.NotSubmitted => "not_submitted",
        SubmissionDeliveryState.Submitted => "submitted",
        _ => throw new JsonException($"Unsupported submission delivery state '{value}'.")
    });
}
