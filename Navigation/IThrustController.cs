using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript
{
    public interface IThrustController
    {
        void DampenAllDirections(Vector3D shipVelocity, float gridMass, float tolerance);
        void ResetThrustOverrides();
        void SetThrusts(Vector3 thrustAmount, float tolerance);
        void SetSideThrusts(float left, float right, float up, float down);
    }
}
