using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.AllenBradley
{
    public sealed record AllenBradleyDriverConfig
    {
        public string Host { get; init; } = "127.0.0.1";
        public int Port { get; init; } = 44818;
        public int Timeout { get; init; } = 5000;
        public string Path { get; init; } = "";
        public bool UseConnected { get; init; } = false;

        public static AllenBradleyDriverConfig Parse(string connectionString)
            => ConnectionStringBinder.Bind<AllenBradleyDriverConfig>(connectionString);
    }
}
