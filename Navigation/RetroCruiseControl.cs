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
            OrientAndAccelerate = 1,
            OrientAndDecelerate = 3,
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
        public double completionShipSpeed = 0.1;

        /// <summary>
        /// timeToStop + value to start rotating the ship for deceleration
        /// </summary>
        public double decelStartMarginSeconds = 5;

        /// <summary>
        /// aim/orient tolerance in radians
        /// </summary>
        public double orientToleranceAngleRadians = 0.5 * DegToRadMulti;

        public float thrustOverrideMultiplier = 1f;

        /// <summary>
        /// look at the code referencing this field
        /// </summary>
        public double speedDiv = 200;

        private RetroCruiseStage _stage;
        private double prevDistanceToTarget;
        private int counter = 0;
        //how far off the aim is from the desired orientation
        private double? lastAimDirectionAngleRad = null;

        private float gridMass;
        private float forwardAccelPremultiplied; //premultiplied by thrustOverrideMultiplier

        private float forwardAccel;
        private float backAccel;
        private float rightAccel;
        private float leftAccel;
        private float upAccel;
        private float downAccel;

        //const float DAMPENER_TOLERANCE = 0.1f;

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

        private void DampenAllDirections(Vector3D shipVelocity, double shipSpeed)
        {
            float DAMPENER_TOLERANCE = (float)(shipSpeed * 0.0001);
            const float thrustDiv = 5;
            Vector3 localVelocity = Vector3D.TransformNormal(shipVelocity, MatrixD.Transpose(Controller.WorldMatrix));
            float backward = localVelocity.Z < DAMPENER_TOLERANCE ? Math.Min(1, -localVelocity.Z / backAccel / thrustDiv) * thrustOverrideMultiplier : 0;
            float forward = localVelocity.Z > DAMPENER_TOLERANCE ? Math.Min(1, localVelocity.Z / forwardAccel / thrustDiv) * thrustOverrideMultiplier : 0;
            float right = localVelocity.X < DAMPENER_TOLERANCE ? Math.Min(1, -localVelocity.X / rightAccel / thrustDiv) * thrustOverrideMultiplier : 0;
            float left = localVelocity.X > DAMPENER_TOLERANCE ? Math.Min(1, localVelocity.X / leftAccel / thrustDiv) * thrustOverrideMultiplier : 0;
            float up = localVelocity.Y < DAMPENER_TOLERANCE ? Math.Min(1, -localVelocity.Y / upAccel / thrustDiv) * thrustOverrideMultiplier : 0;
            float down = localVelocity.Y > DAMPENER_TOLERANCE ? Math.Min(1, localVelocity.Y / downAccel / thrustDiv) * thrustOverrideMultiplier : 0;

            foreach (var thrust in Thrusters[Direction.Forward])
                thrust.ThrustOverridePercentage = forward;

            foreach (var thrust in Thrusters[Direction.Backward])
                thrust.ThrustOverridePercentage = backward;

            foreach (var thrust in Thrusters[Direction.Right])
                thrust.ThrustOverridePercentage = right;

            foreach (var thrust in Thrusters[Direction.Left])
                thrust.ThrustOverridePercentage = left;

            foreach (var thrust in Thrusters[Direction.Up])
                thrust.ThrustOverridePercentage = up;

            foreach (var thrust in Thrusters[Direction.Down])
                thrust.ThrustOverridePercentage = down;
        }

        private void DampenSideways(Vector3D shipVelocity, double shipSpeed)
        {
            float DAMPENER_TOLERANCE = (float)(shipSpeed * 0.0001);
            const float thrustDiv = 5;
            Vector3 localVelocity = Vector3D.TransformNormal(shipVelocity, MatrixD.Transpose(Controller.WorldMatrix));
            float right = localVelocity.X < DAMPENER_TOLERANCE ? Math.Min(1, -localVelocity.X / rightAccel / thrustDiv) * thrustOverrideMultiplier : 0;
            float left = localVelocity.X > DAMPENER_TOLERANCE ? Math.Min(1, localVelocity.X / leftAccel / thrustDiv) * thrustOverrideMultiplier : 0;
            float up = localVelocity.Y < DAMPENER_TOLERANCE ? Math.Min(1, -localVelocity.Y / upAccel / thrustDiv) * thrustOverrideMultiplier : 0;
            float down = localVelocity.Y > DAMPENER_TOLERANCE ? Math.Min(1, localVelocity.Y / downAccel / thrustDiv) * thrustOverrideMultiplier : 0;

            foreach (var thrust in Thrusters[Direction.Right])
                thrust.ThrustOverridePercentage = right;

            foreach (var thrust in Thrusters[Direction.Left])
                thrust.ThrustOverridePercentage = left;

            foreach (var thrust in Thrusters[Direction.Up])
                thrust.ThrustOverridePercentage = up;

            foreach (var thrust in Thrusters[Direction.Down])
                thrust.ThrustOverridePercentage = down;
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
                lastAimDirectionAngleRad = null;

                SetDampenerState(false);

                //eta = accelTime + cruiseTime + stopTime
            }
            if (counter % 30 == 0)
            {
                float forwardThrust = ForwardThrusters.Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
                forwardAccelPremultiplied = forwardThrust / gridMass * thrustOverrideMultiplier;

                forwardAccel = Thrusters[Direction.Forward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust) / gridMass;
                backAccel = Thrusters[Direction.Backward].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust) / gridMass;
                rightAccel = Thrusters[Direction.Right].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust) / gridMass;
                leftAccel = Thrusters[Direction.Left].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust) / gridMass;
                upAccel = Thrusters[Direction.Up].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust) / gridMass;
                downAccel = Thrusters[Direction.Down].Where(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust) / gridMass;
            }

            Vector3D myPosition = Controller.GetPosition();
            Vector3D myVelocity = Controller.GetShipVelocities().LinearVelocity;
            double mySpeed = myVelocity.Length();

            Vector3D targetDirection = Target - myPosition;//aka relativePosition
            distanceToTarget = targetDirection.Length();

            //time to stop: currentSpeed / acceleration;
            //stopping distance: timeToStop * (currentSpeed / 2)
            //or also: currentSpeed^2 / (2 * acceleration)
            double stopTime = mySpeed / forwardAccelPremultiplied;
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
                accelTime = (currentDesiredSpeedDelta / forwardAccelPremultiplied);
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
                double cruiseDist = distanceToTarget - stopDist;
                double cruiseTime = cruiseDist / DesiredSpeed;
                estimatedTimeOfArrival = cruiseTime + stopTime;

                OrientAndDecelerate(myVelocity, targetDirection, timeToStartDecel, mySpeed);
            }

            if (Stage == RetroCruiseStage.Complete)
            {
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

                DampenSideways(myVelocity, mySpeed);
                return;
            }

            ResetThrustOverridesExceptBack();
        }

        StringBuilder debug => Program.debug;

        private void OrientAndDecelerate(Vector3D myVelocity, Vector3D targetDirection, double timeToStartDecel, double mySpeed)
        {
            double thrustDiv = DesiredSpeed / speedDiv;

            if (mySpeed <= completionShipSpeed)
            {
                Stage = RetroCruiseStage.Complete;
                return;
            }

            if (distanceToTarget > prevDistanceToTarget)
            {
                ResetGyroOverride();
                DampenAllDirections(myVelocity, mySpeed);
                return;
            }

            Vector3D orientForward = -myVelocity;

            Orient(orientForward);

            if (!lastAimDirectionAngleRad.HasValue)
            {
                lastAimDirectionAngleRad = Vector3D.Angle(Controller.WorldMatrix.Forward, orientForward);
            }

            bool oriented = lastAimDirectionAngleRad.Value <= orientToleranceAngleRadians;

            debug.Clear();

            if (oriented)
            {
                if (distanceToTarget > prevDistanceToTarget)
                {
                    return;
                }

                float overrideAmount = MathHelper.Min((float)((-timeToStartDecel + thrustDiv) / thrustDiv), 1f);

                overrideAmount *= thrustOverrideMultiplier;

                debug.Append("overrideAmount ").AppendLine(overrideAmount.ToString());
                debug.Append("timeToStartDecel ").AppendLine(timeToStartDecel.ToString());
                debug.Append("thrustFactor ").AppendLine(thrustDiv.ToString());

                foreach (var thruster in ForwardThrusters)
                {
                    thruster.ThrustOverridePercentage = overrideAmount;
                }

                DampenSideways(-targetDirection, mySpeed);
                return;
            }

            if (timeToStartDecel > 0)
            {
                ResetThrustOverridesExceptBack();
                return;
            }

            DampenAllDirections(myVelocity, mySpeed);
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
