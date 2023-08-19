using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript
{
    public class RetroCruiseControlScriptDampener : ICruiseController
    {
        public enum RetroCruiseStage : byte
        {
            None = 0,
            OrientAndAccelerate = 1,
            OrientAndDecelerate = 3,
            Complete = 5,
            Aborted = 6,
        }

        const double TICK = 1.0 / 60.0;
        const double DegToRadMulti = Math.PI / 180;
        const double RadToDegMulti = 180 / Math.PI;

        public event Action CruiseCompleted = delegate { };

        public string Name => "RetroCruiseScriptDampener";
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
        public double orientToleranceAngleRadians = 0.5 * DegToRadMulti;

        /// <summary>
        /// reduces thrust override to down to 0 at this much time over timeToStartDecel, 1 at timeToStartDecel == 0
        /// </summary>
        public double decelDynamicThrustMaxSeconds = 0.1;

        public float thrustOverrideMultiplier = 1f;

        private RetroCruiseStage _stage;
        private double prevDistanceToTarget;
        private int counter = 0;
        //how far off the aim is from the desired orientation
        private double? lastAimDirectionAngleRad = null;

        private float gridMass;
        private float forwardAccel;

        public RetroCruiseControlScriptDampener(
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
            float forwardThrust = ForeThrusters.Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
            forwardAccel = forwardThrust / gridMass;
        }

        public void AppendStatus(StringBuilder strb)
        {
            strb.Append("\n-- RetroCruiseControl Status --");
            strb.Append("\nStage: ").Append(Stage.ToString());
            strb.Append("\nETA: ").Append(estimatedTimeOfArrival.ToString("0.0"));
            strb.Append("\nRemainingAccelTime: ").Append(accelTime.ToString("0.000"));
            strb.Append("\nTimeToStartDecel: ").Append(timeToStartDecel.ToString("0.000"));
            strb.Append("\nTargetDistance: ").Append(distanceToTarget.ToString("0.0"));
            strb.Append("\nStoppingDistance: ").Append(stopDist.ToString("0.0"));
            if (lastAimDirectionAngleRad.HasValue)
                strb.Append("\nAimDirectionAngle: ").Append((lastAimDirectionAngleRad.Value * RadToDegMulti).ToString("0.0"));
            else
                strb.Append("\nAimDirectionAngle: null");
            strb.AppendLine();

            //Vector3D localVelocity = Vector3D.TransformNormal(Controller.GetShipVelocities().LinearVelocity, MatrixD.Transpose(Controller.WorldMatrix));
            //if (localVelocity)
        }

        /// <summary>
        /// Reduce ship velocities in 
        /// </summary>
        private void DampenSideways(Vector3D shipVelocity)
        {
            Vector3 localVelocity = Vector3D.TransformNormal(shipVelocity, MatrixD.Transpose(Controller.WorldMatrix));
            //double forward = localVelocity.Z < 0 ? thrustOverrideMultiplier : 0;
            //double backward = localVelocity.Z > 0 ? thrustOverrideMultiplier : 0;
            float right = localVelocity.X < 0 ? Math.Min(1, -localVelocity.X / 5) * thrustOverrideMultiplier : 0;
            float left = localVelocity.X > 0 ? Math.Min(1, localVelocity.X / 5) * thrustOverrideMultiplier : 0;
            float up = localVelocity.Y < 0 ? Math.Min(1, -localVelocity.Y / 5) * thrustOverrideMultiplier : 0;
            float down = localVelocity.Y > 0 ? Math.Min(1, localVelocity.Y / 5) * thrustOverrideMultiplier : 0;

            foreach (var thrust in Thrusters[Direction.Right])
            {
                thrust.ThrustOverridePercentage = right;
            }

            foreach (var thrust in Thrusters[Direction.Left])
            {
                thrust.ThrustOverridePercentage = left;
            }

            foreach (var thrust in Thrusters[Direction.Up])
            {
                thrust.ThrustOverridePercentage = up;
            }

            foreach (var thrust in Thrusters[Direction.Down])
            {
                thrust.ThrustOverridePercentage = down;
            }
        }

        private double estimatedTimeOfArrival;
        private double accelTime;
        private double timeToStartDecel;
        private double stopDist;
        private double distanceToTarget;

        public void Run()
        {
            counter++;
            if (counter % 10 == 0)
            {
                gridMass = Controller.CalculateShipMass().PhysicalMass;
                float forwardThrust = ForeThrusters.Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
                forwardAccel = forwardThrust / gridMass * thrustOverrideMultiplier;
                lastAimDirectionAngleRad = null;

                //eta = accelTime + cruiseTime + stopTime
            }

            Vector3D myPosition = Controller.GetPosition();
            Vector3D myVelocity = Controller.GetShipVelocities().LinearVelocity;
            double mySpeed = myVelocity.Length();

            Vector3D targetDirection = Target - myPosition;//aka relativePosition
            distanceToTarget = targetDirection.Length();

            //time to stop: currentSpeed / acceleration;
            //stopping distance: timeToStop * (currentSpeed / 2)
            //or also: currentSpeed^2 / (2 * acceleration)
            double stopTime = mySpeed / forwardAccel;
            stopDist = stopTime * (mySpeed * 0.5);
            //stopDist = (mySpeed * mySpeed) / (2 * forwardAccel);

            timeToStartDecel = ((distanceToTarget - stopDist) / mySpeed) + (TICK * 2);
            //double distToStartDecel = distanceToTarget - stopDist;

            double currentDesiredSpeedDelta = Math.Abs(DesiredSpeed - mySpeed);

            if (Stage == RetroCruiseStage.None)
            {
                ResetThrustOverrides();

                prevDistanceToTarget = distanceToTarget + 1;

                SetDampenerState(false);

                //todo: make the ship stop using retroDecel

                Stage = RetroCruiseStage.OrientAndAccelerate;

                foreach (var thruster in BackThrusters)
                {
                    thruster.Enabled = false;
                }
            }

            if (Stage == RetroCruiseStage.OrientAndAccelerate)
            {
                accelTime = (currentDesiredSpeedDelta / forwardAccel);
                double accelDist = accelTime * (currentDesiredSpeedDelta * 0.5);
                double cruiseDist = distanceToTarget - stopDist - accelDist;
                double cruiseTime = cruiseDist / DesiredSpeed;
                estimatedTimeOfArrival = accelTime + cruiseTime + stopTime;

                //if (cruiseDist >= decelStartMarginSeconds)//there's enough time to accel to desired speed and turn retrograde
                //{
                //    double cruiseTime = cruiseDist * DesiredSpeed;
                //    estimatedTimeOfArrival = accelTime + cruiseTime + stopTime;
                //}
                //else//accel will stop midway
                //{
                //    //TODO: calculate ETA when ship cant get to desired speed
                //    double realAccelDist = accelDist - (cruiseDist * 0.5);
                //    double realStopDist = stopDist - (cruiseDist * 0.5);
                //
                //
                //    double realAccelTime = (realAccelDist / forwardAccel) * (realAccelDist * 0.5);
                //    double realStopTime = (realStopDist / forwardAccel) * (realStopDist * 0.5);
                //}

                OrientAndAccelerate(timeToStartDecel, targetDirection, mySpeed, myVelocity);
            }

            if (Stage == RetroCruiseStage.OrientAndDecelerate)
            {
                if (distanceToTarget > prevDistanceToTarget)
                {
                    Abort();
                    return;
                }

                double cruiseDist = distanceToTarget - stopDist;
                double cruiseTime = cruiseDist / DesiredSpeed;
                estimatedTimeOfArrival = cruiseTime + stopTime;

                double timeToStartDecelPadded = timeToStartDecel - 0.1;

                OrientAndDecelerate(myVelocity, targetDirection, timeToStartDecelPadded, mySpeed);
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
            SetDampenerState(false);
        }

        private void OrientAndAccelerate(double timeToStartDecel, Vector3D targetDirection, double mySpeed, Vector3D myVelocity)
        {
            if (timeToStartDecel <= decelStartMarginSeconds || mySpeed >= DesiredSpeed)
            {
                Stage = RetroCruiseStage.OrientAndDecelerate;
                lastAimDirectionAngleRad = null;
                return;
            }

            Orient(targetDirection);

            if (!lastAimDirectionAngleRad.HasValue)
            {
                lastAimDirectionAngleRad = Vector3D.Angle(Controller.WorldMatrix.Forward, targetDirection);
            }

            if (lastAimDirectionAngleRad.Value <= orientToleranceAngleRadians)
            {
                if (thrustOverrideMultiplier == 1f)
                {
                    foreach (var thruster in ForeThrusters)
                    {
                        thruster.ThrustOverridePercentage = 1;
                    }

                    DampenSideways(myVelocity);
                    return;
                }

                foreach (var thruster in ForeThrusters)
                {
                    thruster.ThrustOverridePercentage = thrustOverrideMultiplier;
                }
            }
        }

        private void OrientAndDecelerate(Vector3D myVelocity, Vector3D targetDirection, double timeToStartDecel, double mySpeed)
        {
            Vector3D orientForward = -(myVelocity + targetDirection) * 0.5;

            if (mySpeed <= completionShipSpeed)
            {
                Stage = RetroCruiseStage.Complete;
                return;
            }

            Orient(orientForward);

            if (!lastAimDirectionAngleRad.HasValue)
            {
                lastAimDirectionAngleRad = Vector3D.Angle(Controller.WorldMatrix.Forward, orientForward);
            }

            if (timeToStartDecel > 0)
            {
                if (lastAimDirectionAngleRad.Value <= orientToleranceAngleRadians)
                {
                    float overrideAmount = MathHelper.Clamp((float)((-timeToStartDecel + decelDynamicThrustMaxSeconds) / decelDynamicThrustMaxSeconds), 0.001f, 1f);
                    overrideAmount *= thrustOverrideMultiplier;

                    foreach (var thruster in ForeThrusters)
                    {
                        thruster.ThrustOverridePercentage = overrideAmount;
                    }

                    DampenSideways(myVelocity);
                    return;
                }

                foreach (var thruster in ForeThrusters)
                {
                    thruster.ThrustOverride = 0.0001f;
                }

                return;
            }

            if (lastAimDirectionAngleRad.Value > orientToleranceAngleRadians)
            {
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
