using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IngameScript
{
    internal class Prograde : OrientControllerBase, ICruiseController
    {
        public event CruiseTerminateEventDelegate CruiseTerminated = delegate { };

        public string Name => nameof(Prograde);
        public IMyShipController Controller { get; set; }

        public double terminateSpeed = 5;

        public Prograde(IAimController aimControl, IMyShipController controller, IList<IMyGyro> gyros)
            : base(aimControl, controller, gyros)
        {
            this.Controller = controller;
        }

        public void Run()
        {
            var shipVelocity = Controller.GetShipVelocities().LinearVelocity;
            Orient(shipVelocity);

            if (shipVelocity.LengthSquared() <= terminateSpeed * terminateSpeed)
            {
                Terminate($"Speed is less than {terminateSpeed:0.#} m/s");
            }
        }

        public void AppendStatus(StringBuilder strb) { }

        public void Terminate(string reason)
        {
            ResetGyroOverride();
            CruiseTerminated.Invoke(this, reason);
        }

        public void Abort() => Terminate("Aborted");
        protected override void OnNoFunctionalGyrosLeft() => Terminate("No functional gyros found");
    }
}
