namespace PlcLibrary.DriverPool.Models
{
    internal static class ResiliencePipelineKeys
    {
        public static string Pool(string deviceId) => $"Pool:{deviceId}";
        public static string Push(string deviceId) => $"Push:{deviceId}";
    }
}
