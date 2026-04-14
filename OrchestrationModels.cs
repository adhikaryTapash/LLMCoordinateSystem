using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMCoordinateSystem;

public static class JsonContentHelper
{
	public static string StripMarkdownCodeFence(string text)
	{
		text = text.Trim();
		if (!text.StartsWith("```", StringComparison.Ordinal))
			return text;

		var firstNl = text.IndexOf('\n');
		if (firstNl >= 0)
			text = text[(firstNl + 1)..];

		var end = text.LastIndexOf("```", StringComparison.Ordinal);
		if (end >= 0)
			text = text[..end];

		return text.Trim();
	}
}

public class PlannerRoutingDecision
{
	[JsonPropertyName("executionMode")]
	public string ExecutionMode { get; set; } = "full_generation";

	/// <summary>When true, retries may rewrite only evaluator-listed EMR sections.</summary>
	[JsonPropertyName("allowSectionRefinementOnRetry")]
	public bool AllowSectionRefinementOnRetry { get; set; } = true;

	[JsonPropertyName("reason")]
	public string Reason { get; set; } = "";
}

public class PlannerRoutePlan
{
	[JsonPropertyName("routing")]
	public PlannerRoutingDecision Routing { get; set; } = new();

	[JsonPropertyName("displaySteps")]
	public List<TaskNode> DisplaySteps { get; set; } = new();

	public static PlannerRoutePlan Fallback(string input) => new()
	{
		Routing = new PlannerRoutingDecision
		{
			ExecutionMode = "full_generation",
			AllowSectionRefinementOnRetry = true,
			Reason = "Fallback: single full pass"
		},
		DisplaySteps =
		[
			new TaskNode { Id = "1", Description = input, Agent = "executor" }
		]
	};
}

public class EvaluationRecord
{
	public static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};

	[JsonPropertyName("score")]
	public int Score { get; set; }

	/// <summary>Optional explicit pass flag; orchestrator also uses <see cref="Score"/> threshold.</summary>
	[JsonPropertyName("acceptable")]
	public bool Acceptable { get; set; }

	[JsonPropertyName("weakSections")]
	public List<string> WeakSections { get; set; } = new();

	[JsonPropertyName("feedback")]
	public string Feedback { get; set; } = "";

	public static EvaluationRecord ParseOrFallback(string raw)
	{
		raw = JsonContentHelper.StripMarkdownCodeFence(raw ?? "");
		try
		{
			var r = JsonSerializer.Deserialize<EvaluationRecord>(raw, JsonOptions);
			if (r != null)
			{
				r.Score = Math.Clamp(r.Score, 0, 100);
				r.WeakSections ??= new List<string>();
				return r;
			}
		}
		catch (JsonException)
		{
			// ignored
		}

		return new EvaluationRecord
		{
			Score = 0,
			Acceptable = false,
			WeakSections = new List<string>(),
			Feedback = string.IsNullOrWhiteSpace(raw) ? "No evaluation" : raw
		};
	}

	public string ToLogSummary() =>
		$"Score: {Score}/100 | Acceptable: {Acceptable} | Weak sections: {(WeakSections.Count == 0 ? "(none)" : string.Join(", ", WeakSections))}";
}

public static class EmrSectionHelper
{
	public static readonly string[] CanonicalSections =
	[
		"Chief Complaint",
		"History of Present Illness",
		"Assessment",
		"Plan"
	];

	public static IReadOnlyList<string> MatchCanonicalSections(IEnumerable<string> weak)
	{
		var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var w in weak)
		{
			var t = w.Trim();
			if (t.Length == 0)
				continue;

			foreach (var c in CanonicalSections)
			{
				if (c.Equals(t, StringComparison.OrdinalIgnoreCase) ||
				    c.Contains(t, StringComparison.OrdinalIgnoreCase) ||
				    t.Contains(c, StringComparison.OrdinalIgnoreCase))
					matched.Add(c);
			}
		}

		return matched.ToList();
	}
}
