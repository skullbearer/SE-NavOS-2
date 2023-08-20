using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IngameScript.Navigation
{
    internal class Prograde : ICruiseController
    {
        public event CruiseTerminateEventDelegate CruiseTerminated = delegate { };

        public string Name => nameof(Prograde);
        public IAimController AimControl { get; set; }
        public IMyShipController Controller { get; set; }
        public IMyGyro Gyro { get; set; }

        public double terminateSpeed = 5;

        public Prograde(IAimController aimControl, IMyShipController controller, IMyGyro gyro)
        {
            this.AimControl = aimControl;
            this.Controller = controller;
            this.Gyro = gyro;

            gyro.Enabled = true;
        }

        public void Run()
        {
            var shipVelocity = Controller.GetShipVelocities().LinearVelocity;
            AimControl.Orient(shipVelocity, Gyro, Controller.WorldMatrix);

            if (shipVelocity.LengthSquared() <= terminateSpeed * terminateSpeed)
            {
                ResetGyroOverride();

                CruiseTerminated.Invoke(this, $"Speed is less than {terminateSpeed:0.#} m/s");
            }
        }

        public void AppendStatus(StringBuilder strb)
        {

        }

        private void ResetGyroOverride()
        {
            Gyro.Pitch = 0;
            Gyro.Yaw = 0;
            Gyro.Roll = 0;
            Gyro.GyroOverride = false;
        }

        public void Abort()
        {
            ResetGyroOverride();

            CruiseTerminated.Invoke(this, $"Aborted");
        }
    }
}
