using Sandbox.Game.Entities;
using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage;
using VRageMath;

namespace IngameScript.Navigation
{
    public class Retroburn : Retrograde
    {
        const float DAMPENER_TOLERANCE = 0.005f;

        public override string Name => nameof(Retroburn);

        public float maxThrustOverrideRatio = 1f;
        public int runInterval = 10;

        private Dictionary<Direction, MyTuple<IMyThrust, float>[]> thrusters;

        private float gridMass;

        private int counter = -1;

        public Retroburn(
            IAimController aimControl,
            IMyShipController controller,
            IMyGyro gyro,
            Dictionary<Direction, List<IMyThrust>> thrusters)
            : base(aimControl, controller, gyro)
        {
            this.thrusters = thrusters.ToDictionary(
                kv => kv.Key,
                kv => thrusters[kv.Key]
                    .Select(thrust => new MyTuple<IMyThrust, float>(thrust, thrust.MaxEffectiveThrust * maxThrustOverrideRatio))
                    .ToArray());
        }

        public override void Run()
        {
            counter++;
            if (counter % 60 == 0)
            {
                gridMass = Controller.CalculateShipMass().PhysicalMass;
                UpdateThrust();
            }

            Vector3D shipVelocity = Controller.GetShipVelocities().LinearVelocity;
            double velocitySq = shipVelocity.LengthSquared();

            if (velocitySq > terminateSpeed * terminateSpeed)
            {
                Orient(-shipVelocity);
            }
            else
            {
                ResetGyroOverride();
            }

            if (counter % runInterval == 0)
            {
                Controller.DampenersOverride = false;
                Vector3D shipVelocityNormalized = shipVelocity.SafeNormalize();

                if (Vector3D.Dot(-shipVelocityNormalized, Controller.WorldMatrix.Forward) > 0.999999)
                {
                    DampenAllDirections(shipVelocity);
                }
                else
                {
                    ResetThrustOverrides();
                }
            }

            if (velocitySq <= DAMPENER_TOLERANCE * DAMPENER_TOLERANCE)
            {
                ResetThrustOverrides();
                ResetGyroOverride();
                Controller.DampenersOverride = true;
                RaiseCruiseTerminated(this, $"Speed is less than {DAMPENER_TOLERANCE} m/s");
            }
        }

        private void UpdateThrust()
        {
            foreach (var kv in thrusters)
            {
                for (int i = 0; i < kv.Value.Length; i++)
                {
                    var val = kv.Value[i];
                    val.Item2 = val.Item1.MaxEffectiveThrust * maxThrustOverrideRatio;
                    kv.Value[i] = val;
                }
            }
        }

        private void DampenAllDirections(Vector3D shipVelocity)
        {
            Vector3 localVelocity = Vector3D.TransformNormal(shipVelocity, MatrixD.Transpose(Controller.WorldMatrix));
            Vector3 thrustAmount = localVelocity * 2 * gridMass / runInterval;
            float backward = thrustAmount.Z < DAMPENER_TOLERANCE ? -thrustAmount.Z : 0;
            float forward = thrustAmount.Z > DAMPENER_TOLERANCE ? thrustAmount.Z : 0;
            float right = thrustAmount.X < DAMPENER_TOLERANCE ? -thrustAmount.X : 0;
            float left = thrustAmount.X > DAMPENER_TOLERANCE ? thrustAmount.X : 0;
            float up = thrustAmount.Y < DAMPENER_TOLERANCE ? -thrustAmount.Y : 0;
            float down = thrustAmount.Y > DAMPENER_TOLERANCE ? thrustAmount.Y : 0;

            foreach (var thrust in thrusters[Direction.Forward])
                thrust.Item1.ThrustOverride = Math.Min(forward, thrust.Item2);
            foreach (var thrust in thrusters[Direction.Backward])
                thrust.Item1.ThrustOverride = backward;
            foreach (var thrust in thrusters[Direction.Right])
                thrust.Item1.ThrustOverride = right;
            foreach (var thrust in thrusters[Direction.Left])
                thrust.Item1.ThrustOverride = left;
            foreach (var thrust in thrusters[Direction.Up])
                thrust.Item1.ThrustOverride = up;
            foreach (var thrust in thrusters[Direction.Down])
                thrust.Item1.ThrustOverride = down;
        }

        private void ResetThrustOverrides()
        {
            foreach (var list in thrusters.Values)
            {
                foreach (var thruster in list)
                {
                    thruster.Item1.ThrustOverridePercentage = 0;
                }
            }
        }
    }
}
