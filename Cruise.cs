using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    partial class Program
    {
        void Cruise()
        {
            var gridMass = navRef.CalculateShipMass().PhysicalMass;
            var currentSpeed = navRef.GetShipSpeed();

            remainingDist = cruiseDist - Vector3D.Distance(Me.CubeGrid.GetPosition(), startPos);
            var totalAccelDist = (cruiseSpeed * cruiseSpeed) / (2 * (foreThrust / gridMass));
            var stopDist = (currentSpeed * currentSpeed) / (2 * (backThrust / gridMass));
            var totalStopDist = (cruiseSpeed * cruiseSpeed) / (2 * (backThrust / gridMass));

            var accelTime = (cruiseSpeed - currentSpeed) / (foreThrust / gridMass);
            var stopTime = currentSpeed / (backThrust / gridMass);
            var totalStopTime = cruiseSpeed / (backThrust / gridMass);
            var maxSpeedDuration = (cruiseDist - totalStopDist - totalAccelDist) / cruiseSpeed;

            if (cruiseStage == CruiseStageEnum.Acceleleration)
            {
                ETA = maxSpeedDuration + accelTime + totalStopTime;
                CruiseStage1(currentSpeed, stopDist);
            }

            if (cruiseStage == CruiseStageEnum.Cruise)
            {
                ETA = stopTime + ((remainingDist - stopDist) / cruiseSpeed);
                CruiseStage2(stopDist);
            }

            if (cruiseStage == CruiseStageEnum.Deceleration)
            {
                ETA = stopTime;
                CruiseStage3(currentSpeed);
            }
        }

        void CruiseStage1(double currentSpeed, double stopDist)
        {
            if (currentSpeed < cruiseSpeed && stopDist < remainingDist)
            {
                navRef.DampenersOverride = true;
                thrusters[Base6Directions.Direction.Forward].ForEach(t => t.ThrustOverridePercentage = 1);
                thrusters[Base6Directions.Direction.Backward].ForEach(t => t.Enabled = false);
                var g = gyros.First();
                g.Pitch = 0;
                g.Yaw = 0;
                g.Roll = 0;
                g.GyroOverride = true;
            }
            else if (currentSpeed >= cruiseSpeed)
                cruiseStage = CruiseStageEnum.Cruise;
            else if (stopDist >= remainingDist)
                cruiseStage = CruiseStageEnum.Deceleration;
        }

        void CruiseStage2(double stopDist)
        {
            if (stopDist < remainingDist)
            {
                navRef.DampenersOverride = true;
                thrusters[Base6Directions.Direction.Forward].ForEach(t => t.ThrustOverridePercentage = 0);
                thrusters[Base6Directions.Direction.Backward].ForEach(t => t.Enabled = false);
                var g = gyros.First();
                g.Pitch = 0;
                g.Yaw = 0;
                g.Roll = 0;
                g.GyroOverride = true;
            }
            else if (stopDist >= remainingDist)
                cruiseStage = CruiseStageEnum.Deceleration;
        }

        void CruiseStage3(double currentSpeed)
        {
            if (currentSpeed > 5)
            {
                navRef.DampenersOverride = true;
                thrusters[Base6Directions.Direction.Forward].ForEach(t => t.ThrustOverridePercentage = 0);
                thrusters[Base6Directions.Direction.Backward].ForEach(t => t.Enabled = true);
                var g = gyros.First();
                g.Pitch = 0;
                g.Yaw = 0;
                g.Roll = 0;
                g.GyroOverride = true;
            }
            else if (currentSpeed < 5)
                SysReset();
        }
    }
}
