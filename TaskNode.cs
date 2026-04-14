using System.Text.Json.Serialization;

namespace LLMCoordinateSystem;

public class TaskNode
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = "";

	[JsonPropertyName("description")]
	public string Description { get; set; } = "";

	[JsonPropertyName("agent")]
	public string Agent { get; set; } = "";
}
