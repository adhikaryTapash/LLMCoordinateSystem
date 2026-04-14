using OpenAI.Chat;

namespace LLMCoordinateSystem;

public class AgentMemory
{
	private readonly List<ChatMessage> _messages = new();

	public void Add(ChatMessage message)
	{
		_messages.Add(message);
	}

	public IReadOnlyList<ChatMessage> GetAll()
	{
		return _messages;
	}

	public void Clear()
	{
		_messages.Clear();
	}
}
