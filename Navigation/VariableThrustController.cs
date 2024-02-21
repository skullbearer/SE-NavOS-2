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

        public Dictionary<Direction, IList<IMyThrust>> Thrusters { get; }

        private float _maxThrustOverrideRatio = 1f;
        private IMyShipController shipController;

        //inverse total directional thrust force
        private float forwardThrustInv = 0;
        private float backThrustInv = 0;
        private float leftThrustInv = 0;
        private float rightThrustInv = 0;
        private float upThrustInv = 0;
        private float downThrustInv = 0;

        public VariableThrustController(Dictionary<Direction, List<IMyThrust>> thrusters, IMyShipController shipController)
        {
            this.Thrusters = thrusters.ToDictionary(
                kv => kv.Key,
                kv => (IList<IMyThrust>)thrusters[kv.Key].ToArray());
            this.shipController = shipController;
        }

        public void UpdateThrusts()
        {
            foreach (var kv in Thrusters)
            {
                float total = 0;
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    total += kv.Value[i].MaxEffectiveThrust;
                }
                total = 1.0f / total;
                switch (kv.Key)
                {
                    case Direction.Forward: forwardThrustInv = total; break;
                    case Direction.Backward: backThrustInv = total; break;
                    case Direction.Left: leftThrustInv = total; break;
                    case Direction.Right: rightThrustInv = total; break;
                    case Direction.Up: upThrustInv = total; break;
                    case Direction.Down: downThrustInv = total; break;
                }
            }
        }

        public void DampenAllDirections(Vector3D shipVelocity, float gridMass, float tolerance)
        {
            Vector3 localVelocity = Vector3D.TransformNormal(shipVelocity, MatrixD.Transpose(shipController.WorldMatrix));
            Vector3 thrustAmount = localVelocity * gridMass;
            SetThrusts(thrustAmount, tolerance);
        }

        public void SetThrusts(Vector3 thrustAmount, float tolerance, bool stopDamp = false)
        {
            float backward = thrustAmount.Z < tolerance ? -thrustAmount.Z : stopDamp ? 0 : float.MinValue;
            float forward = thrustAmount.Z > tolerance ? thrustAmount.Z : stopDamp ? 0 : float.MinValue;
            float right = thrustAmount.X < tolerance ? -thrustAmount.X : stopDamp ? 0 : float.MinValue;
            float left = thrustAmount.X > tolerance ? thrustAmount.X : stopDamp ? 0 : float.MinValue;
            float up = thrustAmount.Y < tolerance ? -thrustAmount.Y : stopDamp ? 0 : float.MinValue;
            float down = thrustAmount.Y > tolerance ? thrustAmount.Y : stopDamp ? 0 : float.MinValue;

            SetSideThrusts(left, right, up, down);

            backward *= backThrustInv;
            forward = Math.Min(forward * forwardThrustInv, MaxThrustRatio);

            foreach (var thrust in Thrusters[Direction.Forward]) thrust.ThrustOverridePercentage = forward;
            foreach (var thrust in Thrusters[Direction.Backward]) thrust.ThrustOverridePercentage = backward;
        }

        public void SetSideThrusts(float left, float right, float up, float down)
        {
            left *= leftThrustInv;
            right *= rightThrustInv;
            up *= upThrustInv;
            down *= downThrustInv;

            foreach (var thrust in Thrusters[Direction.Left]) thrust.ThrustOverridePercentage = left;
            foreach (var thrust in Thrusters[Direction.Right]) thrust.ThrustOverridePercentage = right;
            foreach (var thrust in Thrusters[Direction.Up]) thrust.ThrustOverridePercentage = up;
            foreach (var thrust in Thrusters[Direction.Down]) thrust.ThrustOverridePercentage = down;
        }

        public void ResetThrustOverrides()
        {
            foreach (var kv in Thrusters)
                for (int i = 0; i < kv.Value.Count; i++)
                    kv.Value[i].ThrustOverride = 0;
        }

        public void ThrustOverrideZero()
        {
            foreach (var kv in Thrusters)
                for (int i = 0; i < kv.Value.Count; i++)
                    kv.Value[i].ThrustOverride = float.MinValue;
        }

        public void OnOffThrust(Direction dir = Direction.Forward, bool on = true)
        {// To avoid an occasional bug, where turning on or off the thruster fails when override is not zero, zero the override first.
            foreach (var thrust in Thrusters[Direction.Forward]) if (on) { thrust.ThrustOverride = 0f; thrust.Enabled = true;} else { thrust.ThrustOverride = 0f; thrust.Enabled = false;}
        }

    }
}
