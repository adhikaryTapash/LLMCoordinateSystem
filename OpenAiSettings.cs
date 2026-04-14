namespace LLMCoordinateSystem;

public sealed class OpenAiSettings
{
    public const string SectionName = "OpenAI";

    /// <summary>
    /// From configuration (user secrets, <c>OpenAI__ApiKey</c>, or appsettings). Leave empty in repo.
    /// When empty here, the process <c>OPENAI_API_KEY</c> environment variable is used at runtime.
    /// </summary>
    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "gpt-4o-mini";
}
