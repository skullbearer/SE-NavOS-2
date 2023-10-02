using Sandbox.Game.Entities;
using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage;
using VRageMath;

namespace IngameScript
{
    public class Retroburn : Retrograde, ICruiseController
    {
        const float DAMPENER_TOLERANCE = 0.005f;

        public override string Name => nameof(Retroburn);

        public int runInterval = 10;

        private IVariableThrustController thrustController;
        private float gridMass;
        private int counter = -1;

        public Retroburn(
            IAimController aimControl,
            IMyShipController controller,
            List<IMyGyro> gyros,
            IVariableThrustController thrustController)
            : base(aimControl, controller, gyros)
        {
            this.thrustController = thrustController;
        }

        public override void Run()
        {
            counter++;
            if (counter % 60 == 0)
            {
                gridMass = ShipController.CalculateShipMass().PhysicalMass;
                thrustController.UpdateThrusts();
            }

            Vector3D shipVelocity = ShipController.GetShipVelocities().LinearVelocity;
            double velocitySq = shipVelocity.LengthSquared();

            if (velocitySq > terminateSpeed * terminateSpeed)
                Orient(-shipVelocity);
            else
                ResetGyroOverride();

            if (counter % runInterval == 0)
            {
                ShipController.DampenersOverride = false;
                Vector3D shipVelocityNormalized = shipVelocity.SafeNormalize();

                if (Vector3D.Dot(-shipVelocityNormalized, ShipController.WorldMatrix.Forward) > 0.999999)
                    thrustController.DampenAllDirections(shipVelocity / runInterval, gridMass, DAMPENER_TOLERANCE);
                else
                    thrustController.ResetThrustOverrides();
            }

            if (velocitySq <= DAMPENER_TOLERANCE * DAMPENER_TOLERANCE)
            {
                thrustController.ResetThrustOverrides();
                ShipController.DampenersOverride = true;
                Terminate(this, $"Speed is less than {DAMPENER_TOLERANCE} m/s");
            }
        }
    }
}
