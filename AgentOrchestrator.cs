using System.Text.Json;

namespace LLMCoordinateSystem;

public class AgentOrchestrator
{
	private const int MaxRetries = 2;
	private const int MinAcceptableScore = 80;

	private readonly IPlannerAgent _planner;
	private readonly IExecutionAgent _executor;
	private readonly IEvaluatorAgent _evaluator;
	private readonly IConsoleMirrorLog _log;

	public AgentOrchestrator(
		IPlannerAgent planner,
		IExecutionAgent executor,
		IEvaluatorAgent evaluator,
		IConsoleMirrorLog log)
	{
		_planner = planner;
		_executor = executor;
		_evaluator = evaluator;
		_log = log;
	}

	public async Task<string> RunAsync(string userInput, string skill)
	{
		_planner.ResetMemory();
		_executor.ResetMemory();
		_evaluator.ResetMemory();

		var stepStart = DateTimeOffset.Now;
		LogStepStart("Planning (routing)", stepStart);
		var routePlan = await _planner.CreateRoutingPlanAsync(skill, userInput);
		LogStepEnd("Planning (routing)", stepStart);

		_log.WriteLine("\n🔀 Planner routing:");
		_log.WriteLine(
			$"  executionMode={routePlan.Routing.ExecutionMode} | " +
			$"allowSectionRefinementOnRetry={routePlan.Routing.AllowSectionRefinementOnRetry}");
		_log.WriteLine($"  reason: {routePlan.Routing.Reason}");
		_log.WriteLine("\n🧠 Display steps (visibility / trace):");
		foreach (var t in routePlan.DisplaySteps)
			_log.WriteLine($"  • [{t.Id}] {t.Description} (agent: {t.Agent})");
		_log.WriteLine();

		var originalUserInput = userInput;
		var workingInput = userInput;
		string response = "";
		EvaluationRecord? lastEval = null;

		var useSectionRefineNext = false;
		IReadOnlyList<string>? refineSections = null;

		for (var attempt = 0; attempt <= MaxRetries; attempt++)
		{
			_log.WriteLine($"\n🔁 Attempt {attempt + 1}");

			if (attempt > 0)
			{
				_executor.ResetMemory();
				_evaluator.ResetMemory();
			}

			var isEmr = string.Equals(skill, "emr.dictation.voice", StringComparison.OrdinalIgnoreCase);
			var allowSection = routePlan.Routing.AllowSectionRefinementOnRetry;

			if (useSectionRefineNext &&
			    refineSections is { Count: > 0 } &&
			    isEmr &&
			    allowSection)
			{
				_log.WriteLine($"➡️ Section-level refinement: {string.Join(", ", refineSections)}");

				stepStart = DateTimeOffset.Now;
				LogStepStart("Execution (section refine)", stepStart);
				response = await _executor.RefineEmrSectionsAsync(
					skill,
					response,
					originalUserInput,
					refineSections,
					lastEval?.Feedback ?? "");
				LogStepEnd("Execution (section refine)", stepStart);
			}
			else
			{
				_log.WriteLine("➡️ Full execution (single pass with current input)");

				stepStart = DateTimeOffset.Now;
				LogStepStart("Execution", stepStart);
				response = await _executor.ExecuteAsync(skill, workingInput);
				LogStepEnd("Execution", stepStart);
			}

			useSectionRefineNext = false;
			refineSections = null;

			_log.WriteLine($"\n🤖 Response:\n{response}\n");

			stepStart = DateTimeOffset.Now;
			LogStepStart("Evaluation", stepStart);
			lastEval = await _evaluator.EvaluateAsync(originalUserInput, response);
			LogStepEnd("Evaluation", stepStart);

			var evalJson = JsonSerializer.Serialize(lastEval, EvaluationRecord.JsonOptions);
			_log.WriteLine($"\n🔍 Evaluation (JSON):\n{evalJson}\n");
			_log.WriteLine($"📊 {lastEval.ToLogSummary()}");
			if (!string.IsNullOrWhiteSpace(lastEval.Feedback))
				_log.WriteLine($"📝 Feedback: {lastEval.Feedback}\n");

			if (lastEval.Acceptable || lastEval.Score >= MinAcceptableScore)
			{
				_log.WriteLine("✅ Output accepted (acceptable flag or score threshold)");
				break;
			}

			if (attempt == MaxRetries)
			{
				_log.WriteLine("⚠️ Max retries reached.");
				break;
			}

			var weak = EmrSectionHelper.MatchCanonicalSections(lastEval.WeakSections);
			if (isEmr && allowSection && weak.Count > 0)
			{
				useSectionRefineNext = true;
				refineSections = weak;
			}
			else
			{
				var returnLine = isEmr
					? "Return improved FULL EMR note."
					: "Return improved full response.";

				workingInput = $"""
					Original Input:
					{originalUserInput}

					Previous Output:
					{response}

					Fix the issues based on:
					{lastEval.Feedback}

					{returnLine}
					""";
			}
		}

		return response;
	}

	private void LogStepStart(string stepName, DateTimeOffset start) =>
		_log.WriteLine($"[{stepName}] Start: {start:yyyy-MM-dd HH:mm:ss.fff}");

	private void LogStepEnd(string stepName, DateTimeOffset start)
	{
		var end = DateTimeOffset.Now;
		var elapsedMs = (end - start).TotalMilliseconds;
		_log.WriteLine($"[{stepName}] End:   {end:yyyy-MM-dd HH:mm:ss.fff}  (elapsed {elapsedMs:F1} ms)");
	}
}
