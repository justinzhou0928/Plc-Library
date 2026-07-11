using System;

namespace PlcLibrary.DriverDomain.Attributes
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class ProtocolDriverNameAttribute(string name) : Attribute
    {
        public string Name { get; } = name;
    }
}
