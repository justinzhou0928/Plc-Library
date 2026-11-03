using System;

namespace PlcLibrary.DriverDomain.Attributes
{
    /// <summary>声明驱动对应的协议名，必须与 <c>DeviceConfiguration.Protocol</c> 精确匹配</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class ProtocolDriverNameAttribute(string name) : Attribute
    {
        /// <summary>协议名</summary>
        public string Name { get; } = name;
    }
}
