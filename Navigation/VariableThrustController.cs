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
    public class VariableThrustController : IVariableThrustController, IThrustController
    {
        public float MaxThrustRatio
        {
            get { return _maxThrustOverrideRatio; }
            set
            {
                if (_maxThrustOverrideRatio != value)
                {
                    _maxThrustOverrideRatio = value;
                    UpdateThrusts();
                }
            }
        }

        public Dictionary<Direction, MyTuple<IMyThrust, float>[]> Thrusters { get; }

        private float _maxThrustOverrideRatio = 1f;
        private IMyShipController shipController;

        public VariableThrustController(Dictionary<Direction, List<IMyThrust>> thrusters, IMyShipController shipController)
        {
            this.Thrusters = thrusters.ToDictionary(
                kv => kv.Key,
                kv => thrusters[kv.Key]
                    .Select(thrust => new MyTuple<IMyThrust, float>(thrust, thrust.MaxEffectiveThrust * MaxThrustRatio))
                    .ToArray());
            this.shipController = shipController;
        }

        public void UpdateThrusts()
        {
            foreach (var kv in Thrusters)
            {
                for (int i = 0; i < kv.Value.Length; i++)
                {
                    var val = kv.Value[i];
                    val.Item2 = val.Item1.MaxEffectiveThrust * MaxThrustRatio;
                    kv.Value[i] = val;
                }
            }
        }

        public void DampenAllDirections(Vector3D shipVelocity, float gridMass, float tolerance)
        {
            Vector3 localVelocity = Vector3D.TransformNormal(shipVelocity, MatrixD.Transpose(shipController.WorldMatrix));
            Vector3 thrustAmount = localVelocity * 2 * gridMass;
            SetThrusts(thrustAmount, tolerance);
        }

        public void SetThrusts(Vector3 thrustAmount, float tolerance)
        {
            float backward = thrustAmount.Z < tolerance ? -thrustAmount.Z : 0;
            float forward = thrustAmount.Z > tolerance ? thrustAmount.Z : 0;
            float right = thrustAmount.X < tolerance ? -thrustAmount.X : 0;
            float left = thrustAmount.X > tolerance ? thrustAmount.X : 0;
            float up = thrustAmount.Y < tolerance ? -thrustAmount.Y : 0;
            float down = thrustAmount.Y > tolerance ? thrustAmount.Y : 0;

            foreach (var thrust in Thrusters[Direction.Forward]) thrust.Item1.ThrustOverride = Math.Min(forward, thrust.Item2);
            foreach (var thrust in Thrusters[Direction.Backward]) thrust.Item1.ThrustOverride = backward;
            SetSideThrusts(left, right, up, down);
        }

        public void SetSideThrusts(float left, float right, float up, float down)
        {
            foreach (var thrust in Thrusters[Direction.Left]) thrust.Item1.ThrustOverride = left;
            foreach (var thrust in Thrusters[Direction.Right]) thrust.Item1.ThrustOverride = right;
            foreach (var thrust in Thrusters[Direction.Up]) thrust.Item1.ThrustOverride = up;
            foreach (var thrust in Thrusters[Direction.Down]) thrust.Item1.ThrustOverride = down;
        }

        public void ResetThrustOverrides()
        {
            foreach (var kv in Thrusters)
                for (int i = 0; i < kv.Value.Length; i++)
                    kv.Value[i].Item1.ThrustOverride = 0;
        }
    }
}
