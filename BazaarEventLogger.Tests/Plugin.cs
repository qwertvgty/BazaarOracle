namespace BazaarEventLogger
{
    public static class Plugin
    {
        public static TestLogSink Log { get; } = new TestLogSink();
    }

    public sealed class TestLogSink
    {
        public void LogInfo(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message) { }
    }
}
