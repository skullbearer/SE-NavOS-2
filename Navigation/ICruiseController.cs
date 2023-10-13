using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IngameScript
{
    public enum CruiseTerminateReason
    {
        Completed = 1,
        Aborted = 2,
        Other = 3,
    }

    public delegate void CruiseTerminateEventDelegate(ICruiseController source, string reason);

    public interface ICruiseController
    {
        event CruiseTerminateEventDelegate CruiseTerminated;
        string Name { get; }
        void AppendStatus(StringBuilder strb);
        void Run();
        void Abort();
        void Terminate(string reason);
    }
}
