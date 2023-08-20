using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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

        public float thrustOverrideMulti = 1; //thrust override multiplier

        public double relativeSpeedThreshold = 0.01;//stop dampening under this relative speed

        private long targetEntityId;
        private WcPbApi wcApi;
        private IMyTerminalBlock pb;

        private float forwardAccel;
        private float backwardAccel;
        private float rightAccel;
        private float leftAccel;
        private float upAccel;
        private float downAccel;

        private int counter = 0;
        private Dictionary<MyDetectedEntityInfo, float> threats = new Dictionary<MyDetectedEntityInfo, float>();
        private Vector3D relativeVelocity;

        private MyDetectedEntityInfo? target;

        public SpeedMatch(
            long targetEntityId,
            WcPbApi wcApi,
            IMyShipController shipController,
            Dictionary<Direction, List<IMyThrust>> thrusters,
            IMyTerminalBlock programmableBlock)
        {
            this.targetEntityId = targetEntityId;
            this.wcApi = wcApi;
            this.ShipController = shipController;
            this.Thrusters = thrusters;
            this.pb = programmableBlock;
        }

        public void AppendStatus(StringBuilder strb)
        {
            strb.AppendLine("-- SpeedMatch Status --");
            strb.Append("Target: ").Append(targetEntityId).AppendLine();
            if (target.HasValue)
            {
                strb.Append("Name: ").AppendLine(target.Value.Name);
                strb.Append("RelativeVelocity: ").AppendLine(relativeVelocity.Length().ToString("0.0"));
                //maybe add some more info about the target?
            }
        }

        public void Run()
        {
            counter++;
            if (counter % 10 == 0)
            {
                ShipController.DampenersOverride = false;

                target = null;

                try
                {
                    //target = wcApi.GetAiFocus(ShipController.CubeGrid.EntityId);

                    //support changing main target after running speedmatch
                    wcApi.GetSortedThreats(pb, threats);
                    foreach (var threat in threats.Keys)
                    {
                        if (threat.EntityId == targetEntityId)
                        {
                            target = threat;
                            break;
                        }
                    }
                }
                catch { }

                UpdateThrustAccel();
            }

            if (!target.HasValue)
            {
                ResetThrustOverrides();
                return;
            }

            relativeVelocity = target.Value.Velocity - ShipController.GetShipVelocities().LinearVelocity;
            Vector3 relativeVelocityLocal = -Vector3D.TransformNormal(relativeVelocity, MatrixD.Transpose(ShipController.WorldMatrix));

            float backward = relativeVelocityLocal.Z < relativeSpeedThreshold ? Math.Min(-relativeVelocityLocal.Z / backwardAccel, 1) * thrustOverrideMulti : 0;
            float forward = relativeVelocityLocal.Z > relativeSpeedThreshold ? Math.Min(relativeVelocityLocal.Z / forwardAccel, 1) * thrustOverrideMulti : 0;
            float right = relativeVelocityLocal.X < relativeSpeedThreshold ? Math.Min(-relativeVelocityLocal.X / rightAccel, 1) * thrustOverrideMulti : 0;
            float left = relativeVelocityLocal.X > relativeSpeedThreshold ? Math.Min(relativeVelocityLocal.X / leftAccel, 1) * thrustOverrideMulti : 0;
            float up = relativeVelocityLocal.Y < relativeSpeedThreshold ? Math.Min(-relativeVelocityLocal.Y / upAccel, 1) * thrustOverrideMulti : 0;
            float down = relativeVelocityLocal.Y > relativeSpeedThreshold ? Math.Min(relativeVelocityLocal.Y / downAccel, 1) * thrustOverrideMulti : 0;

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

        private void UpdateThrustAccel()
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
