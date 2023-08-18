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
        Vector3D retroCruiseDestination = Vector3D.Zero;
        double retroOffset = 0;

        void RetroCruiseControl(bool firstRun = false)
        {
            float gridMass = navRef.CalculateShipMass().PhysicalMass;
            Vector3D mySpeedVec = navRef.GetShipVelocities().LinearVelocity;
            double mySpeed = mySpeedVec.Length();
            double forwardThrust = thrusters[Base6Directions.Direction.Forward].FindAll(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
            
            //in m/s^2
            double accel = forwardThrust / gridMass;

            //time = (mass * speed) / force
            //dist = (mass * speed)^2 / (2 * force)//wtf is this
            //dist = speed^2 / (2 * accel)
            double stopTime = (gridMass * mySpeed) / forwardThrust;
            double stopDist = mySpeed.Sq() / (2 * accel);

            //time to x speed from 0 = speed / accel
            //dist to x speed = (mySpeed + finalSpeed) / 2 * time

            remainingDist = Vector3D.Distance(navRef.GetPosition(), retroCruiseDestination) - retroOffset;


            if (firstRun)
            {
                if (retroCruiseDestination.IsZero())
                {
                    retroCruiseDestination = (navRef.WorldMatrix.Forward * cruiseDist) + navRef.GetPosition();
                }
                cruiseStage = CruiseStageEnum.Acceleleration;
            }

            if (cruiseStage == CruiseStageEnum.Acceleleration)
            {
                double accelTime = ((cruiseSpeed - mySpeed) / accel);
                double accelDist = (mySpeed + cruiseSpeed) / (2 * accelTime);
                double cruiseTime = (remainingDist - stopDist - accelDist) / cruiseSpeed;
                ETA = accelTime + ((gridMass * cruiseSpeed) / forwardThrust) + cruiseTime;
                RetroAccelStage();
                if (mySpeed >= cruiseSpeed)
                {
                    cruiseStage = CruiseStageEnum.Cruise;
                }
                else if (remainingDist <= stopDist)
                {
                    cruiseStage = CruiseStageEnum.Deceleration;
                }
            }

            if (cruiseStage == CruiseStageEnum.Cruise)
            {
                double cruiseTime = (remainingDist - stopDist) / mySpeed;
                ETA = stopTime + cruiseTime;
                RetroCruiseStage(mySpeedVec);
                if (remainingDist <= stopDist)
                {
                    cruiseStage = CruiseStageEnum.Deceleration;
                }
            }

            if (cruiseStage == CruiseStageEnum.Deceleration)
            {
                ETA = stopTime;
                RetroDecelStage(mySpeedVec);
                if (mySpeed <= 0.5)
                {
                    RetroCruiseReset();
                }
            }
        }

        void RetroCruiseReset()
        {
            retroOffset = 0;
            cruiseActive = false;
            retro = false;
            retroDecel = false;
            SysReset();
            retroCruiseDestination = Vector3D.Zero;
            gyros.ForEach(g => { g.Pitch = 0f; g.Roll = 0f; g.Yaw = 0f; g.GyroOverride = false; });
            cruiseStage = 0;
            cruiseDist = 0;
            cruiseSpeed = 0;
            ETA = 0;

            remainingDist = 0;
        }
        Vector3D aimDir;
        void RetroAccelStage()
        {
            aimDir = retroCruiseDestination - navRef.GetPosition();
            aim.Orient(aimDir, gyro, navRef.WorldMatrix);
            if (GetAccuracy(aimDir) > 0.99999)
            {
                thrusters[Base6Directions.Direction.Forward].ForEach(t => t.ThrustOverridePercentage = 1);
                navRef.DampenersOverride = true;
            }
            else
            {
                thrusters[Base6Directions.Direction.Forward].ForEach(t => t.ThrustOverridePercentage = 0);
                navRef.DampenersOverride = false;
            }
        }

        void RetroCruiseStage(Vector3D shipVelocity)
        {
            aimDir = -shipVelocity;
            thrusters[Base6Directions.Direction.Forward].ForEach(t => t.ThrustOverridePercentage = 0);
            navRef.DampenersOverride = false;

            aim.Orient(aimDir, gyro, navRef.WorldMatrix);
        }

        void RetroDecelStage(Vector3D shipVelocity)
        {
            aimDir = -shipVelocity;
            aim.Orient(aimDir, gyro, navRef.WorldMatrix);
            navRef.DampenersOverride = true;
        }

        void RetroDecel(bool decelerate)
        {
            var aimDir = -navRef.GetShipVelocities().LinearVelocity;
            aim.Orient(aimDir, gyro, navRef.WorldMatrix);

            if (decelerate)
            {
                navRef.DampenersOverride = GetAccuracy(aimDir) > 0.99999;
            }

            if (aimDir.LengthSquared() < 0.1 * 0.1)
            {
                RetroCruiseReset();
            }
        }

        double GetAccuracy(Vector3D aimDir)
        {
            return Vector3D.Dot(navRef.WorldMatrix.Forward, Vector3D.Normalize(aimDir));
        }
    }
}
