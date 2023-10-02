using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript
{
    internal class CalibrateTurnTime : OrientControllerBase, ICruiseController
    {
        public event CruiseTerminateEventDelegate CruiseTerminated;

        public string Name => nameof(CalibrateTurnTime);

        const double TICK = 1.0 / 60.0;
        private const double orientToleranceAngleRadians = 0.075 * (Math.PI / 180.0);

        private double elapsedTimeMs;
        private Vector3D target;

        private Config _config;

        public CalibrateTurnTime(Config config, IAimController aimControl, IMyShipController controller, IList<IMyGyro> gyros)
            : base(aimControl, controller, gyros)
        {
            _config = config;

            elapsedTimeMs = 0;
            target = controller.WorldMatrix.Backward;
        }

        public void AppendStatus(StringBuilder strb)
        {
            strb.Append("\nCalibrating Turn Time...\n");
            strb.Append("Elapsed Ms: ").Append(elapsedTimeMs.ToString("0\n"));
        }

        public void Run()
        {
            Orient(target);

            elapsedTimeMs += TICK * 1000;

            if (Vector3D.Dot(target, ShipController.WorldMatrix.Forward) > 0.999999)
            {
                _config.Ship180TurnTimeSeconds = Math.Round(elapsedTimeMs / 1000.0, 2, MidpointRounding.AwayFromZero);
                Complete();
            }
        }

        public void Abort() => Terminate("Aborted");
        private void Complete() => Terminate($"Calibration Completed.\nTurn time is {_config.Ship180TurnTimeSeconds} seconds.");

        public void Terminate(string reason)
        {
            ResetGyroOverride();
            CruiseTerminated.Invoke(this, reason);
        }

        protected override void OnNoFunctionalGyrosLeft() => Terminate("No functional gyros found, Calibration terminated.");
    }
}
