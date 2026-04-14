using System.Text;

namespace LLMCoordinateSystem;

public interface IConsoleMirrorLog
{
	void StartConversationLog(string userInput);

	void WriteLine(string? value = null);

	void EndConversationLog();
}

/// <summary>
/// Writes the same lines to the console and to a file under <c>C:\logs</c> for each user turn.
/// </summary>
public sealed class ConsoleMirrorLog : IConsoleMirrorLog, IDisposable
{
	private const string LogsDirectory = @"C:\logs";

	private readonly object _gate = new();
	private StreamWriter? _writer;

	public void StartConversationLog(string userInput)
	{
		lock (_gate)
		{
			_writer?.Dispose();
			_writer = null;

			Directory.CreateDirectory(LogsDirectory);

			var fileName = $"conversation_{DateTime.Now:yyyy-MM-dd_HHmmss_fff}.log";
			var path = Path.Combine(LogsDirectory, fileName);
			_writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
			{
				AutoFlush = true
			};

			_writer.WriteLine($"==== Conversation start {DateTimeOffset.Now:O} ====");
			_writer.WriteLine("User input:");
			_writer.WriteLine(userInput);
			_writer.WriteLine();

			var fullPath = Path.GetFullPath(path);
			var pathLine = $"📝 Log file: {fullPath}";
			_writer.WriteLine(pathLine);
			Console.WriteLine(pathLine);
			_writer.WriteLine();
		}
	}

	public void WriteLine(string? value = null)
	{
		var line = value ?? string.Empty;
		Console.WriteLine(line);
		lock (_gate)
		{
			_writer?.WriteLine(line);
		}
	}

	public void EndConversationLog()
	{
		lock (_gate)
		{
			if (_writer is null)
				return;

			_writer.WriteLine();
			_writer.WriteLine($"==== Conversation end {DateTimeOffset.Now:O} ====");
			_writer.Dispose();
			_writer = null;
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_writer?.Dispose();
			_writer = null;
		}
	}
}
