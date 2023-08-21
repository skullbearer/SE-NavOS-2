using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript.Navigation
{
    public class Retrograde : ICruiseController
    {
        public event CruiseTerminateEventDelegate CruiseTerminated = delegate { };

        public virtual string Name => nameof(Retrograde);
        public IAimController AimControl { get; set; }
        public IMyShipController Controller { get; set; }
        public IMyGyro Gyro { get; set; }

        public double terminateSpeed = 5;

        public Retrograde(IAimController aimControl, IMyShipController controller, IMyGyro gyro)
        {
            this.AimControl = aimControl;
            this.Controller = controller;
            this.Gyro = gyro;

            gyro.Enabled = true;
        }

        public virtual void AppendStatus(StringBuilder strb)
        {

        }

        public virtual void Run()
        {
            var shipVelocity = Controller.GetShipVelocities().LinearVelocity;
            Orient(-shipVelocity);

            if (shipVelocity.LengthSquared() <= terminateSpeed * terminateSpeed)
            {
                ResetGyroOverride();

                RaiseCruiseTerminated(this, $"Speed is less than {terminateSpeed:0.#} m/s");
            }
        }

        protected void Orient(Vector3D forward)
        {
            AimControl.Orient(forward, Gyro, Controller.WorldMatrix);
        }

        protected void ResetGyroOverride()
        {
            Gyro.Pitch = 0;
            Gyro.Yaw = 0;
            Gyro.Roll = 0;
            Gyro.GyroOverride = false;
        }

        public virtual void Abort()
        {
            ResetGyroOverride();

            RaiseCruiseTerminated(this, $"Aborted");
        }

        protected void RaiseCruiseTerminated(ICruiseController source, string reason)
        {
            CruiseTerminated.Invoke(source, reason);
        }
    }
}
