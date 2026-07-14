using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.OpcUa
{
    public sealed record OpcUaDriverConfig
    {
        public string Endpoint { get; init; } = "opc.tcp://localhost:4840";
        public string? UserName { get; init; }
        public string? Password { get; init; }
        public string Security { get; init; } = "None";
        public int Timeout { get; init; } = 5000;
        public int PublishingInterval { get; init; } = 1000;
        public int SessionTimeout { get; init; } = 60000;
        public bool AutoAcceptCertificate { get; init; } = true;
        public string PkiOwnPath { get; init; } = "PlcLibrary.OpcUa/pki/own";
        public string PkiTrustedPath { get; init; } = "PlcLibrary.OpcUa/pki/trusted";
        public string PkiRejectedPath { get; init; } = "PlcLibrary.OpcUa/pki/rejected";

        public static OpcUaDriverConfig Parse(string connectionString)
            => ConnectionStringBinder.Bind<OpcUaDriverConfig>(connectionString);
    }
}
