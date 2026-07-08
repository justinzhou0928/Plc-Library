using System;
using System.Collections.Generic;
using System.Text;

namespace PlcLibrary.DriverDomain.Enums
{
    public enum QualityCode : byte
    {
        Good = 0x00,
        Uncertain = 0x40,
        BadTimeout = 0x80,
        BadCommFailure = 0x81,
        BadConfigError = 0x82,
        BadDeviceFault = 0x83,
        BadOutOfService = 0x84,
        Offline = 0xC0,
        Initializing = 0xC1,
    }
}
