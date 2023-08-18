using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript
{
    public class RetroCruiseControl : ICruiseController
    {
        public enum RetroCruiseStage : byte
        {
            None = 0,
            OrientAccel = 1,
            Accelerate = 2,
            OrientDecel = 3,
            Decelerate = 4,
            Complete = 5,
            Aborted = 6,
        }

        const double TICK = 1.0 / 60.0;

        public event Action CruiseCompleted = delegate { };

        public RetroCruiseStage Stage
        {
            get { return _stage; }
            private set
            {
                if (_stage != value)
                {
                    var old = _stage;
                    _stage = value;
                    OnStageChanged(old, value);
                }
            }
        }
        public Vector3D Target { get; }
        public double DesiredSpeed { get; }
        public IAimController AimControl { get; set; }
        public IMyShipController Controller { get; set; }
        public IMyGyro Gyro { get; set; }
        public Dictionary<Direction, List<IMyThrust>> Thrusters { get; set; }
        private IEnumerable<IMyThrust> ForeThrusters => Thrusters[Direction.Forward];
        private IEnumerable<IMyThrust> BackThrusters => Thrusters[Direction.Backward];

        /// <summary>
        /// what speed end cruise routine during deceleration
        /// </summary>
        public double completionShipSpeed = 5;

        /// <summary>
        /// timeToStop + value to start rotating the ship for deceleration
        /// </summary>
        public double decelStartMarginSeconds = 5;

        /// <summary>
        /// aim/orient tolerance in radians
        /// </summary>
        public double orientToleranceAngleRadians = 0.5 * (Math.PI / 180);

        /// <summary>
        /// reduces thrust override to down to 0 at this much time over timeToStartDecel, 1 at timeToStartDecel == 0
        /// </summary>
        public double decelDynamicThrustMaxSeconds = 0.1;

        private RetroCruiseStage _stage;
        private double prevDistanceToTarget;
        private int counter = 0;
        //how far off the aim is from the desired orientation
        private double? lastAimDirectionAngleRad = null;

        private float gridMass;
        private float forwardThrust;
        private float forwardAccel;

        public RetroCruiseControl(
            Vector3D target,
            double desiredSpeed,
            IAimController aimControl,
            IMyShipController controller,
            IMyGyro gyro,
            Dictionary<Direction, List<IMyThrust>> thrusters)
        {
            this.Target = target;
            this.DesiredSpeed = desiredSpeed;
            this.AimControl = aimControl;
            this.Controller = controller;
            this.Gyro = gyro;
            this.Thrusters = thrusters;

            Stage = RetroCruiseStage.None;
            gridMass = controller.CalculateShipMass().PhysicalMass;
            forwardThrust = ForeThrusters.Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
            forwardAccel = forwardThrust / gridMass;
        }

        public void AppendStatus(StringBuilder strb)
        {
            strb.Append("-- RetroCruiseControl Status --\n");
            strb.Append("Stage: ").Append(Stage.ToString());
            strb.Append("DistanceToTarget ").Append(distanceToTarget.ToString("0"));
            strb.Append("StopDistance ").Append(stopDist.ToString("0"));
            strb.Append("TimeToStartDecel ").Append(timeToStartDecel.ToString("0.00"));
        }

        private double distanceToTarget;
        private double stopDist;
        private double timeToStartDecel;
        private double estimatedTimeToArrival;

        public void Run()
        {
            counter++;
            if (counter % 10 == 0)
            {
                gridMass = Controller.CalculateShipMass().PhysicalMass;
                forwardThrust = ForeThrusters.Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
                forwardAccel = forwardThrust / gridMass;
                lastAimDirectionAngleRad = null;
            }

            Vector3D myPosition = Controller.GetPosition();
            Vector3D myVelocity = Controller.GetShipVelocities().LinearVelocity;
            double mySpeed = myVelocity.Length();

            Vector3D targetDirection = Target - myPosition;//aka relativePosition
            distanceToTarget = targetDirection.Length();

            //time to stop: currentSpeed / acceleration;
            //stopping distance: timeToStop * (currentSpeed / 2)
            //or also: currentSpeed^2 / (2 * acceleration)
            //double stopTime = mySpeed / forwardAccel;
            //double stopDist = stopTime * (mySpeed * 0.5);
            stopDist = (mySpeed * mySpeed) / (2 * forwardAccel);

            timeToStartDecel = ((distanceToTarget - stopDist) / mySpeed) + (TICK * 2);
            //double distToStartDecel = distanceToTarget - stopDist;

            //double currentDesiredSpeedDelta = Math.Abs(DesiredSpeed - mySpeed);

            if (Stage == RetroCruiseStage.None)
            {
                ResetThrustOverrides();

                prevDistanceToTarget = distanceToTarget + 1;

                //todo: make the ship stop using retroDecel

                Stage = RetroCruiseStage.OrientAccel;

                foreach (var thruster in BackThrusters)
                {
                    thruster.Enabled = false;
                }
            }

            if (Stage == RetroCruiseStage.OrientAccel)
            {
                OrientAccel(timeToStartDecel, targetDirection);
            }

            if (Stage == RetroCruiseStage.Accelerate)
            {
                Accelerate(timeToStartDecel, targetDirection, mySpeed);
            }

            if (Stage == RetroCruiseStage.OrientDecel)
            {
                if (distanceToTarget > prevDistanceToTarget)
                {
                    Abort();
                    return;
                }

                double timeToStartDecelPadded = timeToStartDecel - 0.1;

                OrientDecel(myVelocity, targetDirection, timeToStartDecelPadded);
            }

            if (Stage == RetroCruiseStage.Decelerate)
            {
                if (distanceToTarget > prevDistanceToTarget)
                {
                    Abort();
                    return;
                }

                double timeToStartDecelPadded = timeToStartDecel - 0.1;

                Decelerate(myVelocity, targetDirection, timeToStartDecelPadded, mySpeed);
            }

            if (Stage == RetroCruiseStage.Complete)
            {
                Complete();
            }

            prevDistanceToTarget = distanceToTarget;

            //Program.debug.Clear();
            //Program.debug.AppendLine($"{_stage} to {Stage}");
            //Program.debug.AppendLine($"timeToStartDecel {timeToStartDecel}");
            //Program.debug.AppendLine($"timeToStartDecelPadded {timeToStartDecelPadded}");
            //Program.debug.AppendLine($"distToStartDecel {distToStartDecel}");
            //Program.debug.AppendLine($"currentDesiredSpeedDelta {currentDesiredSpeedDelta}");
            //Program.debug.AppendLine($"forwardAccel {forwardAccel}");
            //Program.debug.AppendLine($"stopDist {stopDist}");
        }

        private void ResetThrustOverrides()
        {
            foreach (var list in Thrusters.Values)
            {
                foreach (var thruster in list)
                {
                    thruster.ThrustOverridePercentage = 0;
                }
            }
        }

        private void TurnOnAllThrusters()
        {
            foreach (var list in Thrusters.Values)
            {
                foreach (var thruster in list)
                {
                    thruster.Enabled = true;
                }
            }
        }

        private void ResetGyroOverride()
        {
            Gyro.Pitch = 0;
            Gyro.Yaw = 0;
            Gyro.Roll = 0;
            Gyro.GyroOverride = false;
        }

        private void Orient(Vector3D forward)
        {
            if (!Gyro.Enabled)
            {
                Gyro.Enabled = true;
            }

            AimControl.Orient(forward, Gyro, Controller.WorldMatrix);
        }

        private void SetDampenerState(bool enabled)
        {
            if (Controller.DampenersOverride != enabled)
            {
                Controller.DampenersOverride = enabled;
            }
        }

        private void OnStageChanged(RetroCruiseStage old, RetroCruiseStage now)
        {
            ResetThrustOverrides();
        }

        private void OrientAccel(double timeToStartDecel, Vector3D targetDirection)
        {
            if (timeToStartDecel <= decelStartMarginSeconds)
            {
                Stage = RetroCruiseStage.OrientDecel;
                return;
            }

            Orient(targetDirection);

            SetDampenerState(false);

            if (!lastAimDirectionAngleRad.HasValue)
            {
                lastAimDirectionAngleRad = Vector3D.Angle(Controller.WorldMatrix.Forward, targetDirection);
            }

            if (lastAimDirectionAngleRad.Value <= orientToleranceAngleRadians)
            {
                Stage = RetroCruiseStage.Accelerate;
                return;
            }
        }

        private void Accelerate(double timeToStartDecel, Vector3D targetDirection, double mySpeed)
        {
            if (timeToStartDecel <= decelStartMarginSeconds || mySpeed >= DesiredSpeed)
            {
                Stage = RetroCruiseStage.OrientDecel;
                lastAimDirectionAngleRad = null;
                return;
            }

            Orient(targetDirection);

            SetDampenerState(true);

            if (!lastAimDirectionAngleRad.HasValue)
            {
                lastAimDirectionAngleRad = Vector3D.Angle(Controller.WorldMatrix.Forward, targetDirection);
            }

            if (lastAimDirectionAngleRad.Value <= orientToleranceAngleRadians)
            {
                foreach (var thruster in ForeThrusters)
                {
                    thruster.ThrustOverridePercentage = 1;
                }
            }
            else
            {
                Stage = RetroCruiseStage.OrientAccel;
                return;
            }
        }

        private void OrientDecel(Vector3D myVelocity, Vector3D targetDirection, double timeToStartDecel)
        {
            Vector3D orientForward = -(myVelocity + targetDirection) * 0.5;

            if (timeToStartDecel <= 0)
            {
                Stage = RetroCruiseStage.Decelerate;
                return;
            }

            Orient(orientForward);

            foreach (var thruster in ForeThrusters)
            {
                thruster.ThrustOverride = 0.01f;
            }

            if (!lastAimDirectionAngleRad.HasValue)
            {
                lastAimDirectionAngleRad = Vector3D.Angle(Controller.WorldMatrix.Forward, orientForward);
            }

            if (lastAimDirectionAngleRad.Value <= orientToleranceAngleRadians)
            {

                SetDampenerState(true);
                return;
            }

            SetDampenerState(false);
        }

        private void Decelerate(Vector3D myVelocity, Vector3D targetDirection, double timeToStartDecel, double mySpeed)
        {
            Vector3D orientForward = -(myVelocity + targetDirection) * 0.5;

            if (mySpeed <= completionShipSpeed)
            {
                Stage = RetroCruiseStage.Complete;
                return;
            }

            Orient(orientForward);

            SetDampenerState(true);

            if (!lastAimDirectionAngleRad.HasValue)
            {
                lastAimDirectionAngleRad = Vector3D.Angle(Controller.WorldMatrix.Forward, orientForward);
            }

            if (lastAimDirectionAngleRad.Value <= orientToleranceAngleRadians && timeToStartDecel > 0)
            {
                float overrideAmount = MathHelper.Clamp((float)((-timeToStartDecel + decelDynamicThrustMaxSeconds) / decelDynamicThrustMaxSeconds), 0.001f, 1f);
                foreach (var thruster in ForeThrusters)
                {
                    thruster.ThrustOverridePercentage = overrideAmount;
                }

                return;
            }

            foreach (var thruster in ForeThrusters)
            {
                thruster.ThrustOverridePercentage = 0f;
            }
        }

        private void Complete()
        {
            ResetThrustOverrides();
            TurnOnAllThrusters();

            ResetGyroOverride();

            SetDampenerState(true);

            CruiseCompleted.Invoke();
        }

        public void Abort()
        {
            ResetThrustOverrides();
            TurnOnAllThrusters();

            ResetGyroOverride();

            Stage = RetroCruiseStage.Aborted;

            CruiseCompleted.Invoke();

            //SetDampenerState(true);
        }
    }
}
