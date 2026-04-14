using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace LLMCoordinateSystem;

internal static class OpenAiChatClientFactory
{
    private const string MissingKeyMessage =
        "OpenAI API key is missing. Set OpenAI:ApiKey (appsettings, user secrets, or OpenAI__ApiKey), " +
        "or set the OPENAI_API_KEY environment variable. See env.example in the project folder.";

    public static ChatClient Create(IOptions<OpenAiSettings> options) => Create(options.Value);

    public static ChatClient Create(OpenAiSettings settings)
    {
        var apiKey = string.IsNullOrWhiteSpace(settings.ApiKey)
            ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            : settings.ApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(MissingKeyMessage);

        var model = string.IsNullOrWhiteSpace(settings.Model) ? "gpt-4o-mini" : settings.Model;
        return new ChatClient(model, apiKey);
    }
}
