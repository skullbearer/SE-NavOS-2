using Sandbox.Game.Entities;
using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript.Navigation
{
    public class Retroburn : Retrograde
    {
        public override string Name => nameof(Retroburn);
        public Dictionary<Direction, List<IMyThrust>> Thrusters { get; set; }

        public float maxThrustOverrideRatio = 1f;
        public int runInterval = 10;

        private float gridMass;

        private float forwardThrust;
        private float backThrust;
        private float rightThrust;
        private float leftThrust;
        private float upThrust;
        private float downThrust;

        const float DAMPENER_TOLERANCE = 0.005f;

        private int counter = 0;

        public Retroburn(
            IAimController aimControl,
            IMyShipController controller,
            IMyGyro gyro,
            Dictionary<Direction, List<IMyThrust>> thrusters)
            : base(aimControl, controller, gyro)
        {
            this.Thrusters = thrusters;
        }

        public override void Run()
        {
            counter++;
            if (counter % 30 == 0)
            {
                gridMass = Controller.CalculateShipMass().PhysicalMass;
                UpdateThrust();
            }

            Vector3D shipVelocity = Controller.GetShipVelocities().LinearVelocity;
            double velocitySq = shipVelocity.LengthSquared();

            if (counter % runInterval == 0)
            {
                DampenAllDirections(shipVelocity);
            }

            if (velocitySq > terminateSpeed * terminateSpeed)
            {
                Orient(-shipVelocity);
            }
            else
            {
                ResetGyroOverride();
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
            forwardThrust = Thrusters[Direction.Forward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
            backThrust = Thrusters[Direction.Backward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
            rightThrust = Thrusters[Direction.Right].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
            leftThrust = Thrusters[Direction.Left].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
            upThrust = Thrusters[Direction.Up].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
            downThrust = Thrusters[Direction.Down].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
        }

        private void DampenAllDirections(Vector3D shipVelocity)
        {
            Vector3 localVelocity = Vector3D.TransformNormal(shipVelocity, MatrixD.Transpose(Controller.WorldMatrix));
            Vector3 thrustAmount = localVelocity * 2 * gridMass / runInterval;
            float backward = thrustAmount.Z < DAMPENER_TOLERANCE ? Math.Min(-thrustAmount.Z, backThrust * maxThrustOverrideRatio) : 0;
            float forward = thrustAmount.Z > DAMPENER_TOLERANCE ? Math.Min(thrustAmount.Z, forwardThrust * maxThrustOverrideRatio) : 0;
            float right = thrustAmount.X < DAMPENER_TOLERANCE ? Math.Min(-thrustAmount.X, rightThrust * maxThrustOverrideRatio) : 0;
            float left = thrustAmount.X > DAMPENER_TOLERANCE ? Math.Min(thrustAmount.X, leftThrust * maxThrustOverrideRatio) : 0;
            float up = thrustAmount.Y < DAMPENER_TOLERANCE ? Math.Min(-thrustAmount.Y, upThrust * maxThrustOverrideRatio) : 0;
            float down = thrustAmount.Y > DAMPENER_TOLERANCE ? Math.Min(thrustAmount.Y, downThrust * maxThrustOverrideRatio) : 0;

            foreach (var thrust in Thrusters[Direction.Forward])
                thrust.ThrustOverride = forward;
            foreach (var thrust in Thrusters[Direction.Backward])
                thrust.ThrustOverride = backward;
            foreach (var thrust in Thrusters[Direction.Right])
                thrust.ThrustOverride = right;
            foreach (var thrust in Thrusters[Direction.Left])
                thrust.ThrustOverride = left;
            foreach (var thrust in Thrusters[Direction.Up])
                thrust.ThrustOverride = up;
            foreach (var thrust in Thrusters[Direction.Down])
                thrust.ThrustOverride = down;
        }

        private void ResetThrustOverrides()
        {
            foreach (var list in Thrusters.Values)
            {
                foreach (var thruster in list)
                {
                    thruster.ThrustOverridePercentage = 0;
                }
            }
        }
    }
}
