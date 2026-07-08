using PlcLibrary.DriverDomain.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Pipeline.Interfaces
{
    public interface IDataHandler
    {
        ValueTask HandleAsync(DriverResult result, CancellationToken ct);
    }
}
