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
                ResetGyroOverride();

                RaiseCruiseTerminated(this, $"Speed is less than {terminateSpeed:0.#} m/s");
            }
        }

        public virtual void Abort()
        {
            ResetGyroOverride();

            RaiseCruiseTerminated(this, $"Aborted");
        }

        protected void RaiseCruiseTerminated(ICruiseController source, string reason)
        {
            CruiseTerminated.Invoke(source, reason);
        }

        protected override void OnNoFunctionalGyrosLeft()
        {
            RaiseCruiseTerminated(this, "No functional gyros found");
        }
    }
}
