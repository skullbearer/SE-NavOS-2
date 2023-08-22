using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript.Navigation
{
    public class RetroCruiseControl : ICruiseController
    {
        public enum RetroCruiseStage : byte
        {
            None = 0,
            OrientAndAccelerate = 1,
            OrientAndDecelerate = 3,
            DecelerateNoOrient = 4,
            Complete = 5,
            Aborted = 6,
        }

        const double TICK = 1.0 / 60.0;
        const double DegToRadMulti = Math.PI / 180;
        const double RadToDegMulti = 180 / Math.PI;

        public event CruiseTerminateEventDelegate CruiseTerminated = delegate { };

        public string Name => nameof(RetroCruiseControl);
        public RetroCruiseStage Stage
        {
            get { return _stage; }
            private set
            {
                if (_stage != value)
                {
                    var old = _stage;
                    _stage = value;
                    OnStageChanged();
                }
            }
        }
        public Vector3D Target { get; }
        public double DesiredSpeed { get; }
        public IAimController AimControl { get; set; }
        public IMyShipController Controller { get; set; }
        public IMyGyro Gyro { get; set; }
        public Dictionary<Direction, List<IMyThrust>> Thrusters { get; set; }
        private IEnumerable<IMyThrust> ForwardThrusters => Thrusters[Direction.Forward];
        private IEnumerable<IMyThrust> BackThrusters => Thrusters[Direction.Backward];

        /// <summary>
        /// what speed end cruise routine during deceleration
        /// </summary>
        public double completionShipSpeed = 0.05;

        /// <summary>
        /// timeToStop + value to start rotating the ship for deceleration
        /// </summary>
        public double decelStartMarginSeconds = 5;

        /// <summary>
        /// aim/orient tolerance in radians
        /// </summary>
        public double orientToleranceAngleRadians = 0.5 * DegToRadMulti;

        public float thrustOverrideMultiplier = 1f;

        //public float reserveThrustRatio = 0.05f;

        //useful for overestimating stop time and dist for better cruise accuracy
        public double stopTimeAndDistanceMulti = 1.05;

        private RetroCruiseStage _stage;
        private double prevDistanceToTarget;
        private int counter = 0;
        //how far off the aim is from the desired orientation
        private double? lastAimDirectionAngleRad = null;

        private float gridMass;
        private float forwardAccelPremultiplied; //premultiplied by thrustOverrideMultiplier

        private float forwardThrust;
        private float backThrust;
        private float rightThrust;
        private float leftThrust;
        private float upThrust;
        private float downThrust;

        const float DAMPENER_TOLERANCE = 0.005f;

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
            float forwardThrust = ForwardThrusters.Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
            forwardAccelPremultiplied = forwardThrust / gridMass * thrustOverrideMultiplier;
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
        }

        private void DampenAllDirections(Vector3D shipVelocity)
        {
            Vector3 localVelocity = Vector3D.TransformNormal(shipVelocity, MatrixD.Transpose(Controller.WorldMatrix));
            Vector3 thrustAmount = localVelocity * 2 * gridMass;
            float backward = thrustAmount.Z < DAMPENER_TOLERANCE ? Math.Min(-thrustAmount.Z, backThrust * thrustOverrideMultiplier) : 0;
            float forward = thrustAmount.Z > DAMPENER_TOLERANCE ? Math.Min(thrustAmount.Z, forwardThrust * thrustOverrideMultiplier) : 0;
            float right = thrustAmount.X < DAMPENER_TOLERANCE ? Math.Min(-thrustAmount.X, rightThrust * thrustOverrideMultiplier) : 0;
            float left = thrustAmount.X > DAMPENER_TOLERANCE ? Math.Min(thrustAmount.X, leftThrust * thrustOverrideMultiplier) : 0;
            float up = thrustAmount.Y < DAMPENER_TOLERANCE ? Math.Min(-thrustAmount.Y, upThrust * thrustOverrideMultiplier) : 0;
            float down = thrustAmount.Y > DAMPENER_TOLERANCE ? Math.Min(thrustAmount.Y, downThrust * thrustOverrideMultiplier) : 0;

            foreach (var thrust in Thrusters[Direction.Forward])
                thrust.ThrustOverride = forward;
            foreach (var thrust in Thrusters[Direction.Backward])
                thrust.ThrustOverride = backward;
            foreach (var thrust in Thrusters[Direction.Right])
                thrust.ThrustOverride = right;
            foreach (var thrust in Thrusters[Direction.Left])
                thrust.ThrustOverride = left;
            foreach (var thrust in Thrusters[Direction.Up])
                thrust.ThrustOverride = up;
            foreach (var thrust in Thrusters[Direction.Down])
                thrust.ThrustOverride = down;
        }

        private void DampenSideways(Vector3D shipVelocity)
        {
            Vector3 localVelocity = Vector3D.TransformNormal(shipVelocity, MatrixD.Transpose(Controller.WorldMatrix));
            Vector3 thrustAmount = localVelocity * 2 * gridMass;
            float right = thrustAmount.X < DAMPENER_TOLERANCE ? Math.Min(-thrustAmount.X, rightThrust * thrustOverrideMultiplier) : 0;
            float left = thrustAmount.X > DAMPENER_TOLERANCE ? Math.Min(thrustAmount.X, leftThrust * thrustOverrideMultiplier) : 0;
            float up = thrustAmount.Y < DAMPENER_TOLERANCE ? Math.Min(-thrustAmount.Y, upThrust * thrustOverrideMultiplier) : 0;
            float down = thrustAmount.Y > DAMPENER_TOLERANCE ? Math.Min(thrustAmount.Y, downThrust * thrustOverrideMultiplier) : 0;

            foreach (var thrust in Thrusters[Direction.Right])
                thrust.ThrustOverride = right;
            foreach (var thrust in Thrusters[Direction.Left])
                thrust.ThrustOverride = left;
            foreach (var thrust in Thrusters[Direction.Up])
                thrust.ThrustOverride = up;
            foreach (var thrust in Thrusters[Direction.Down])
                thrust.ThrustOverride = down;
        }

        private double estimatedTimeOfArrival;
        private double accelTime;
        private double timeToStartDecel;
        private double stopTime;
        private double stopDist;
        private double distanceToTarget;

        public void Run()
        {
            counter++;
            if (counter % 10 == 0)
            {
                gridMass = Controller.CalculateShipMass().PhysicalMass;
                lastAimDirectionAngleRad = null;

                SetDampenerState(false);

                //eta = accelTime + cruiseTime + stopTime
            }
            if (counter % 30 == 0)
            {
                forwardThrust = ForwardThrusters.Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
                backThrust = Thrusters[Direction.Backward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
                rightThrust = Thrusters[Direction.Right].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
                leftThrust = Thrusters[Direction.Left].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
                upThrust = Thrusters[Direction.Up].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
                downThrust = Thrusters[Direction.Down].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);

                forwardAccelPremultiplied = forwardThrust / gridMass * thrustOverrideMultiplier;

                //calculate ACTUAL ETA
            }

            Vector3D myPosition = Controller.GetPosition();
            Vector3D myVelocity = Controller.GetShipVelocities().LinearVelocity;
            double mySpeed = myVelocity.Length();

            Vector3D targetDirection = Target - myPosition;//aka relativePosition
            distanceToTarget = targetDirection.Length();

            //time to stop: currentSpeed / acceleration;
            //stopping distance: timeToStop * (currentSpeed / 2)
            //or also: currentSpeed^2 / (2 * acceleration)
            stopTime = mySpeed / forwardAccelPremultiplied * stopTimeAndDistanceMulti;
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

                //or cancel out current sideways velocity
                //and backwards velocity (moving away from target) if any

                Stage = RetroCruiseStage.OrientAndAccelerate;
            }

            if (Stage == RetroCruiseStage.OrientAndAccelerate)
            {
                if (counter % 10 == 0) //calculate eta
                {
                    accelTime = (currentDesiredSpeedDelta / forwardAccelPremultiplied);
                    double accelDist = accelTime * (currentDesiredSpeedDelta * 0.5);
                    double actualStopTime = DesiredSpeed / forwardAccelPremultiplied * stopTimeAndDistanceMulti;
                    double actualStopDist = actualStopTime * (DesiredSpeed * 0.5);
                    double cruiseDist = distanceToTarget - stopDist - accelDist;
                    double cruiseTime = cruiseDist / DesiredSpeed;
                    estimatedTimeOfArrival = accelTime + cruiseTime + stopTime;
                }

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
                if (counter % 10 == 0) //calculate eta
                {
                    double cruiseDist = distanceToTarget - stopDist;
                    double cruiseTime = cruiseDist / DesiredSpeed;
                    estimatedTimeOfArrival = cruiseTime + stopTime;
                }

                OrientAndDecelerate(myVelocity, targetDirection, timeToStartDecel, mySpeed, distanceToTarget);
            }

            if (Stage == RetroCruiseStage.DecelerateNoOrient)
            {
                if (counter % 10 == 0) //calculate eta
                {
                    double cruiseDist = distanceToTarget - stopDist;
                    double cruiseTime = cruiseDist / DesiredSpeed;
                    estimatedTimeOfArrival = cruiseTime + stopTime;
                }

                DecelerateNoOrient(myVelocity, timeToStartDecel, mySpeed);
            }

            if (Stage == RetroCruiseStage.Complete)
            {
                estimatedTimeOfArrival = 0;
                Complete();
            }

            prevDistanceToTarget = distanceToTarget;
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

        private void ResetThrustOverridesExceptBack()
        {
            foreach (var thruster in Thrusters[Direction.Forward])
                thruster.ThrustOverridePercentage = 0;
            foreach (var thruster in Thrusters[Direction.Right])
                thruster.ThrustOverridePercentage = 0;
            foreach (var thruster in Thrusters[Direction.Left])
                thruster.ThrustOverridePercentage = 0;
            foreach (var thruster in Thrusters[Direction.Up])
                thruster.ThrustOverridePercentage = 0;
            foreach (var thruster in Thrusters[Direction.Down])
                thruster.ThrustOverridePercentage = 0;
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

        private void OnStageChanged()
        {
            ResetThrustOverrides();
            SetDampenerState(false);
        }

        private void OrientAndAccelerate(double timeToStartDecel, Vector3D targetDirection, double mySpeed, Vector3D myVelocity)
        {
            if (timeToStartDecel <= decelStartMarginSeconds || (mySpeed >= DesiredSpeed && distanceToTarget <= prevDistanceToTarget))
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
                foreach (var thruster in ForwardThrusters)
                {
                    thruster.ThrustOverridePercentage = thrustOverrideMultiplier;
                }

                DampenSideways(myVelocity);
                return;
            }

            ResetThrustOverridesExceptBack();
        }

        StringBuilder debug => Program.debug;

        private void OrientAndDecelerate(Vector3D myVelocity, Vector3D targetDirection, double timeToStartDecel, double mySpeed, double targetDist)
        {
            if (mySpeed <= completionShipSpeed)
            {
                Stage = RetroCruiseStage.Complete;
                return;
            }

            if (distanceToTarget > prevDistanceToTarget)
            {
                ResetGyroOverride();
                Stage = RetroCruiseStage.DecelerateNoOrient;
                return;
            }

            //Vector3D orientForward = -targetDirection;
            Vector3D orientForward = -(targetDirection + myVelocity);

            Orient(orientForward);

            if (!lastAimDirectionAngleRad.HasValue)
            {
                lastAimDirectionAngleRad = Vector3D.Angle(Controller.WorldMatrix.Forward, orientForward);
            }

            if (lastAimDirectionAngleRad.Value <= orientToleranceAngleRadians)
            {
                if (targetDist < forwardAccelPremultiplied)
                {
                    ResetGyroOverride();
                    Stage = RetroCruiseStage.DecelerateNoOrient;
                    return;
                }

                float overrideAmount = Math.Min((float)(-timeToStartDecel + thrustOverrideMultiplier), thrustOverrideMultiplier);

                debug.Clear();
                debug.Append("overrideAmount ").AppendLine(overrideAmount.ToString());
                debug.Append("timeToStartDecel ").AppendLine(timeToStartDecel.ToString());

                foreach (var thruster in ForwardThrusters)
                {
                    thruster.ThrustOverridePercentage = overrideAmount;
                }

                DampenSideways(myVelocity);
                //DampenSideways(-targetDirection);
                return;
            }

            if (timeToStartDecel > 0)
            {
                ResetThrustOverridesExceptBack();
                return;
            }

            DampenAllDirections(myVelocity);
        }

        private void DecelerateNoOrient(Vector3D myVelocity, double timeToStartDecel, double mySpeed)
        {
            if (distanceToTarget > prevDistanceToTarget)
            {
                DampenAllDirections(myVelocity);
                return;
            }

            if (mySpeed <= completionShipSpeed)
            {
                Stage = RetroCruiseStage.Complete;
                return;
            }

            float overrideAmount = Math.Min((float)(-timeToStartDecel + thrustOverrideMultiplier), thrustOverrideMultiplier);

            debug.Clear();
            debug.Append("overrideAmount ").AppendLine(overrideAmount.ToString());
            debug.Append("timeToStartDecel ").AppendLine(timeToStartDecel.ToString());

            foreach (var thruster in ForwardThrusters)
            {
                thruster.ThrustOverridePercentage = overrideAmount;
            }

            DampenSideways(myVelocity);
        }

        private void Complete()
        {
            ResetThrustOverrides();
            TurnOnAllThrusters();

            ResetGyroOverride();

            SetDampenerState(true);

            CruiseTerminated.Invoke(this, "Destination Reached");
        }

        public void Abort()
        {
            ResetThrustOverrides();
            TurnOnAllThrusters();

            ResetGyroOverride();

            Stage = RetroCruiseStage.Aborted;

            CruiseTerminated.Invoke(this, "Aborted");
        }
    }
}
