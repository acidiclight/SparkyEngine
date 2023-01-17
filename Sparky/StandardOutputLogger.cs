using System.Text;

namespace Sparky;

public class StandardOutputLogger : ILogger
{
    private StringBuilder messageBuilder = new StringBuilder();
    private ConsoleColor previousColor;

    public StandardOutputLogger()
    {
        previousColor = Console.ForegroundColor;
    }
    
    private void SetConsoleColor(ConsoleColor color)
    {
        previousColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
    }

    private void ResetConsoleColor()
    {
        Console.ForegroundColor = previousColor;
    }

    private void AppendDate()
    {
        messageBuilder.Append('[');
        messageBuilder.Append(DateTime.Now.ToShortDateString());
        messageBuilder.Append(' ');
        messageBuilder.Append(DateTime.Now.ToShortTimeString());
        messageBuilder.Append(']');
    }
    
    public void Log(string message)
    {
        ResetConsoleColor();
        messageBuilder.Length = 0;

        AppendDate();

        messageBuilder.Append(" <info> ");
        messageBuilder.Append(message);
        
        Console.WriteLine(messageBuilder);
    }

    public void LogWarning(string message)
    {
        ResetConsoleColor();
        messageBuilder.Length = 0;

        AppendDate();

        messageBuilder.Append(" <warn> ");
        messageBuilder.Append(message);

        SetConsoleColor(ConsoleColor.Magenta);
        Console.WriteLine(messageBuilder);
    }

    public void LogError(string message)
    {
        ResetConsoleColor();
        messageBuilder.Length = 0;

        AppendDate();

        messageBuilder.Append(" <shit> ");
        messageBuilder.Append(message);

        SetConsoleColor(ConsoleColor.Red);
        Console.WriteLine(messageBuilder);
    }
}