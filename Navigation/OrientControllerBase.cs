using Sandbox.Game.Entities;
using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage.ModAPI;
using VRageMath;

namespace IngameScript.Navigation
{
    public abstract class OrientControllerBase
    {
        public IAimController AimControl { get; private set; }
        public IMyShipController ShipController { get; private set; }
        public IMyGyro GyroInUse { get; private set; }

        private readonly Queue<IMyGyro> availableGyros;

        protected OrientControllerBase(IAimController aimControl, IMyShipController controller, IList<IMyGyro> gyros)
        {
            this.AimControl = aimControl;
            this.ShipController = controller;
            this.availableGyros = new Queue<IMyGyro>();
            foreach (var gyro in gyros)
            {
                availableGyros.Enqueue(gyro);
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
                    OnNoFunctionalGyrosLeft();
                    return;
                }

                GyroInUse.Enabled = true;
            }
        }

        protected void Orient(Vector3D forward)
        {
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
                    OnNoFunctionalGyrosLeft();
                    return;
                }

                GyroInUse.Enabled = true;
            }

            AimControl.Orient(forward, GyroInUse, ShipController.WorldMatrix);
        }

        protected void ResetGyroOverride()
        {
            if (GyroInUse != null)
            {
                GyroInUse.Pitch = 0;
                GyroInUse.Yaw = 0;
                GyroInUse.Roll = 0;
                GyroInUse.GyroOverride = false;
            }
        }

        protected abstract void OnNoFunctionalGyrosLeft();
    }
}
