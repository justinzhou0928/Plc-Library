namespace PlcLibrary.DriverDomain.Enums
{
    /// <summary>驱动连接状态</summary>
    public enum DriverStatus
    {
        /// <summary>未连接</summary>
        Disconnected = 0,

        /// <summary>连接中</summary>
        Connecting = 1,

        /// <summary>已连接</summary>
        Connected = 2,

        /// <summary>故障，等待池/监督循环重建</summary>
        Faulted = 3,
    }
}
