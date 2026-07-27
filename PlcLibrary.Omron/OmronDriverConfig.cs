using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.Omron
{
    public sealed record OmronDriverConfig
    {
        public string Host { get; init; } = "127.0.0.1";
        public int Port { get; init; } = 9600;
        public int Timeout { get; init; } = 3000;
        public byte LocalNode { get; init; } = 1;
        public byte DestinyNode { get; init; } = 2;
        public bool IsUdp { get; init; } = false;

        public static OmronDriverConfig Parse(string connectionString)
            => ConnectionStringBinder.Bind<OmronDriverConfig>(connectionString);
    }
}
