using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.Mitsubishi
{
    public sealed record MitsubishiDriverConfig
    {
        public string Host { get; init; } = "127.0.0.1";
        public int Port { get; init; } = 6000;
        public int Timeout { get; init; } = 3000;
        public string ProtocolType { get; init; } = "MC";

        public static MitsubishiDriverConfig Parse(string connectionString)
            => ConnectionStringBinder.Bind<MitsubishiDriverConfig>(connectionString);
    }
}
