using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IngameScript
{
    public class Retroburn : Retrograde
    {
        public override string Name => nameof(Retroburn);

        public Retroburn(
            IAimController aimControl,
            IMyShipController controller,
            IMyGyro gyro,
            Dictionary<Direction, List<IMyThrust>> thrusters)
            : base(aimControl, controller, gyro)
        {

        }

        public override void Run()
        {
            var shipVelocity = Controller.GetShipVelocities().LinearVelocity;
            Orient(-shipVelocity);

            if (shipVelocity.LengthSquared() <= terminateSpeed * terminateSpeed)
            {
                ResetGyroOverride();

                RaiseCruiseTerminated(this, $"Speed is less than {terminateSpeed:0.#} m/s");
            }
        }
    }
}
