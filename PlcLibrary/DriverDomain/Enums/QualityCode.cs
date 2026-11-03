namespace PlcLibrary.DriverDomain.Enums
{
    /// <summary>采集结果质量码，数值与 OPC UA StatusCode 的严重度分段对齐</summary>
    public enum QualityCode : byte
    {
        /// <summary>数据有效</summary>
        Good = 0x00,

        /// <summary>数据可用但不确定</summary>
        Uncertain = 0x40,

        /// <summary>读取超时</summary>
        BadTimeout = 0x80,

        /// <summary>通信失败</summary>
        BadCommFailure = 0x81,

        /// <summary>点位/连接配置错误</summary>
        BadConfigError = 0x82,

        /// <summary>设备故障</summary>
        BadDeviceFault = 0x83,

        /// <summary>设备停用/维护</summary>
        BadOutOfService = 0x84,

        /// <summary>驱动离线</summary>
        Offline = 0xC0,

        /// <summary>驱动初始化中</summary>
        Initializing = 0xC1,
    }
}
