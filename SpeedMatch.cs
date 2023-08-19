using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript
{
    internal class SpeedMatch : ICruiseController
    {
        public event Action CruiseCompleted;

        public string Name => nameof(SpeedMatch);
        public IMyShipController ShipController { get; set; }
        public Dictionary<Direction, List<IMyThrust>> Thrusters { get; set; }

        private long targetEntityId;
        private WcPbApi wcApi;
        private int counter = 0;

        private float forwardAccel;
        private float backwardAccel;
        private float rightAccel;
        private float leftAccel;
        private float upAccel;
        private float downAccel;

        public const float overrideMulti = (float)Program.maxThrustOverridePercent;

        public SpeedMatch(
            long targetEntityId,
            WcPbApi wcApi,
            IMyShipController shipController,
            Dictionary<Direction, List<IMyThrust>> thrusters)
        {
            this.targetEntityId = targetEntityId;
            this.wcApi = wcApi;
            this.ShipController = shipController;
            this.Thrusters = thrusters;
        }

        public void AppendStatus(StringBuilder strb)
        {

        }

        public void Run()
        {
            counter++;
            if (counter % 10 == 0)
            {
                MyDetectedEntityInfo? target;
                try
                {
                    target = wcApi.GetAiFocus(ShipController.CubeGrid.EntityId);
                }
                catch
                {
                    target = null;
                }

                if (!target.HasValue || target.Value.EntityId != targetEntityId)
                {
                    ResetThrustOverrides();
                    return;
                }

                UpdateThrust();

                Vector3D relativeVelocity = target.Value.Velocity - ShipController.GetShipVelocities().LinearVelocity;
                Vector3 relativeVelocityLocal = Vector3D.TransformNormal(relativeVelocity, MatrixD.Transpose(ShipController.WorldMatrix));
                
                float forward = 0;
                float backward = 0;
                float right = 0;
                float left = 0;
                float up = 0;
                float down = 0;

                if (relativeVelocityLocal.Z < 0)
                    forward = Math.Min(-relativeVelocityLocal.Z / forwardAccel, 1) * overrideMulti;
                else if (relativeVelocityLocal.Z > 0)
                    backward = Math.Min(relativeVelocityLocal.Z / backwardAccel, 1) * overrideMulti;

                if (relativeVelocityLocal.X < 0)
                    right = Math.Min(-relativeVelocityLocal.X / rightAccel, 1) * overrideMulti;
                else if (relativeVelocityLocal.X > 0)
                    left = Math.Min(relativeVelocityLocal.X / leftAccel, 1) * overrideMulti;

                if (relativeVelocityLocal.Y < 0)
                    up = Math.Min(-relativeVelocityLocal.Y / upAccel, 1) * overrideMulti;
                else if (relativeVelocityLocal.Y > 0)
                    down = Math.Min(relativeVelocityLocal.Y / downAccel, 1) * overrideMulti;

                foreach (var thrust in Thrusters[Direction.Forward])
                    thrust.ThrustOverridePercentage = forward;

                foreach (var thrust in Thrusters[Direction.Backward])
                    thrust.ThrustOverridePercentage = backward;

                foreach (var thrust in Thrusters[Direction.Right])
                    thrust.ThrustOverridePercentage = right;

                foreach (var thrust in Thrusters[Direction.Left])
                    thrust.ThrustOverridePercentage = left;

                foreach (var thrust in Thrusters[Direction.Up])
                    thrust.ThrustOverridePercentage = up;

                foreach (var thrust in Thrusters[Direction.Down])
                    thrust.ThrustOverridePercentage = down;
            }
        }

        private void ResetThrustOverrides()
        {
            foreach (var list in Thrusters.Values)
            {
                foreach (var thrust in list)
                {
                    thrust.ThrustOverridePercentage = 0;
                }
            }
        }

        private void UpdateThrust()
        {
            float gridMass = ShipController.CalculateShipMass().PhysicalMass;

            forwardAccel = Thrusters[Direction.Forward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust) / gridMass;
            backwardAccel = Thrusters[Direction.Backward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust) / gridMass;
            rightAccel = Thrusters[Direction.Forward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust) / gridMass;
            leftAccel = Thrusters[Direction.Forward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust) / gridMass;
            upAccel = Thrusters[Direction.Forward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust) / gridMass;
            downAccel = Thrusters[Direction.Forward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust) / gridMass;
        }

        public void Abort()
        {
            ResetThrustOverrides();
        }
    }
}
