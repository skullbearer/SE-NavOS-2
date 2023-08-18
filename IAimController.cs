using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript
{
    public interface IAimController
    {
        void Orient(Vector3D forward, IMyGyro gyro, MatrixD refMatrix);
        void Orient(Vector3D forward, Vector3D up, IMyGyro gyro, MatrixD refMatrix);
        void Reset();
    }
}
