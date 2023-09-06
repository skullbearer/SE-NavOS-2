using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage;
using VRageMath;

namespace IngameScript.Navigation
{
    public class RetroCruiseControl : OrientControllerBase, ICruiseController
    {
        public enum RetroCruiseStage : byte
        {
            None = 0,
            CancelPerpendicularVelocity = 1,
            OrientAndAccelerate = 2,
            OrientAndDecelerate = 3,
            DecelerateNoOrient = 4,
            FinalDecelAndStop = 5,
            Complete = 6,
            Aborted = 7,
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
                    Program.Log($"{old} to {value}");
                }
            }
        }
        public Vector3D Target { get; }
        public double DesiredSpeed { get; }

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

        public float maxThrustOverrideRatio = 1f;
        public double maxInitialPerpendicularVelocity = 5;

        //public float reserveThrustRatio = 0.05f;

        //useful for overestimating stop time and dist for better cruise accuracy
        public double stopTimeAndDistanceMulti = 1.05;

        //how far off the aim is from the desired orientation
        private Dictionary<Direction, MyTuple<IMyThrust, float>[]> thrusters;

        const float DAMPENER_TOLERANCE = 0.005f;

        private bool counter5 = false;
        private bool counter10 = false;
        private bool counter30 = false;
        private bool counter60 = false;

        //active variables
        private RetroCruiseStage _stage;
        private int counter = -1;

        //updated every 30 ticks
        private float gridMass;
        private float forwardAccel;
        private float forwardAccelPremultiplied; //premultiplied by maxThrustOverrideRatio
        private float forwardThrust;

        //updated every 10 ticks
        private double? lastAimDirectionAngleRad = null;
        private double estimatedTimeOfArrival;

        //updated every tick
        private double accelTime;
        private double timeToStartDecel;
        private double stopTime;
        private double stopDist;
        private double distanceToTarget;
        private Vector3D myVelocity;
        private double mySpeed;
        private Vector3D targetDirection;
        private double currentAndDesiredSpeedDelta;

        public RetroCruiseControl(
            Vector3D target,
            double desiredSpeed,
            IAimController aimControl,
            IMyShipController controller,
            IList<IMyGyro> gyros,
            Dictionary<Direction, List<IMyThrust>> thrusters)
            : base(aimControl, controller, gyros)
        {
            this.Target = target;
            this.DesiredSpeed = desiredSpeed;
            this.thrusters = thrusters.ToDictionary(
                kv => kv.Key,
                kv => thrusters[kv.Key]
                    .Select(thrust => new MyTuple<IMyThrust, float>(thrust, thrust.MaxEffectiveThrust * maxThrustOverrideRatio))
                    .ToArray());

            Stage = RetroCruiseStage.None;
            gridMass = controller.CalculateShipMass().PhysicalMass;
            forwardThrust = this.thrusters[Direction.Forward].Where(t => t.Item1.IsWorking).Sum(t => t.Item1.MaxEffectiveThrust);
            forwardAccel = forwardThrust / gridMass;
            forwardAccelPremultiplied = forwardAccel * maxThrustOverrideRatio;
        }

        public void AppendStatus(StringBuilder strb)
        {
            strb.Append("\n-- RetroCruiseControl Status --");
            strb.Append("\nDesired Speed: ").Append(DesiredSpeed.ToString("0.##"));
            strb.Append("\nTargetDistance: ").Append(distanceToTarget.ToString("0.0"));
            strb.Append("\nStage: ").Append(Stage.ToString());
            strb.Append("\nETA: ").Append(estimatedTimeOfArrival.ToString("0.0"));
            strb.Append("\nRemainingAccelTime: ").Append(accelTime.ToString("0.000"));
            strb.Append("\nTimeToStartDecel: ").Append(timeToStartDecel.ToString("0.000"));
            strb.Append("\nStoppingDistance: ").Append(stopDist.ToString("0.0"));
            if (lastAimDirectionAngleRad.HasValue)
                strb.Append("\nAimDirectionAngle: ").Append((lastAimDirectionAngleRad.Value * RadToDegMulti).ToString("0.0"));
            else
                strb.Append("\nAimDirectionAngle: null");
            strb.AppendLine();
        }

        private void DampenAllDirections(Vector3D shipVelocity)
        {
            Vector3 localVelocity = Vector3D.TransformNormal(shipVelocity, MatrixD.Transpose(ShipController.WorldMatrix));
            Vector3 thrustAmount = localVelocity * gridMass;
            float backward = thrustAmount.Z < DAMPENER_TOLERANCE ? -thrustAmount.Z : 0;
            float forward = thrustAmount.Z > DAMPENER_TOLERANCE ? thrustAmount.Z : 0;
            float right = thrustAmount.X < DAMPENER_TOLERANCE ? -thrustAmount.X : 0;
            float left = thrustAmount.X > DAMPENER_TOLERANCE ? thrustAmount.X : 0;
            float up = thrustAmount.Y < DAMPENER_TOLERANCE ? -thrustAmount.Y : 0;
            float down = thrustAmount.Y > DAMPENER_TOLERANCE ? thrustAmount.Y : 0;

            foreach (var thrust in thrusters[Direction.Forward])
                thrust.Item1.ThrustOverride = Math.Min(forward, thrust.Item2);
            foreach (var thrust in thrusters[Direction.Backward])
                thrust.Item1.ThrustOverride = backward;
            foreach (var thrust in thrusters[Direction.Right])
                thrust.Item1.ThrustOverride = right;
            foreach (var thrust in thrusters[Direction.Left])
                thrust.Item1.ThrustOverride = left;
            foreach (var thrust in thrusters[Direction.Up])
                thrust.Item1.ThrustOverride = up;
            foreach (var thrust in thrusters[Direction.Down])
                thrust.Item1.ThrustOverride = down;
        }

        private void UpdateThrust()
        {
            foreach (var kv in thrusters)
            {
                for (int i = 0; i < kv.Value.Length; i++)
                {
                    var val = kv.Value[i];
                    val.Item2 = val.Item1.MaxEffectiveThrust * maxThrustOverrideRatio;
                    kv.Value[i] = val;
                }
            }
        }

        private void DampenSideways(Vector3D shipVelocity)
        {
            Vector3 localVelocity = Vector3D.TransformNormal(shipVelocity, MatrixD.Transpose(ShipController.WorldMatrix));
            Vector3 thrustAmount = localVelocity * gridMass;
            float right = thrustAmount.X < DAMPENER_TOLERANCE ? -thrustAmount.X : 0;
            float left = thrustAmount.X > DAMPENER_TOLERANCE ? thrustAmount.X : 0;
            float up = thrustAmount.Y < DAMPENER_TOLERANCE ? -thrustAmount.Y : 0;
            float down = thrustAmount.Y > DAMPENER_TOLERANCE ? thrustAmount.Y : 0;

            foreach (var thrust in thrusters[Direction.Right])
                thrust.Item1.ThrustOverride = right;
            foreach (var thrust in thrusters[Direction.Left])
                thrust.Item1.ThrustOverride = left;
            foreach (var thrust in thrusters[Direction.Up])
                thrust.Item1.ThrustOverride = up;
            foreach (var thrust in thrusters[Direction.Down])
                thrust.Item1.ThrustOverride = down;
        }

        public void Run()
        {
            counter++;
            counter5 = counter % 5 == 0;
            counter10 = counter % 10 == 0;
            counter30 = counter % 30 == 0;
            counter60 = counter % 60 == 0;
            if (counter10)
            {
                lastAimDirectionAngleRad = null;

                SetDampenerState(false);
            }
            if (counter30)
            {
                forwardThrust = thrusters[Direction.Forward].Where(t => t.Item1.IsWorking).Sum(t => t.Item1.MaxEffectiveThrust);
                forwardAccel = forwardThrust / gridMass;
                forwardAccelPremultiplied = forwardAccel * maxThrustOverrideRatio;
            }
            if (counter60)
            {
                gridMass = ShipController.CalculateShipMass().PhysicalMass;
                UpdateThrust();
            }

            Vector3D myPosition = ShipController.GetPosition();
            myVelocity = ShipController.GetShipVelocities().LinearVelocity;
            mySpeed = myVelocity.Length();

            targetDirection = Target - myPosition;//aka relativePosition
            distanceToTarget = targetDirection.Length();

            //time to stop: currentSpeed / acceleration;
            //stopping distance: timeToStop * (currentSpeed / 2)
            //or also: currentSpeed^2 / (2 * acceleration)
            stopTime = mySpeed / forwardAccelPremultiplied * stopTimeAndDistanceMulti;
            stopDist = stopTime * (mySpeed * 0.5);
            //stopDist = (mySpeed * mySpeed) / (2 * forwardAccel);

            timeToStartDecel = ((distanceToTarget - stopDist) / mySpeed) + (TICK * 2);
            //double distToStartDecel = distanceToTarget - stopDist;

            currentAndDesiredSpeedDelta = Math.Abs(DesiredSpeed - mySpeed);

            if (Stage == RetroCruiseStage.None)
            {
                ResetThrustOverrides();

                //todo: make the ship stop using retroDecel

                //or cancel out current sideways velocity
                //and backwards velocity (moving away from target) if any

                Vector3D perpVel = Vector3D.ProjectOnPlane(ref myVelocity, ref targetDirection);
                if (perpVel.LengthSquared() > maxInitialPerpendicularVelocity * maxInitialPerpendicularVelocity)
                {
                    Stage = RetroCruiseStage.CancelPerpendicularVelocity;
                }
                else
                {
                    Stage = RetroCruiseStage.OrientAndAccelerate;
                }
            }

            if (Stage == RetroCruiseStage.CancelPerpendicularVelocity)
            {
                CancelPerpendicularVelocity();
            }

            if (Stage == RetroCruiseStage.OrientAndAccelerate)
            {
                OrientAndAccelerate();
            }

            if (Stage == RetroCruiseStage.OrientAndDecelerate)
            {
                OrientAndDecelerate();
            }

            if (Stage == RetroCruiseStage.DecelerateNoOrient)
            {
                DecelerateNoOrient();
            }

            if (Stage == RetroCruiseStage.Complete)
            {
                estimatedTimeOfArrival = 0;
                Complete();
            }

            if (counter10)
            {

                if (Stage <= RetroCruiseStage.OrientAndAccelerate)
                {
                    accelTime = (currentAndDesiredSpeedDelta / forwardAccelPremultiplied);
                    double accelDist = accelTime * ((mySpeed + DesiredSpeed) * 0.5);

                    double actualStopTime = DesiredSpeed / forwardAccelPremultiplied * stopTimeAndDistanceMulti;
                    double actualStopDist = actualStopTime * (DesiredSpeed * 0.5);

                    double cruiseDist = distanceToTarget - actualStopDist - accelDist;
                    double cruiseTime = cruiseDist / DesiredSpeed;

                    estimatedTimeOfArrival = accelTime + cruiseTime + actualStopTime;
                }
                else
                {
                    accelTime = 0;

                    double cruiseDist = distanceToTarget - stopDist;
                    double cruiseTime = cruiseDist / DesiredSpeed;

                    estimatedTimeOfArrival = cruiseTime + stopTime;
                }
            }
        }

        private void ResetThrustOverrides()
        {
            foreach (var list in thrusters.Values)
            {
                foreach (var thruster in list)
                {
                    thruster.Item1.ThrustOverridePercentage = 0;
                }
            }
        }

        private void ResetThrustOverridesExceptFront()
        {
            foreach (var thruster in thrusters[Direction.Backward])
                thruster.Item1.ThrustOverridePercentage = 0;
            foreach (var thruster in thrusters[Direction.Right])
                thruster.Item1.ThrustOverridePercentage = 0;
            foreach (var thruster in thrusters[Direction.Left])
                thruster.Item1.ThrustOverridePercentage = 0;
            foreach (var thruster in thrusters[Direction.Up])
                thruster.Item1.ThrustOverridePercentage = 0;
            foreach (var thruster in thrusters[Direction.Down])
                thruster.Item1.ThrustOverridePercentage = 0;
        }

        private void ResetThrustOverridesExceptBack()
        {
            foreach (var thruster in thrusters[Direction.Forward])
                thruster.Item1.ThrustOverridePercentage = 0;
            foreach (var thruster in thrusters[Direction.Right])
                thruster.Item1.ThrustOverridePercentage = 0;
            foreach (var thruster in thrusters[Direction.Left])
                thruster.Item1.ThrustOverridePercentage = 0;
            foreach (var thruster in thrusters[Direction.Up])
                thruster.Item1.ThrustOverridePercentage = 0;
            foreach (var thruster in thrusters[Direction.Down])
                thruster.Item1.ThrustOverridePercentage = 0;
        }

        private void TurnOnAllThrusters()
        {
            foreach (var list in thrusters.Values)
            {
                foreach (var thruster in list)
                {
                    thruster.Item1.Enabled = true;
                }
            }
        }

        private void ResetGyroOverride()
        {
            GyroInUse.Pitch = 0;
            GyroInUse.Yaw = 0;
            GyroInUse.Roll = 0;
            GyroInUse.GyroOverride = false;
        }

        private void Orient(Vector3D forward)
        {
            if (!GyroInUse.Enabled)
            {
                GyroInUse.Enabled = true;
            }

            AimControl.Orient(forward, GyroInUse, ShipController.WorldMatrix);
        }

        private void SetDampenerState(bool enabled)
        {
            if (ShipController.DampenersOverride != enabled)
            {
                ShipController.DampenersOverride = enabled;
            }
        }

        private void OnStageChanged()
        {
            ResetThrustOverrides();
            ResetGyroOverride();
            SetDampenerState(false);
            lastAimDirectionAngleRad = null;
        }

        private void CancelPerpendicularVelocity()
        {
            Vector3D perpVel = Vector3D.ProjectOnPlane(ref myVelocity, ref targetDirection);
            double perpSpeed = perpVel.Length();
            Vector3D aimDirection = -perpVel;

            if (perpSpeed <= maxInitialPerpendicularVelocity)
            {
                Stage = RetroCruiseStage.OrientAndAccelerate;
                return;
            }

            Orient(aimDirection);

            if (!lastAimDirectionAngleRad.HasValue)
            {
                double cos = MathHelperD.Clamp(ShipController.WorldMatrix.Forward.Dot(aimDirection) / (/*Controller.WorldMatrix.Forward.Length() * */perpSpeed), -1, 1);
                lastAimDirectionAngleRad = Math.Acos(cos);
            }

            if (lastAimDirectionAngleRad.Value <= orientToleranceAngleRadians)
            {
                float overrideAmount = MathHelper.Clamp(((float)perpSpeed * 2 * gridMass) / forwardThrust, 0, maxThrustOverrideRatio);
                foreach (var thruster in thrusters[Direction.Forward])
                {
                    thruster.Item1.ThrustOverridePercentage = overrideAmount;
                }
            }
            else
            {
                foreach (var thruster in thrusters[Direction.Forward])
                {
                    thruster.Item1.ThrustOverridePercentage = 0;
                }
            }

            if (counter10)
            {
                ResetThrustOverridesExceptFront();
            }
        }

        private void OrientAndAccelerate()
        {
            bool approaching = Vector3D.Dot(targetDirection, myVelocity) > 0;

            if ((approaching && timeToStartDecel <= decelStartMarginSeconds && mySpeed > 0.01) || (approaching && mySpeed >= DesiredSpeed))
            {
                Stage = RetroCruiseStage.OrientAndDecelerate;
                return;
            }

            Vector3D aimDirection = targetDirection;

            Orient(aimDirection);

            if (!counter10)
            {
                return;
            }

            if (!lastAimDirectionAngleRad.HasValue)
            {
                //lastAimDirectionAngleRad = Vector3D.Angle(Controller.WorldMatrix.Forward, targetDirection);
                //don't do unnecessary sqrt for controller.matrix.forward because its already a unit vector
                double cos = MathHelperD.Clamp(ShipController.WorldMatrix.Forward.Dot(aimDirection) / (/*Controller.WorldMatrix.Forward.Length() * */aimDirection.Length()), -1, 1);
                lastAimDirectionAngleRad = Math.Acos(cos);

                if (lastAimDirectionAngleRad.Value == double.NaN)
                {
                    lastAimDirectionAngleRad = 0;
                }
            }

            if (lastAimDirectionAngleRad.Value <= orientToleranceAngleRadians)
            {
                foreach (var thruster in thrusters[Direction.Forward])
                {
                    thruster.Item1.ThrustOverridePercentage = maxThrustOverrideRatio;
                }

                DampenSideways(myVelocity * 0.1);
                return;
            }

            ResetThrustOverridesExceptBack();
        }

        private void OrientAndDecelerate()
        {
            if (mySpeed <= completionShipSpeed)
            {
                Stage = RetroCruiseStage.Complete;
                return;
            }

            bool approaching = Vector3D.Dot(targetDirection, myVelocity) > 0;

            if (!approaching)
            {
                Stage = RetroCruiseStage.DecelerateNoOrient;
                return;
            }

            Vector3D orientForward = -(targetDirection + myVelocity);

            Orient(orientForward);

            if (!lastAimDirectionAngleRad.HasValue)
            {
                //lastAimDirectionAngleRad = Vector3D.Angle(Controller.WorldMatrix.Forward, orientForward);
                //don't do unnecessary sqrt for controller.matrix.forward because its already a unit vector
                double cos = MathHelperD.Clamp(ShipController.WorldMatrix.Forward.Dot(orientForward) / (/*Controller.WorldMatrix.Forward.LengthSquared() * */orientForward.Length()), -1, 1);
                lastAimDirectionAngleRad = Math.Acos(cos);

                if (lastAimDirectionAngleRad.Value == double.NaN)
                {
                    lastAimDirectionAngleRad = 0;
                }
            }

            if (lastAimDirectionAngleRad.Value <= orientToleranceAngleRadians)
            {
                if (distanceToTarget < forwardAccelPremultiplied)
                {
                    Stage = RetroCruiseStage.DecelerateNoOrient;
                    return;
                }

                float overrideAmount = Math.Min(((float)-timeToStartDecel + maxThrustOverrideRatio), maxThrustOverrideRatio);

                foreach (var thruster in thrusters[Direction.Forward])
                {
                    thruster.Item1.ThrustOverridePercentage = overrideAmount;
                }

                DampenSideways(myVelocity);
                return;
            }

            if (timeToStartDecel > 0)
            {
                ResetThrustOverridesExceptBack();
                return;
            }

            DampenAllDirections(myVelocity);
        }

        private void DecelerateNoOrient()
        {
            if (mySpeed <= completionShipSpeed)
            {
                Stage = RetroCruiseStage.Complete;
                return;
            }

            bool approaching = Vector3D.Dot(targetDirection, myVelocity) > 0;

            if (!approaching)
            {
                DampenAllDirections(myVelocity);
                return;
            }

            float overrideAmount = Math.Min(((float)-timeToStartDecel + maxThrustOverrideRatio), maxThrustOverrideRatio);

            foreach (var thruster in thrusters[Direction.Forward])
            {
                thruster.Item1.ThrustOverridePercentage = overrideAmount;
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

        protected override void OnNoFunctionalGyrosLeft()
        {
            ResetThrustOverrides();
            TurnOnAllThrusters();

            ResetGyroOverride();

            Stage = RetroCruiseStage.Aborted;

            CruiseTerminated.Invoke(this, "No functional gyros found");
        }
    }
}
