using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript
{
    internal class Retrograde : ICruiseController
    {
        public event Action CruiseCompleted;

        public string Name => nameof(Retrograde);
        public IAimController AimControl { get; set; }
        public IMyShipController Controller { get; set; }
        public IMyGyro Gyro { get; set; }

        /// <summary>
        /// How often to update things like ship velocity, desireddirection ,etc
        /// </summary>
        public int valueUpdateInterval = 10;

        public double terminateSpeed = 5;

        private int counter = 0;

        public Retrograde(IAimController aimControl, IMyShipController controller, IMyGyro gyro)
        {
            this.AimControl = aimControl;
            this.Controller = controller;
            this.Gyro = gyro;
        }

        public void Run()
        {
            //counter++;
            //if (counter % valueUpdateInterval == 0)
            //{
            //
            //}

            var shipVelocity = Controller.GetShipVelocities().LinearVelocity;
            AimControl.Orient(-shipVelocity, Gyro, Controller.WorldMatrix);

            if (shipVelocity.LengthSquared() <= terminateSpeed * terminateSpeed)
            {
                Abort();
                CruiseCompleted?.Invoke();
            }
        }

        public void AppendStatus(StringBuilder strb)
        {

        }

        public void Abort()
        {
            Gyro.Pitch = 0;
            Gyro.Yaw = 0;
            Gyro.Roll = 0;
            Gyro.GyroOverride = false;
        }
    }
}
