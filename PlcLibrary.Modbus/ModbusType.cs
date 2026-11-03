namespace PlcLibrary.Modbus
{
    /// <summary>Modbus 寄存器/线圈类型</summary>
    public enum ModbusType
    {
        /// <summary>线圈（可读可写）</summary>
        Coil,

        /// <summary>离散输入（只读）</summary>
        DiscreteInput,

        /// <summary>输入寄存器（只读）</summary>
        InputRegister,

        /// <summary>保持寄存器（可读可写）</summary>
        HoldingRegister,
    }
}
