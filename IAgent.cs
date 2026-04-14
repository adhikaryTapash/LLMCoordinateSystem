namespace LLMCoordinateSystem;

public interface IAgent
{
	Task<string> RunAsync(string input);
}
