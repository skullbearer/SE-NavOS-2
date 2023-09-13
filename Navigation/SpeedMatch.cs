using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VRage;
using VRageMath;

namespace IngameScript.Navigation
{
    internal class SpeedMatch : ICruiseController, IVariableMaxOverrideThrustController
    {
        public event CruiseTerminateEventDelegate CruiseTerminated;

        public string Name => nameof(SpeedMatch);
        public IMyShipController ShipController { get; set; }
        public float MaxThrustOverrideRatio
        {
            get { return _maxThrustOverrideRatio; }
            set
            {
                if (_maxThrustOverrideRatio != value)
                {
                    _maxThrustOverrideRatio = value;
                    UpdateThrust();
                }
            }
        }

        public double relativeSpeedThreshold = 0.01;//stop dampening under this relative speed
        public int thrustInterval = 2;

        private float _maxThrustOverrideRatio = 1; //thrust override multiplier

        private long targetEntityId;
        private WcPbApi wcApi;
        private IMyTerminalBlock pb;

        //private float forwardThrust;
        //private float backwardThrust;
        //private float rightThrust;
        //private float leftThrust;
        //private float upThrust;
        //private float downThrust;

        private Dictionary<Direction, MyTuple<IMyThrust, float>[]> thrusters;
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
            this.thrusters = thrusters.ToDictionary(
                kv => kv.Key,
                kv => thrusters[kv.Key]
                    .Select(thrust => new MyTuple<IMyThrust, float>(thrust, thrust.MaxEffectiveThrust * MaxThrustOverrideRatio))
                    .ToArray());
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
                    //support changing main target after running speedmatch
                    threats.Clear();

                    var aifocus = wcApi.GetAiFocus(pb.EntityId);
                    if (aifocus?.EntityId == targetEntityId)
                    {
                        target = aifocus.Value;
                    }
                    else
                    {
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
                }
                catch { }

                if (!target.HasValue)
                {
                    ResetThrustOverrides();
                    return;
                }

                UpdateThrust();
            }

            if (!target.HasValue)
            {
                return;
            }

            if (counter % thrustInterval == 0)
            {
                relativeVelocity = target.Value.Velocity - ShipController.GetShipVelocities().LinearVelocity;
                Vector3 relativeVelocityLocal = Vector3D.TransformNormal(relativeVelocity, MatrixD.Transpose(ShipController.WorldMatrix));
                Vector3 thrustAmount = -relativeVelocityLocal * 2 * ShipController.CalculateShipMass().PhysicalMass;
                thrustAmount *= 0.5f / thrustInterval;
                Vector3 input = ShipController.MoveIndicator;
                bool x0 = Math.Abs(input.X) <= 0.01;
                bool y0 = Math.Abs(input.Y) <= 0.01;
                bool z0 = Math.Abs(input.Z) <= 0.01;

                float backward = thrustAmount.Z < 0 && z0 ? -thrustAmount.Z : 0;
                float forward = thrustAmount.Z > 0 && z0 ? thrustAmount.Z : 0;
                float right = thrustAmount.X < 0 && x0 ? -thrustAmount.X : 0;
                float left = thrustAmount.X > 0 && x0 ? thrustAmount.X : 0;
                float up = thrustAmount.Y < 0 && y0 ? -thrustAmount.Y : 0;
                float down = thrustAmount.Y > 0 && y0 ? thrustAmount.Y : 0;

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
        }

        private void ResetThrustOverrides()
        {
            foreach (var kv in thrusters)
            {
                for (int i = 0; i < kv.Value.Length; i++)
                {
                    kv.Value[i].Item1.ThrustOverridePercentage = 0;
                }
            }
        }

        private void UpdateThrust()
        {
            foreach (var kv in thrusters)
            {
                for (int i = 0; i < kv.Value.Length; i++)
                {
                    var val = kv.Value[i];
                    val.Item2 = val.Item1.MaxEffectiveThrust * MaxThrustOverrideRatio;
                    kv.Value[i] = val;
                }
            }
        }

        public void Abort()
        {
            ResetThrustOverrides();

            CruiseTerminated.Invoke(this, "Aborted");
        }
    }
}
