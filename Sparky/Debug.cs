namespace Sparky;

public static class Debug
{
    private static List<ILogger> loggers = new List<ILogger>();

    private static void All(Action<ILogger> action)
    {
        foreach (ILogger logger in loggers)
            action(logger);
    }

    public static void Log(string message)
        => All(x => x.Log(message));
    
    public static void LogWarning(string message)
        => All(x => x.LogWarning(message));
    
    public static void LogError(string message)
        => All(x => x.LogError(message));

    public static void LogException(Exception ex)
    {
        string[] messages = ex.ToString().Split(Environment.NewLine);
        foreach (string message in messages)
            All(x => x.LogError(message));
    }

    public static void Init()
    {
        loggers.Clear();
        
        loggers.Add(new StandardOutputLogger());

        Log("logger initialized");
    }
}