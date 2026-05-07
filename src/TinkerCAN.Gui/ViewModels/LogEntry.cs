using Avalonia.Media;

namespace LINGui.ViewModels;

public enum LogLevel { TX, Info, Warn, Error }

public class LogEntry
{
    public string Timestamp { get; }
    public string Message { get; }
    public LogLevel Level { get; }
    public IBrush Brush { get; }

    public LogEntry(string message, LogLevel level)
    {
        Timestamp = System.DateTime.Now.ToString("HH:mm:ss.fff");
        Message = message;
        Level = level;
        Brush = level switch
        {
            LogLevel.TX    => Brushes.LimeGreen,
            LogLevel.Info  => Brushes.Cyan,
            LogLevel.Warn  => Brushes.Yellow,
            LogLevel.Error => Brushes.OrangeRed,
            _              => Brushes.White,
        };
    }
}
