using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript.Navigation
{
    public class Orient : ICruiseController
    {
        public event CruiseTerminateEventDelegate CruiseTerminated = delegate { };

        public string Name => nameof(Orient);
        public IAimController AimControl { get; set; }
        public IMyShipController ShipController { get; set; }
        public IMyGyro GyroInUse { get; set; }
        public Vector3D OrientTarget { get; set; }

        private readonly Queue<IMyGyro> availableGyros;
        private bool terminated = false;

        public Orient(IAimController aimControl, IMyShipController controller, List<IMyGyro> gyros, Vector3D target)
        {
            this.AimControl = aimControl;
            this.ShipController = controller;
            this.availableGyros = new Queue<IMyGyro>();
            this.OrientTarget = target;
            gyros.ForEach(i => availableGyros.Enqueue(i));

            while (availableGyros.Count > 0)
            {
                GyroInUse = availableGyros.Dequeue();
                if (GyroInUse != null && !GyroInUse.Closed && GyroInUse.IsFunctional)
                {
                    break;
                }
            }

            if (GyroInUse == null || GyroInUse.Closed || !GyroInUse.IsFunctional)
            {
                CruiseTerminated.Invoke(this, "No functional gyros found");
                terminated = true;
                return;
            }
        }

        public void AppendStatus(StringBuilder strb)
        {

        }

        public void Run()
        {
            if (terminated)
            {
                return;
            }

            if (GyroInUse == null || GyroInUse.Closed || !GyroInUse.IsFunctional)
            {
                while (availableGyros.Count > 0)
                {
                    GyroInUse = availableGyros.Dequeue();
                    if (GyroInUse != null && !GyroInUse.Closed && GyroInUse.IsFunctional)
                    {
                        break;
                    }
                }
                
                if (GyroInUse == null || GyroInUse.Closed || !GyroInUse.IsFunctional)
                {
                    CruiseTerminated.Invoke(this, "No functional gyros found");
                    terminated = true;
                    return;
                }
            }

            AimControl.Orient(OrientTarget - ShipController.GetPosition(), GyroInUse, ShipController.WorldMatrix);
        }

        public void Abort()
        {

        }
    }
}
