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
        public float MaxThrustOverrideRatio
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
        private double _orientToleranceAngleRadians = 0.05 * DegToRadMulti;
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
        private float forwardThrustInv;

        //updated every 10 ticks
        private double? lastAimDirectionAngleRad = null;
        private double estimatedTimeOfArrival;

        //updated every tick
        private double accelTime;
        private double timeToStartDecel;
        private double cruiseTime;
        private double stopTime;
        private double stopDist;
        private double actualStopTime;
        private double distanceToTarget;
        private Vector3D myVelocity;
        private double mySpeed;
        private Vector3D targetDirection;
        private double currentAndDesiredSpeedDelta;

        private string cruiseStageStr;
        private StringBuilder statusStrb = new StringBuilder();

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
                    .Select(thrust => new MyTuple<IMyThrust, float>(thrust, thrust.MaxEffectiveThrust * MaxThrustOverrideRatio))
                    .ToArray());

            Stage = RetroCruiseStage.None;
            gridMass = controller.CalculateShipMass().PhysicalMass;

            UpdateForwardThrustAndAccel();
        }

        public void AppendStatus(StringBuilder strb)
        {
            strb.AppendStringBuilder(statusStrb);
        }

        private static readonly RetroCruiseStage[] allStages = (RetroCruiseStage[])Enum.GetValues(typeof(RetroCruiseStage));

        private void UpdateStatusStrb()
        {
            statusStrb.Clear();
            statusStrb.AppendLine("\n-- Cruise Status --\n");

            if (timeToStartDecel < 0 || Vector3D.Dot(myVelocity, targetDirection) < 0)
            {
                statusStrb.Append($"!! Overshoot Warning !!\n\n");
            }

            const string stage1 = "> Cancel Perpendicular Speed\n";

            switch (Stage)
            {
                case RetroCruiseStage.CancelPerpendicularVelocity:
                case RetroCruiseStage.OrientAndAccelerate:
                    statusStrb.AppendLine($"{((byte)Stage == 1 ? $"{stage1}" : $">{stage1}>")}> Accelerate {accelTime:0.0}\nCruise {cruiseTime:0.0}\nDecelerate {actualStopTime:0.0}\nStop");
                    break;
                case RetroCruiseStage.OrientAndDecelerate:
                    statusStrb.AppendLine($">{stage1}>> Accelerate {accelTime:0.0}");
                    if (!decelerating)
                        statusStrb.AppendLine($"> Cruise {cruiseTime:0.0}\nDecelerate {actualStopTime:0.0}\nStop");
                    else
                        statusStrb.AppendLine($">> Cruise {cruiseTime:0.0}\n> Decelerate {actualStopTime:0.0}\nStop");
                    break;
                case RetroCruiseStage.DecelerateNoOrient:
                    statusStrb.AppendLine($">{stage1}>> Accelerate {accelTime:0.0}\n>> Cruise {cruiseTime:0.0}\n>> Decelerate {actualStopTime:0.0}\n> Stop");
                    break;
            }

            statusStrb.Append("\nETA: ").Append(estimatedTimeOfArrival.ToString("0.0"));
            statusStrb.Append("\nDesired Speed: ").Append(DesiredSpeed.ToString("0.##"));
            statusStrb.Append("\nTargetDistance: ").Append(distanceToTarget.ToString("0.0"));
            statusStrb.Append("\nTimeToStartDecel: ").Append(timeToStartDecel.ToString("0.000"));
            statusStrb.Append("\nStoppingDistance: ").Append(stopDist.ToString("0.0"));
            statusStrb.Append("\nAim Error: ").Append(((lastAimDirectionAngleRad ?? 0) * RadToDegMulti).ToString("0.000"));
            statusStrb.AppendLine();
        }

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
                    val.Item2 = val.Item1.MaxEffectiveThrust * MaxThrustOverrideRatio;
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

            if (Stage == RetroCruiseStage.None)
            {
                ResetGyroOverride();
                ResetThrustOverrides();
                TurnOnAllThrusters();
                UpdateThrust();
            }

            if (counter10)
            {
                UpdateStatusStrb();

                lastAimDirectionAngleRad = null;

                SetDampenerState(false);
            }
            if (counter30)
            {
                UpdateForwardThrustAndAccel();
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
            //stopDist = stopTime * (mySpeed * 0.5);
            stopDist = (mySpeed * mySpeed) / (2 * forwardAccel);

            timeToStartDecel = ((distanceToTarget - stopDist) / mySpeed) + (TICK * 2);
            //double distToStartDecel = distanceToTarget - stopDist;

            currentAndDesiredSpeedDelta = Math.Abs(DesiredSpeed - mySpeed);

            if (Stage == RetroCruiseStage.None)
            {
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

            while (true)
            {
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
                    if (counter30 && timeToStartDecel * 0.9 > decelStartMarginSeconds && mySpeed < DesiredSpeed * 0.5)
                    {
                        Stage = RetroCruiseStage.OrientAndAccelerate;
                        continue;
                    }

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

                    estimatedTimeOfArrival = accelTime + cruiseTime + actualStopTime;
                }
                else
                {
                    accelTime = 0;
                    actualStopTime = stopTime;

                    double cruiseDist = distanceToTarget - stopDist;
                    cruiseTime = cruiseDist / mySpeed;

                    estimatedTimeOfArrival = cruiseTime + stopTime;
                }
            }
        }

        private void UpdateForwardThrustAndAccel()
        {
            forwardThrust = thrusters[Direction.Forward].Where(t => t.Item1.IsWorking).Sum(t => t.Item1.MaxEffectiveThrust);
            forwardThrustInv = 1f / forwardThrust;
            forwardAccel = forwardThrust / gridMass;
            forwardAccelPremultiplied = forwardAccel * MaxThrustOverrideRatio;
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
            ResetThrustOverridesSides();
        }

        private void ResetThrustOverridesExceptBack()
        {
            foreach (var thruster in thrusters[Direction.Forward])
                thruster.Item1.ThrustOverridePercentage = 0;
            ResetThrustOverridesSides();
        }

        private void ResetThrustOverridesSides()
        {
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

            UpdateStatusStrb();
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
                lastAimDirectionAngleRad = AngleRadiansBetweenVectorAndControllerForward(aimDirection);
            }

            if (lastAimDirectionAngleRad.Value <= OrientToleranceAngleRadians)
            {
                float overrideAmount = MathHelper.Clamp(((float)perpSpeed * 2 * gridMass) / forwardThrust, 0, MaxThrustOverrideRatio);
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
                lastAimDirectionAngleRad = AngleRadiansBetweenVectorAndControllerForward(aimDirection);
            }

            if (lastAimDirectionAngleRad.Value <= OrientToleranceAngleRadians)
            {
                foreach (var thruster in thrusters[Direction.Forward])
                {
                    thruster.Item1.ThrustOverridePercentage = MaxThrustOverrideRatio;
                }

                DampenSideways(myVelocity * 0.1);
                return;
            }

            ResetThrustOverridesExceptBack();
        }

        private bool decelerating = false;

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
                lastAimDirectionAngleRad = AngleRadiansBetweenVectorAndControllerForward(orientForward);
            }

            if (lastAimDirectionAngleRad.Value <= OrientToleranceAngleRadians)
            {
                if (distanceToTarget < forwardAccelPremultiplied)
                {
                    Stage = RetroCruiseStage.DecelerateNoOrient;
                    return;
                }

                float overrideAmount = MathHelper.Clamp(((float)-timeToStartDecel + MaxThrustOverrideRatio), 0f, MaxThrustOverrideRatio);

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

        private void DecelerateNoOrient()
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

            float overrideAmount = Math.Min(((float)-timeToStartDecel + MaxThrustOverrideRatio), MaxThrustOverrideRatio);

            foreach (var thruster in thrusters[Direction.Forward])
            {
                thruster.Item1.ThrustOverridePercentage = overrideAmount;
            }

            DampenSideways(myVelocity, 0);
        }

        private double AngleRadiansCosBetweenVectorAndControllerForward(Vector3D vec)
        {
            return ShipController.WorldMatrix.Forward.Dot(vec) / vec.Length();
        }

        private double AngleRadiansBetweenVectorAndControllerForward(Vector3D vec)
        {
            //don't do unnecessary sqrt for controller.matrix.forward because its already a unit vector
            double cos = ShipController.WorldMatrix.Forward.Dot(vec) / vec.Length();
            double angle = Math.Acos(cos);
            return angle != double.NaN ? angle : 0;
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

            Stage = RetroCruiseStage.Aborted;

            CruiseTerminated.Invoke(this, reason);
        }

        public void Abort() => Terminate("Aborted");
        protected override void OnNoFunctionalGyrosLeft() => Terminate("No functional gyros found");
    }
}
