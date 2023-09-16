using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript.Navigation
{
    public class Retrograde : OrientControllerBase, ICruiseController
    {
        public event CruiseTerminateEventDelegate CruiseTerminated = delegate { };

        public virtual string Name => nameof(Retrograde);

        public double terminateSpeed = 5;

        public Retrograde(IAimController aimControl, IMyShipController controller, IList<IMyGyro> gyros)
            : base(aimControl, controller, gyros)
        {

        }

        public virtual void AppendStatus(StringBuilder strb)
        {

        }

        public virtual void Run()
        {
            var shipVelocity = ShipController.GetShipVelocities().LinearVelocity;
            Orient(-shipVelocity);

            if (shipVelocity.LengthSquared() <= terminateSpeed * terminateSpeed)
            {
                Terminate(this, $"Speed is less than {terminateSpeed:0.#} m/s");
            }
        }

        protected void Terminate(ICruiseController source, string reason)
        {
            ResetGyroOverride();
            CruiseTerminated.Invoke(source, reason);
        }

        public virtual void Abort() => Terminate(this, $"Aborted");
        protected override void OnNoFunctionalGyrosLeft() => Terminate(this, "No functional gyros found");
    }
}
