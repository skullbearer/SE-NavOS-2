using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript.Navigation
{
    internal class SpeedMatch : ICruiseController
    {
        public event CruiseTerminateEventDelegate CruiseTerminated;

        public string Name => nameof(SpeedMatch);
        public IMyShipController ShipController { get; set; }
        public Dictionary<Direction, List<IMyThrust>> Thrusters { get; set; }

        public float thrustOverrideMulti = 1; //thrust override multiplier

        public double relativeSpeedThreshold = 0.01;//stop dampening under this relative speed

        private long targetEntityId;
        private WcPbApi wcApi;
        private IMyTerminalBlock pb;

        private float forwardThrust;
        private float backwardThrust;
        private float rightThrust;
        private float leftThrust;
        private float upThrust;
        private float downThrust;

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
            strb.Append("Target: ").Append(targetEntityId);
            if (target.HasValue)
            {
                strb.Append("\nName: ").Append(target.Value.Name);
                strb.Append("\nRelativeVelocity: ").AppendLine(relativeVelocity.Length().ToString("0.0"));
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
            Vector3 relativeVelocityLocal = Vector3D.TransformNormal(relativeVelocity, MatrixD.Transpose(ShipController.WorldMatrix));
            Vector3 thrustAmount = -relativeVelocityLocal * 2 * ShipController.CalculateShipMass().PhysicalMass;

            float backward = thrustAmount.Z < 0 ? Math.Min(-thrustAmount.Z, backwardThrust * thrustOverrideMulti) : 0;
            float forward = thrustAmount.Z > 0 ? Math.Min(thrustAmount.Z, forwardThrust * thrustOverrideMulti) : 0;
            float right = thrustAmount.X < 0 ? Math.Min(-thrustAmount.X, rightThrust * thrustOverrideMulti) : 0;
            float left = thrustAmount.X > 0 ? Math.Min(thrustAmount.X, leftThrust * thrustOverrideMulti) : 0;
            float up = thrustAmount.Y < 0 ? Math.Min(-thrustAmount.Y, upThrust * thrustOverrideMulti) : 0;
            float down = thrustAmount.Y > 0 ? Math.Min(thrustAmount.Y, downThrust * thrustOverrideMulti) : 0;

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
                foreach (var thrust in list)
                {
                    thrust.ThrustOverridePercentage = 0;
                }
            }
        }

        private void UpdateThrustAccel()
        {
            forwardThrust = Thrusters[Direction.Forward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
            backwardThrust = Thrusters[Direction.Backward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
            rightThrust = Thrusters[Direction.Forward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
            leftThrust = Thrusters[Direction.Forward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
            upThrust = Thrusters[Direction.Forward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
            downThrust = Thrusters[Direction.Forward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
        }

        public void Abort()
        {
            ResetThrustOverrides();

            CruiseTerminated.Invoke(this, "Aborted");
        }
    }
}
