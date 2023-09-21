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
    public class RetroCruiseControl : OrientControllerBase, ICruiseController, IVariableMaxOverrideThrustController
    {
        public enum RetroCruiseStage : byte
        {
            None = 0,
            CancelPerpendicularVelocity = 1,
            OrientAndAccelerate = 2,
            OrientAndDecelerate = 3,
            DecelerateNoOrient = 4,
            //FinalDecelAndStop = 5,
            Complete = 6,
            Aborted = 7,
        }

        const double TICK = 1.0 / 60.0;
        const double DegToRadMulti = Math.PI / 180.0;
        const double RadToDegMulti = 180.0 / Math.PI;

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
        public float MaxThrustRatio
        {
            get { return _maxThrustOverrideRatio; }
            set
            {
                if ( _maxThrustOverrideRatio != value)
                {
                    _maxThrustOverrideRatio = value;
                    UpdateThrust();
                    UpdateForwardThrustAndAccel();
                }
            }
        }

        /// <summary>
        /// what speed end cruise routine during deceleration
        /// </summary>
        public double completionShipSpeed = 0.05;

        /// <summary>
        /// timeToStop + value to start rotating the ship for deceleration
        /// </summary>
        public double decelStartMarginSeconds = 10;

        /// <summary>
        /// aim/orient tolerance in radians
        /// </summary>
        public double OrientToleranceAngleRadians
        {
            get { return _orientToleranceAngleRadians; }
            set
            {
                if (_orientToleranceAngleRadians != value)
                {
                    _orientToleranceAngleRadians = value;
                    _orientToleranceAngleRadiansCos = Math.Cos(value);
                }
            }
        }
        private double _orientToleranceAngleRadians = 0.075 * DegToRadMulti;
        private double _orientToleranceAngleRadiansCos;

        private float _maxThrustOverrideRatio = 1f;
        public double maxInitialPerpendicularVelocity = 1;

        //public float reserveThrustRatio = 0.05f;

        //useful for overestimating stop time and dist for better cruise accuracy
        public double stopTimeAndDistanceMulti = 1.05;

        //how far off the aim is from the desired orientation
        private Dictionary<Direction, MyTuple<IMyThrust, float>[]> thrusters;

        const float DAMPENER_TOLERANCE = 0.01f;

        private bool counter5 = false;
        private bool counter10 = false;

        //active variables
        private RetroCruiseStage _stage;
        private int counter = -1;

        //updated every 30 ticks
        private float gridMass;
        private float forwardAccelPremultiplied; //premultiplied by maxThrustOverrideRatio
        private float forwardThrustInv;

        //updated every 10 ticks
        private double? lastAimDirectionAngleRad = null;
        private double estimatedTimeOfArrival;

        //updated every tick
        private double accelTime;
        private double timeToStartDecel;
        private double cruiseTime;
        private double currentStopDist;
        private double actualStopTime;
        private double distanceToTarget;
        private Vector3D myVelocity;
        private Vector3D targetDirection;
        private Vector3D gravityAtPos;
        private double vmax;

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
                    .Select(thrust => new MyTuple<IMyThrust, float>(thrust, thrust.MaxEffectiveThrust * MaxThrustRatio))
                    .ToArray());

            Stage = RetroCruiseStage.None;
            gridMass = controller.CalculateShipMass().PhysicalMass;

            UpdateForwardThrustAndAccel();
        }

        public void AppendStatus(StringBuilder strb)
        {
            strb.Append("\n-- Cruise Status --\n\n");

            if (timeToStartDecel < 0 || Vector3D.Dot(myVelocity, targetDirection) < 0)
            {
                strb.Append($"!! Overshoot Warning !!\n\n");
            }

            const string stage1 = "> Cancel Perpendicular Speed\n";

            switch (Stage)
            {
                case RetroCruiseStage.CancelPerpendicularVelocity:
                case RetroCruiseStage.OrientAndAccelerate:
                    strb.Append((byte)Stage == 1 ? stage1 : $">{stage1}>")
                        .Append(" Accelerate ").AppendTime(accelTime)
                        .Append("\nCruise ").AppendTime(cruiseTime)
                        .Append("\nDecelerate ").AppendTime(actualStopTime)
                        .Append("\nStop");
                    break;
                case RetroCruiseStage.OrientAndDecelerate:
                    strb.Append($">{stage1}>> Accelerate 0:00\n");
                    if (!decelerating)
                        strb.Append("> Cruise ").AppendTime(cruiseTime).AppendLine();
                    else
                        strb.Append(">> Cruise ").Append(timeToStartDecel.ToString("0:00.000")).Append("\n> ");
                    strb.Append("Decelerate ").AppendTime(actualStopTime).Append("\nStop");
                    break;
                case RetroCruiseStage.DecelerateNoOrient:
                    strb.Append($">{stage1}>> Accelerate 0:00\n>> Cruise 0:00\n>> Decelerate 0:00\n> Stop").AppendTime(actualStopTime);
                    break;
            }

            strb.Append("\n\nETA: ").AppendTime(estimatedTimeOfArrival);

            if (vmax != 0)
                strb.Append("\nMax Speed: ").Append(vmax.ToString("0.00"));

            strb.Append("\nStoppingDistance: ").Append(currentStopDist.ToString("0.0"))
            .Append("\nTargetDistance: ").Append(distanceToTarget.ToString("0.0"))
            .Append("\nDesired Speed: ").Append(DesiredSpeed.ToString("0.##"))
            .Append("\nAim Error: ").Append(((lastAimDirectionAngleRad ?? 0) * RadToDegMulti).ToString("0.000\n"));
        }

        private static readonly RetroCruiseStage[] allStages = (RetroCruiseStage[])Enum.GetValues(typeof(RetroCruiseStage));

        private void DampenAllDirections(Vector3D shipVelocity, float tolerance = DAMPENER_TOLERANCE)
        {
            Vector3 localVelocity = Vector3D.TransformNormal(shipVelocity, MatrixD.Transpose(ShipController.WorldMatrix));
            Vector3 thrustAmount = localVelocity * gridMass;
            float backward = thrustAmount.Z < tolerance ? -thrustAmount.Z : 0;
            float forward = thrustAmount.Z > tolerance ? thrustAmount.Z : 0;
            float right = thrustAmount.X < tolerance ? -thrustAmount.X : 0;
            float left = thrustAmount.X > tolerance ? thrustAmount.X : 0;
            float up = thrustAmount.Y < tolerance ? -thrustAmount.Y : 0;
            float down = thrustAmount.Y > tolerance ? thrustAmount.Y : 0;

            foreach (var thrust in thrusters[Direction.Forward])
                thrust.Item1.ThrustOverride = Math.Min(forward, thrust.Item2);
            foreach (var thrust in thrusters[Direction.Backward])
                thrust.Item1.ThrustOverride = backward;

            SetSideThrustOverrides(left, right, up, down);
        }

        private void UpdateThrust()
        {
            foreach (var kv in thrusters)
            {
                for (int i = 0; i < kv.Value.Length; i++)
                {
                    var val = kv.Value[i];
                    val.Item2 = val.Item1.MaxEffectiveThrust * MaxThrustRatio;
                    kv.Value[i] = val;
                }
            }
        }

        private void DampenSideways(Vector3D shipVelocity, float tolerance = DAMPENER_TOLERANCE)
        {
            Vector3 localVelocity = Vector3D.TransformNormal(shipVelocity, MatrixD.Transpose(ShipController.WorldMatrix));
            Vector3 thrustAmount = localVelocity * gridMass;
            float right = thrustAmount.X < tolerance ? -thrustAmount.X : 0;
            float left = thrustAmount.X > tolerance ? thrustAmount.X : 0;
            float up = thrustAmount.Y < tolerance ? -thrustAmount.Y : 0;
            float down = thrustAmount.Y > tolerance ? thrustAmount.Y : 0;

            SetSideThrustOverrides(left, right, up, down);
        }

        private void SetSideThrustOverrides(float left, float right, float up, float down)
        {
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
            bool counter5 = counter % 5 == 0;
            counter10 = counter % 10 == 0;
            bool counter30 = counter % 30 == 0;
            bool counter60 = counter % 60 == 0;

            if (Stage == RetroCruiseStage.None)
            {
                ResetGyroOverride();
                ResetThrustOverrides();
                TurnOnAllThrusters();
                UpdateThrust();
            }

            if (counter10)
            {
                lastAimDirectionAngleRad = null;

                SetDampenerState(false);
            }
            if (counter30)
            {
                UpdateForwardThrustAndAccel();
                gravityAtPos = ShipController.GetNaturalGravity();
            }
            if (counter60)
            {
                gridMass = ShipController.CalculateShipMass().PhysicalMass;
                UpdateThrust();
            }

            Vector3D myPosition = ShipController.GetPosition();
            myVelocity = ShipController.GetShipVelocities().LinearVelocity + gravityAtPos;
            double mySpeed = myVelocity.Length();

            targetDirection = Target - myPosition;//aka relativePosition
            distanceToTarget = targetDirection.Length();

            //time to stop: currentSpeed / acceleration;
            //stopping distance: timeToStop * (currentSpeed / 2)
            //or also: currentSpeed^2 / (2 * acceleration)
            //stopTime = mySpeed / forwardAccelPremultiplied * stopTimeAndDistanceMulti;
            //stopDist = stopTime * (mySpeed * 0.5);
            currentStopDist = (mySpeed * mySpeed) / (2 * forwardAccelPremultiplied) * stopTimeAndDistanceMulti;

            timeToStartDecel = ((distanceToTarget - currentStopDist) / mySpeed) + TICK;

            double currentAndDesiredSpeedDelta = Math.Abs(DesiredSpeed - mySpeed);

            if (Stage == RetroCruiseStage.None)
            {
                Vector3D perpVel = Vector3D.ProjectOnPlane(ref myVelocity, ref targetDirection);
                if (perpVel.LengthSquared() > maxInitialPerpendicularVelocity * maxInitialPerpendicularVelocity)
                    Stage = RetroCruiseStage.CancelPerpendicularVelocity;
                else
                    Stage = RetroCruiseStage.OrientAndAccelerate;
            }

            while (true)
            {
                if (Stage == RetroCruiseStage.CancelPerpendicularVelocity)
                {
                    CancelPerpendicularVelocity();
                }

                if (Stage == RetroCruiseStage.OrientAndAccelerate)
                {
                    OrientAndAccelerate(mySpeed);
                }

                if (Stage == RetroCruiseStage.OrientAndDecelerate)
                {
                    if (counter30 && timeToStartDecel * 0.9 > decelStartMarginSeconds && mySpeed < DesiredSpeed * 0.5)
                    {
                        Stage = RetroCruiseStage.OrientAndAccelerate;
                        continue;
                    }

                    OrientAndDecelerate(mySpeed);
                }

                if (Stage == RetroCruiseStage.DecelerateNoOrient)
                {
                    DecelerateNoOrient(mySpeed);
                }

                if (Stage == RetroCruiseStage.Complete)
                {
                    estimatedTimeOfArrival = 0;
                    Complete();
                }

                break;
            }

            if (counter10)
            {

                if (Stage <= RetroCruiseStage.OrientAndAccelerate)
                {
                    accelTime = (currentAndDesiredSpeedDelta / forwardAccelPremultiplied);
                    double accelDist = accelTime * ((mySpeed + DesiredSpeed) * 0.5);

                    actualStopTime = DesiredSpeed / forwardAccelPremultiplied * stopTimeAndDistanceMulti;
                    double actualStopDist = actualStopTime * (DesiredSpeed * 0.5);

                    double cruiseDist = distanceToTarget - actualStopDist - accelDist;
                    cruiseTime = cruiseDist / DesiredSpeed;

                    if (cruiseTime < decelStartMarginSeconds)
                    {
                        //https://math.stackexchange.com/questions/637042/calculate-maximum-velocity-given-accel-decel-initial-v-final-position

                        //v0 = initial (current) speed
                        //vmax = max speed;
                        //v2 = final speed (zero)
                        //a = accel
                        //d = deceleration (NOT DISTANCE!!!!)
                        //t1 = time at max achievable speed
                        //t2 = decel time
                        //x = starting (current) position (aka. x == 0)
                        //l = end position (distance)

                        double v0 = mySpeed;
                        double a = forwardAccelPremultiplied;
                        double d = forwardAccelPremultiplied * (2 - stopTimeAndDistanceMulti);
                        double l = distanceToTarget;

                        //v0 + (a * t1) - (d * t2) == 0
                        //rearranged: t2 == (v0 + (a * t1)) / d

                        //(v0 * t1) + (00.5 * a * t1^2) + ((v0 * t2) + (a * t1 * t2) - (0.5 * d * t2^2)) == l;

                        //t1 == -(v0 / a) + (1 / a) * sqrt(((d * v0^2) + (2 * a * l * d)) / (a + d))
                        //vmax == v0 + (a * t1) == sqrt(((d * v0^2) + (2 * a * l * d)) / (a + d))

                        vmax = Math.Sqrt((d * v0 * v0 + 2 * a * l * d) / (a + d));
                        accelTime = (vmax - v0) / a;
                        actualStopTime = vmax / d;
                        estimatedTimeOfArrival = accelTime + actualStopTime;
                        cruiseTime = 0;
                    }
                    else
                    {
                        vmax = 0;
                        estimatedTimeOfArrival = accelTime + cruiseTime + actualStopTime;
                    }
                }
                else
                {
                    accelTime = 0;
                    actualStopTime = mySpeed / forwardAccelPremultiplied * stopTimeAndDistanceMulti; ;

                    double cruiseDist = distanceToTarget - currentStopDist;
                    cruiseTime = cruiseDist / mySpeed;

                    estimatedTimeOfArrival = cruiseTime + actualStopTime;
                }
            }
        }

        private void UpdateForwardThrustAndAccel()
        {
            float forwardThrust = thrusters[Direction.Forward].Where(t => t.Item1.IsWorking).Sum(t => t.Item1.MaxEffectiveThrust);
            forwardThrustInv = 1f / forwardThrust;
            float forwardAccel = forwardThrust / gridMass;
            forwardAccelPremultiplied = forwardAccel * MaxThrustRatio;
        }

        private void ResetThrustOverrides()
        {
            foreach (var list in thrusters)
                foreach (var thruster in list.Value)
                    thruster.Item1.ThrustOverride = 0;
        }

        private void ResetThrustOverridesExceptFront()
        {
            foreach (var thruster in thrusters[Direction.Backward])
                thruster.Item1.ThrustOverride = 0;
            ResetThrustOverridesSides();
        }

        private void ResetThrustOverridesExceptBack()
        {
            foreach (var thruster in thrusters[Direction.Forward])
                thruster.Item1.ThrustOverride = 0;
            ResetThrustOverridesSides();
        }

        private void ResetThrustOverridesSides()
        {
            foreach (var thruster in thrusters[Direction.Right])
                thruster.Item1.ThrustOverride = 0;
            foreach (var thruster in thrusters[Direction.Left])
                thruster.Item1.ThrustOverride = 0;
            foreach (var thruster in thrusters[Direction.Up])
                thruster.Item1.ThrustOverride = 0;
            foreach (var thruster in thrusters[Direction.Down])
                thruster.Item1.ThrustOverride = 0;
        }

        private void TurnOnAllThrusters()
        {
            foreach (var list in thrusters)
                foreach (var thruster in list.Value)
                    thruster.Item1.Enabled = true;
        }

        private void SetDampenerState(bool enabled) => ShipController.DampenersOverride = enabled;

        private void OnStageChanged()
        {
            ResetThrustOverrides();
            ResetGyroOverride();
            SetDampenerState(false);
            lastAimDirectionAngleRad = null;
            decelerating = false;
        }

        private void CancelPerpendicularVelocity()
        {
            Vector3D aimDirection = -Vector3D.ProjectOnPlane(ref myVelocity, ref targetDirection);
            double perpSpeed = aimDirection.Length();

            if (perpSpeed <= maxInitialPerpendicularVelocity)
            {
                Stage = RetroCruiseStage.OrientAndAccelerate;
                return;
            }

            Orient(aimDirection);

            if (!lastAimDirectionAngleRad.HasValue)
            {
                lastAimDirectionAngleRad = AngleRadiansBetweenVectorAndControllerForward(aimDirection);
            }

            if (lastAimDirectionAngleRad.Value <= OrientToleranceAngleRadians)
            {
                float overrideAmount = MathHelper.Clamp(((float)perpSpeed * 2 * gridMass) * forwardThrustInv, 0, MaxThrustRatio);
                foreach (var thruster in thrusters[Direction.Forward])
                {
                    thruster.Item1.ThrustOverridePercentage = overrideAmount;
                }
            }
            else
            {
                foreach (var thruster in thrusters[Direction.Forward])
                {
                    thruster.Item1.ThrustOverride = 0;
                }
            }

            if (counter10)
            {
                ResetThrustOverridesExceptFront();
            }
        }

        private void OrientAndAccelerate(double mySpeed)
        {
            bool approaching = Vector3D.Dot(targetDirection, myVelocity) > 0;

            if ((approaching && timeToStartDecel <= decelStartMarginSeconds && mySpeed > 0.1) || (approaching && mySpeed >= DesiredSpeed))
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
                lastAimDirectionAngleRad = AngleRadiansBetweenVectorAndControllerForward(aimDirection);
            }

            if (lastAimDirectionAngleRad.Value <= OrientToleranceAngleRadians)
            {
                foreach (var thruster in thrusters[Direction.Forward])
                {
                    thruster.Item1.ThrustOverridePercentage = MaxThrustRatio;
                }

                DampenSideways(myVelocity * 0.1);
                return;
            }

            ResetThrustOverridesExceptBack();
        }

        private bool decelerating = false;

        private void OrientAndDecelerate(double mySpeed)
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
                lastAimDirectionAngleRad = AngleRadiansBetweenVectorAndControllerForward(orientForward);
            }

            if (lastAimDirectionAngleRad.Value <= OrientToleranceAngleRadians)
            {
                if (distanceToTarget < forwardAccelPremultiplied)
                {
                    Stage = RetroCruiseStage.DecelerateNoOrient;
                    return;
                }

                float overrideAmount = MathHelper.Clamp(((float)-timeToStartDecel + MaxThrustRatio), 0f, MaxThrustRatio);

                decelerating = overrideAmount > 0;

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

        private void DecelerateNoOrient(double mySpeed)
        {
            if (mySpeed <= completionShipSpeed)
            {
                Stage = RetroCruiseStage.Complete;
                return;
            }

            if (!lastAimDirectionAngleRad.HasValue)
            {
                lastAimDirectionAngleRad = AngleRadiansBetweenVectorAndControllerForward(-targetDirection);
            }

            bool approaching = Vector3D.Dot(targetDirection, myVelocity) > 0;

            if (!approaching)
            {
                DampenAllDirections(myVelocity, 0);
                return;
            }

            float overrideAmount = Math.Min(((float)-timeToStartDecel + MaxThrustRatio), MaxThrustRatio);

            decelerating = overrideAmount > 0;

            foreach (var thruster in thrusters[Direction.Forward])
            {
                thruster.Item1.ThrustOverridePercentage = overrideAmount;
            }

            DampenSideways(myVelocity, 0);
        }

        private double AngleRadiansBetweenVectorAndControllerForward(Vector3D vec)
        {
            //don't do unnecessary sqrt for controller.matrix.forward because its already a unit vector
            double cos = ShipController.WorldMatrix.Forward.Dot(vec) / vec.Length();
            double angle = Math.Acos(cos);
            if (double.IsNaN(angle))
                angle = 0;
            return angle;
        }

        private void Complete()
        {
            SetDampenerState(true);
            Terminate(distanceToTarget < 10 ? "Destination Reached" : "Terminated");
        }

        public void Terminate(string reason)
        {
            ResetThrustOverrides();
            TurnOnAllThrusters();

            ResetGyroOverride();

            CruiseTerminated.Invoke(this, reason);
        }

        public void Abort()
        {
            Stage = RetroCruiseStage.Aborted;
            Terminate("Aborted");
        }

        protected override void OnNoFunctionalGyrosLeft() => Terminate("No functional gyros found");
    }
}
