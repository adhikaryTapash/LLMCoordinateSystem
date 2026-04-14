using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace LLMCoordinateSystem;

internal class Program
{
	private static async Task Main(string[] args)
	{
		var host = CreateHostBuilder(args).Build();

		var app = host.Services.GetRequiredService<App>();

		await app.RunAsync();
	}

	private static IHostBuilder CreateHostBuilder(string[] args) =>
		Host.CreateDefaultBuilder(args)
			.ConfigureServices((context, services) =>
			{
				services.Configure<OpenAiSettings>(
					context.Configuration.GetSection(OpenAiSettings.SectionName));

				services.AddSingleton<App>();
				services.AddSingleton<IIntentAgent, IntentAgent>();
				services.AddSingleton<ISkillRouter, SkillRouter>();
				services.AddSingleton<IExecutionAgent, ExecutionAgent>();
				services.AddSingleton<IPlannerAgent, PlannerAgent>();
				services.AddSingleton<IEvaluatorAgent, EvaluatorAgent>();
				services.AddSingleton<IConsoleMirrorLog, ConsoleMirrorLog>();
				services.AddSingleton<AgentOrchestrator>();
			});
}

// =========================
// APP ENTRY
// =========================
public class App
{
	private readonly IIntentAgent _intentAgent;
	private readonly ISkillRouter _router;
	private readonly AgentOrchestrator _orchestrator;
	private readonly IConsoleMirrorLog _log;

	public App(
		IIntentAgent intentAgent,
		ISkillRouter router,
		AgentOrchestrator orchestrator,
		IConsoleMirrorLog log)
	{
		_intentAgent = intentAgent;
		_router = router;
		_orchestrator = orchestrator;
		_log = log;
	}

	public async Task RunAsync()
	{
		Console.WriteLine("🏥 Healthcare AI Agent Started");
		Console.WriteLine("Type your query:");

		while (true)
		{
			var input = Console.ReadLine();

			if (string.IsNullOrWhiteSpace(input))
				continue;

			_log.StartConversationLog(input);

			try
			{
				DateTimeOffset stepStart;

				// 1. Intent Detection
				stepStart = DateTimeOffset.Now;
				LogStepStart("Intent detection", stepStart);
				var intent = await _intentAgent.DetectIntentAsync(input);
				LogStepEnd("Intent detection", stepStart);

				// 2. Skill Routing
				stepStart = DateTimeOffset.Now;
				LogStepStart("Skill routing", stepStart);
				var skill = _router.Route(intent);
				LogStepEnd("Skill routing", stepStart);

				var response = await _orchestrator.RunAsync(input, skill);

				_log.WriteLine($"\n🤖 Response: {response}\n");
			}
			finally
			{
				_log.EndConversationLog();
			}
		}
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
// =========================
// Evaluator Agent
// =========================
public class EvaluatorAgent : IEvaluatorAgent, IAgent
{
	private readonly ChatClient _chatClient;
	private readonly AgentMemory _memory = new();

	public EvaluatorAgent(IOptions<OpenAiSettings> options)
	{
		_chatClient = OpenAiChatClientFactory.Create(options);
	}

	public void ResetMemory() => _memory.Clear();

	public async Task<string> RunAsync(string outputToEvaluate)
	{
		var r = await EvaluateAsync("Evaluate the following assistant response.", outputToEvaluate);
		return JsonSerializer.Serialize(r);
	}

	public async Task<EvaluationRecord> EvaluateAsync(string input, string output)
	{
		if (!_memory.GetAll().Any())
		{
			_memory.Add(new SystemChatMessage(
				"You are a strict medical document evaluator.\n" +
				"Return ONLY a single JSON object, no markdown and no extra text.\n" +
				"Schema:\n" +
				"{ \"score\": <integer 0-100>, \"acceptable\": <true|false>, \"weakSections\": [\"Plan\", ...], \"feedback\": \"<short rationale>\" }\n" +
				"Rules:\n" +
				"- score reflects overall quality (completeness, clinical clarity, format).\n" +
				"- acceptable true only if the output is clearly fit for use as-is.\n" +
				"- weakSections: EMR section names that need improvement (e.g. Chief Complaint, History of Present Illness, Assessment, Plan). Use [] if none.\n" +
				"- feedback: concrete issues and what to fix."));
		}

		_memory.Add(new UserChatMessage(
			$"Input:\n{input}\n\nOutput:\n{output}\n\nEvaluate and return JSON only."));

		var response = await _chatClient.CompleteChatAsync(_memory.GetAll());

		var text = response.Value.Content.FirstOrDefault()?.Text ?? "{}";
		_memory.Add(new AssistantChatMessage(text));
		return EvaluationRecord.ParseOrFallback(text);
	}
}

// =========================
// PLANNER AGENT
// =========================
public class PlannerAgent : IPlannerAgent, IAgent
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	private readonly ChatClient _chatClient;
	private readonly AgentMemory _memory = new();

	public PlannerAgent(IOptions<OpenAiSettings> options)
	{
		_chatClient = OpenAiChatClientFactory.Create(options);
	}

	public void ResetMemory() => _memory.Clear();

	public async Task<string> RunAsync(string input) =>
		JsonSerializer.Serialize(await CreateRoutingPlanAsync("general.llm", input));

	public async Task<PlannerRoutePlan> CreateRoutingPlanAsync(string skill, string input)
	{
		if (!_memory.GetAll().Any())
		{
			_memory.Add(new SystemChatMessage(
				"You are a hybrid planner: output ROUTING decisions for the runtime, plus human-readable steps for logs.\n" +
				"The executor always performs full generation from user text; your job is NOT to micro-step execution.\n" +
				"Return ONLY one JSON object, no markdown and no extra text.\n" +
				"Schema:\n" +
				"{\n" +
				"  \"routing\": {\n" +
				"    \"executionMode\": \"full_generation\",\n" +
				"    \"allowSectionRefinementOnRetry\": true,\n" +
				"    \"reason\": \"<why this routing fits the skill and task>\"\n" +
				"  },\n" +
				"  \"displaySteps\": [\n" +
				"    { \"id\": \"1\", \"description\": \"<high-level step label>\", \"agent\": \"executor\" }\n" +
				"  ]\n" +
				"}\n" +
				"executionMode is always \"full_generation\" for now (reserved for future hybrid modes).\n" +
				"allowSectionRefinementOnRetry: true for EMR/dictation-style tasks where section-level fixes help; false if a single monolithic answer is required."));
		}

		_memory.Add(new UserChatMessage($"Skill: {skill}\nTask: {input}"));

		var response = await _chatClient.CompleteChatAsync(_memory.GetAll());
		var json = response.Value.Content.FirstOrDefault()?.Text ?? "{}";
		_memory.Add(new AssistantChatMessage(json));

		json = JsonContentHelper.StripMarkdownCodeFence(json);

		try
		{
			var plan = JsonSerializer.Deserialize<PlannerRoutePlan>(json, JsonOptions);
			if (plan == null)
				return PlannerRoutePlan.Fallback(input);

			plan.Routing ??= new PlannerRoutingDecision();
			plan.DisplaySteps ??= new List<TaskNode>();
			if (plan.DisplaySteps.Count == 0)
			{
				plan.DisplaySteps =
				[
					new TaskNode { Id = "1", Description = input, Agent = "executor" }
				];
			}

			return plan;
		}
		catch (JsonException)
		{
			return PlannerRoutePlan.Fallback(input);
		}
	}
}

// =========================
// INTENT AGENT
// =========================
public interface IIntentAgent
{
	Task<string> DetectIntentAsync(string input);
}
public interface IPlannerAgent
{
	void ResetMemory();
	Task<PlannerRoutePlan> CreateRoutingPlanAsync(string skill, string input);
}

public interface IEvaluatorAgent
{
	void ResetMemory();
	Task<EvaluationRecord> EvaluateAsync(string input, string output);
}
public class IntentAgent : IIntentAgent
{
	public Task<string> DetectIntentAsync(string input)
	{
		if (input.Contains("dictate", StringComparison.OrdinalIgnoreCase))
			return Task.FromResult("voice_dictation");

		if (input.Contains("diagnosis", StringComparison.OrdinalIgnoreCase))
			return Task.FromResult("diagnosis");

		if (input.Contains("lab", StringComparison.OrdinalIgnoreCase))
			return Task.FromResult("lab_analysis");

		return Task.FromResult("general");
	}
}

// =========================
// SKILL ROUTER
// =========================
public interface ISkillRouter
{
	string Route(string intent);
}

public class SkillRouter : ISkillRouter
{
	public string Route(string intent)
	{
		return intent switch
		{
			"voice_dictation" => "emr.dictation.voice",
			"diagnosis" => "emr.diagnosis.vertical",
			"lab_analysis" => "emr.imaging.lab.analysis",
			_ => "general.llm"
		};
	}
}

// =========================
// EXECUTION AGENT
// =========================
public interface IExecutionAgent
{
	void ResetMemory();
	Task<string> ExecuteAsync(string skill, string input);

	/// <summary>Rewrite only the listed EMR sections; keep the rest aligned with <paramref name="currentNote"/>.</summary>
	Task<string> RefineEmrSectionsAsync(
		string skill,
		string currentNote,
		string originalUserInput,
		IReadOnlyList<string> canonicalSectionHeadings,
		string evaluatorFeedback);
}

public class ExecutionAgent : IExecutionAgent, IAgent
{
	private readonly ChatClient _chatClient;
	private readonly AgentMemory _memory = new();

	public ExecutionAgent(IOptions<OpenAiSettings> options)
	{
		_chatClient = OpenAiChatClientFactory.Create(options);
	}

	public void ResetMemory() => _memory.Clear();

	public Task<string> RunAsync(string input) => ExecuteAsync("general.llm", input);

	public async Task<string> ExecuteAsync(string skill, string input)
	{
		string systemPrompt = skill switch
		{
			"emr.dictation.voice" =>
				"Convert input into a COMPLETE EMR note.\n" +
				"You MUST fill ALL sections with realistic clinical details.\n\n" +
				"Format:\n" +
				"Chief Complaint:\n" +
				"History of Present Illness:\n" +
				"Assessment:\n" +
				"Plan:\n" +
				"\nDo not leave any section empty.",

			_ => "You are a helpful assistant."
		};

		if (!_memory.GetAll().Any())
		{
			_memory.Add(new SystemChatMessage(systemPrompt));
		}

		_memory.Add(new UserChatMessage(input));

		var response = await _chatClient.CompleteChatAsync(_memory.GetAll());

		var text = response.Value.Content.FirstOrDefault()?.Text ?? "No response";

		_memory.Add(new AssistantChatMessage(text));

		return text;
	}

	public async Task<string> RefineEmrSectionsAsync(
		string skill,
		string currentNote,
		string originalUserInput,
		IReadOnlyList<string> canonicalSectionHeadings,
		string evaluatorFeedback)
	{
		if (!string.Equals(skill, "emr.dictation.voice", StringComparison.OrdinalIgnoreCase))
		{
			var fallbackUser =
				$"""
				Original Input:
				{originalUserInput}

				Previous Output:
				{currentNote}

				Fix the issues based on:
				{evaluatorFeedback}

				Return improved full response.
				""";
			return await ExecuteAsync(skill, fallbackUser);
		}

		var systemPrompt =
			"You revise an EXISTING EMR note.\n" +
			"Rewrite ONLY the sections the user lists. Keep all other sections substantively the same (same facts and structure).\n" +
			"Output a COMPLETE note with all four headings:\n" +
			"Chief Complaint:\n" +
			"History of Present Illness:\n" +
			"Assessment:\n" +
			"Plan:\n" +
			"Do not leave any section empty.";

		if (!_memory.GetAll().Any())
			_memory.Add(new SystemChatMessage(systemPrompt));

		var sectionList = string.Join(", ", canonicalSectionHeadings);
		var userPrompt =
			$"Sections to rewrite (only these): {sectionList}\n\n" +
			$"Original user / dictation context:\n{originalUserInput}\n\n" +
			$"Evaluator feedback:\n{evaluatorFeedback}\n\n" +
			$"Current EMR:\n{currentNote}\n\n" +
			"Return the full EMR with all four sections.";

		_memory.Add(new UserChatMessage(userPrompt));

		var response = await _chatClient.CompleteChatAsync(_memory.GetAll());
		var text = response.Value.Content.FirstOrDefault()?.Text ?? "No response";
		_memory.Add(new AssistantChatMessage(text));
		return text;
	}
}
