using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IngameScript
{
    public interface ICruiseController
    {
        event Action CruiseCompleted;
        string Name { get; }
        void AppendStatus(StringBuilder strb);
        void Run();
        void Abort();
    }
}
