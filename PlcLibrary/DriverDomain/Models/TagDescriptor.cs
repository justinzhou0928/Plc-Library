namespace PlcLibrary.DriverDomain.Models
{
    /// <summary>
    /// 协议浏览返回的点位/节点描述。`Address` 可直接用于点位配置的 `Address`，或继续浏览。
    /// </summary>
    public sealed record TagDescriptor
    {
        /// <summary>协议地址（如 OPC UA NodeId、BACnet 对象地址）。</summary>
        public required string Address { get; init; }

        /// <summary>展示名。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>协议数据类型名称（如 OPC UA 的 Boolean/DateTime/Double）；未知为 null。</summary>
        public string? DataType { get; init; }

        /// <summary>是否可写（协议能判断时）。</summary>
        public bool IsWritable { get; init; }

        /// <summary>是否可继续向下浏览（文件夹/对象节点）。</summary>
        public bool IsFolder { get; init; }

        /// <summary>协议描述；未知为 null。</summary>
        public string? Description { get; init; }
    }
}
