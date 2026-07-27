using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.Bacnet
{
    public sealed record BacnetDriverConfig
    {
        public string Host { get; init; } = "127.0.0.1";
        public int Port { get; init; } = 47808;
        public int Timeout { get; init; } = 5000;
        public uint DeviceInstance { get; init; } = 0;
        public string LocalEndpointIp { get; init; } = "";

        public static BacnetDriverConfig Parse(string connectionString)
            => ConnectionStringBinder.Bind<BacnetDriverConfig>(connectionString);
    }
}
