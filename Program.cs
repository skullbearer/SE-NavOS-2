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
    public enum CruiseStageEnum : byte
    {
        None = 0,
        Acceleleration = 1,
        Cruise = 2,
        Deceleration = 3,
    }

    partial class Program : MyGridProgram
    {
        #region mdk preserve
        //objectively the better cruise mode
        const bool useRetroCruise = true;

        const string mainControllerTag = "[Nav]";
        const string forwardThrustGroupOverride = "ForwardThrust";

        #endregion mdk preserve
        private readonly Dictionary<Base6Directions.Direction, List<IMyThrust>> thrusters = new Dictionary<Base6Directions.Direction, List<IMyThrust>>
        {
            { Base6Directions.Direction.Forward, new List<IMyThrust>() },
            { Base6Directions.Direction.Backward, new List<IMyThrust>() },
            { Base6Directions.Direction.Right, new List<IMyThrust>() },
            { Base6Directions.Direction.Left, new List<IMyThrust>() },
            { Base6Directions.Direction.Up, new List<IMyThrust>() },
            { Base6Directions.Direction.Down, new List<IMyThrust>() },
        };
        readonly List<IMyGyro> gyros = new List<IMyGyro>();
        IMyCockpit navRef;
        IMyGyro gyro;

        bool retro = false;
        bool retroDecel = false;
        bool cruiseActive = false;
        CruiseStageEnum cruiseStage = CruiseStageEnum.None;
        double cruiseDist = 0;
        double cruiseSpeed = 0;
        bool speedMatch = false;

        Vector3D startPos;
        double ETA = 0;

        double remainingDist = 0;
        float foreThrust = 0;
        float backThrust = 0;

        readonly DateTime bootTime;
        const string versionInfo = "2.3";

        IAimController aim;
        Profiler profiler;
        WcPbApi wcApi;
        bool wcApiActive = false;

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update10;
            bootTime = DateTime.UtcNow;

            aim = new JitAim(Me.CubeGrid.GridSizeEnum);
            profiler = new Profiler(this);
            wcApi = new WcPbApi();

            try { wcApiActive = wcApi.Activate(Me); }
            catch { wcApiActive = false; }

            UpdateBlocks();
        }

        public void Main(string argument, UpdateType updateSource)
        {
            profiler.Run();

            if (argument.Length > 0)
                HandleArgs(argument);

            if ((updateSource & UpdateType.Update10) != 0)
            {
                WritePbOutput();

                if (speedMatch)
                {
                    SpeedMatch();
                }
            }

            if (cruiseActive)
            {
                if (!useRetroCruise)
                {
                    Cruise();
                }
                else
                {
                    RetroCruiseControl();
                }
            }
            else if (retro)
            {
                RetroDecel(retroDecel);
            }
        }

        void SpeedMatch()
        {
            if (!wcApiActive)
            {
                try { wcApiActive = wcApi.Activate(Me); }
                catch { wcApiActive = false; }
            }
            
            if (wcApiActive)
            {
                MyDetectedEntityInfo? target = wcApi.GetAiFocus(Me.CubeGrid.EntityId);
                if (target.HasValue)
                {
                    Vector3D relativeSpeed = target.Value.Velocity - navRef.GetShipVelocities().LinearVelocity;
                    if (Vector3D.Angle(relativeSpeed, navRef.WorldMatrix.Forward) > Math.PI / 2)
                    {
                        float dot = Vector3.Dot(relativeSpeed, navRef.WorldMatrix.Forward);
                        thrusters[Base6Directions.Direction.Backward].ForEach(t => t.ThrustOverridePercentage = dot);
                        thrusters[Base6Directions.Direction.Forward].ForEach(t => t.ThrustOverridePercentage = 0);
                    }
                    else
                    {
                        float dot = Vector3.Dot(relativeSpeed, navRef.WorldMatrix.Backward);
                        thrusters[Base6Directions.Direction.Forward].ForEach(t => t.ThrustOverridePercentage = dot);
                        thrusters[Base6Directions.Direction.Backward].ForEach(t => t.ThrustOverridePercentage = 0);
                    }

                    if (Vector3D.Angle(relativeSpeed, navRef.WorldMatrix.Right) > Math.PI / 2)
                    {
                        float dot = Vector3.Dot(relativeSpeed, navRef.WorldMatrix.Right);
                        thrusters[Base6Directions.Direction.Left].ForEach(t => t.ThrustOverridePercentage = dot);
                        thrusters[Base6Directions.Direction.Right].ForEach(t => t.ThrustOverridePercentage = 0);
                    }
                    else
                    {
                        float dot = Vector3.Dot(relativeSpeed, navRef.WorldMatrix.Left);
                        thrusters[Base6Directions.Direction.Right].ForEach(t => t.ThrustOverridePercentage = dot);
                        thrusters[Base6Directions.Direction.Left].ForEach(t => t.ThrustOverridePercentage = 0);
                    }

                    if (Vector3D.Angle(relativeSpeed, navRef.WorldMatrix.Up) > Math.PI / 2)
                    {
                        float dot = Vector3.Dot(relativeSpeed, navRef.WorldMatrix.Up);
                        thrusters[Base6Directions.Direction.Down].ForEach(t => t.ThrustOverridePercentage = dot);
                        thrusters[Base6Directions.Direction.Up].ForEach(t => t.ThrustOverridePercentage = 0);
                    }
                    else
                    {
                        float dot = Vector3.Dot(relativeSpeed, navRef.WorldMatrix.Down);
                        thrusters[Base6Directions.Direction.Up].ForEach(t => t.ThrustOverridePercentage = dot);
                        thrusters[Base6Directions.Direction.Down].ForEach(t => t.ThrustOverridePercentage = 0);
                    }
                }
            }
        }

        Vector3D ParseCustomDataGPS()
        {
            try
            {
                string[] cdStr = Me.CustomData.Split(':');
                return new Vector3D(double.Parse(cdStr[2]), double.Parse(cdStr[3]), double.Parse(cdStr[4]));
            }
            catch
            {
                return Vector3D.Zero;
            }
        }

        void HandleArgs(string argument)
        {
            string[] arg = argument.ToLower().Split(' ');

            if (arg[0] == "cruise" && !retro)
            {
                try
                {
                    if (arg[1] == "gps")
                    {
                        cruiseSpeed = double.Parse(arg[2]);
                        double offsetDist;
                        if (double.TryParse(arg[3], out offsetDist))
                        {
                            Vector3D dest = ParseCustomDataGPS();
                            if (!dest.IsZero())
                            {
                                retroCruiseDestination = dest;
                                retroOffset = offsetDist;
                                optionalInfo = retroCruiseDestination.ToString();

                                cruiseActive = true;
                                RetroCruiseControl(true);
                            }
                            else throw new Exception();
                        }
                        else throw new Exception();
                    }
                    else
                    {
                        UpdateBlocks();
                        cruiseActive = true;
                        cruiseStage = (CruiseStageEnum)1;
                        cruiseDist = double.Parse(arg[1]);
                        cruiseSpeed = double.Parse(arg[2]);
                        startPos = Me.CubeGrid.GetPosition();

                        foreThrust = thrusters[Base6Directions.Direction.Forward].FindAll(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);
                        backThrust = thrusters[Base6Directions.Direction.Backward].FindAll(t => t.IsWorking).Sum(t => t.MaxEffectiveThrust);

                        RetroCruiseControl(true);
                    }

                    optionalInfo = "";
                }
                catch
                {
                    cruiseActive = false;
                    optionalInfo = "dist or speed invalid";
                }
            }
            else if ((arg[0] == "retro" || arg[0] == "decel") && !cruiseActive)
            {
                retro = true;
                if (arg.Length > 1)
                {
                    retroDecel = arg[1].Equals("true", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    retroDecel = false;
                }
            }
            else if (argument.ToLower().Contains("abort"))
            {
                SysReset();
                RetroCruiseReset();
                DisableThrustOverrides();
                cruiseActive = false;
                retro = false;
                speedMatch = false;
                optionalInfo = "cruise aborted";
            }
            else if (arg[0].Equals("match", StringComparison.OrdinalIgnoreCase))
            {
                if (wcApiActive)
                {
                    speedMatch = true;
                }
            }
        }

        private void DisableThrustOverrides()
        {
            foreach (var list in thrusters.Values)
            {
                foreach (var thruster in list)
                {
                    thruster.ThrustOverridePercentage = 0;
                }
            }
        }

        void SysReset()
        {
            if (cruiseActive || retroDecel)
            {
                navRef.DampenersOverride = true;
            }
            DisableThrustOverrides();
            thrusters[Base6Directions.Direction.Backward].ForEach(t => t.Enabled = true);
            gyros.ForEach(g => { g.Pitch = 0f; g.Roll = 0f; g.Yaw = 0f; g.GyroOverride = false; });
            cruiseActive = false;
            cruiseStage = 0;
            cruiseDist = 0;
            cruiseSpeed = 0;
            ETA = 0;
            startPos = Vector3D.Zero;

            remainingDist = 0;
            foreThrust = 0;
            backThrust = 0;
        }

        void UpdateBlocks()
        {
            thrusters[Base6Directions.Direction.Forward].Clear();
            thrusters[Base6Directions.Direction.Backward].Clear();

            var controllers = new List<IMyCockpit>();
            GridTerminalSystem.GetBlocksOfType(controllers, b => b.CustomName.ToLower().Contains(mainControllerTag.ToLower()) && b.IsSameConstructAs(Me));
            if (controllers.Count == 0)
                throw new Exception($"No cockpit with \"{mainControllerTag}\" found!");
            else navRef = controllers.First();


            var tempThrusters = new List<IMyThrust>();
            try
            {
                GridTerminalSystem.GetBlockGroupWithName(forwardThrustGroupOverride).GetBlocksOfType(tempThrusters, b => b.IsSameConstructAs(Me));
            }
            catch
            {
                GridTerminalSystem.GetBlocksOfType(tempThrusters, b => b.IsSameConstructAs(Me));
            }
            if (tempThrusters.Count == 0)
                throw new Exception("bruh, this ship's got no thrusters!!");
            foreach (var thruster in tempThrusters)
            {
                switch (GetBlockDirection(thruster.WorldMatrix.Forward, navRef.WorldMatrix))
                {
                    case Base6Directions.Direction.Backward:
                        thrusters[Base6Directions.Direction.Forward].Add(thruster); break;
                    case Base6Directions.Direction.Forward:
                        thrusters[Base6Directions.Direction.Backward].Add(thruster); break;
                    case Base6Directions.Direction.Left:
                        thrusters[Base6Directions.Direction.Right].Add(thruster); break;
                    case Base6Directions.Direction.Right:
                        thrusters[Base6Directions.Direction.Left].Add(thruster); break;
                    case Base6Directions.Direction.Down:
                        thrusters[Base6Directions.Direction.Up].Add(thruster); break;
                    case Base6Directions.Direction.Up:
                        thrusters[Base6Directions.Direction.Down].Add(thruster); break;
                }
            }

            GridTerminalSystem.GetBlocksOfType(gyros, b => b.IsSameConstructAs(Me) && b.IsFunctional);
            gyro = gyros.First();

            pbLCD = Me.GetSurface(0);
        }

        public static Base6Directions.Direction GetBlockDirection(Vector3D vector, MatrixD refMatrix)
        {
            if (vector == refMatrix.Forward)
                return Base6Directions.Direction.Forward;
            else if (vector == refMatrix.Backward)
                return Base6Directions.Direction.Backward;
            else if (vector == refMatrix.Right)
                return Base6Directions.Direction.Right;
            else if (vector == refMatrix.Left)
                return Base6Directions.Direction.Left;
            else if (vector == refMatrix.Up)
                return Base6Directions.Direction.Up;
            else if (vector == refMatrix.Down)
                return Base6Directions.Direction.Down;
            else
                throw new Exception("GetBlockDirection nocase");
        }

        StringBuilder pbOut = new StringBuilder();
        string optionalInfo = "";
        IMyTextSurface pbLCD;
        void WritePbOutput()
        {
            //PB Output
            pbOut.AppendLine($"NavOS v{versionInfo} | Avg: {profiler.RunningAverageMs:0.0000}");
            TimeSpan upTime = DateTime.UtcNow - bootTime;
            pbOut.AppendLine($"Uptime: {SecondsToDuration(upTime.TotalSeconds)}\n");

            pbOut.Append(optionalInfo);

            pbOut.AppendLine("\n-- Nav Info --");
            pbOut.AppendLine($"Cruise: {cruiseActive}");
            pbOut.AppendLine($"Cruise Mode: {(useRetroCruise ? "RetroCruise" : "Normal")}");
            pbOut.AppendLine($"Retro: {retro}");
            pbOut.AppendLine($"RetroDecel: {retroDecel}");
            pbOut.AppendLine($"Match: {speedMatch}");
            if (cruiseActive)
            {
                if (useRetroCruise)
                {
                    pbOut.AppendLine($"Offset: {retroOffset}");
                }
                pbOut.AppendLine($"Stage: {cruiseStage}");
                pbOut.AppendLine($"ETA: {SecondsToDuration(ETA)}");
                pbOut.AppendLine($"Remaining Dist: {remainingDist:0}");
                pbOut.AppendLine($"Speed: {cruiseSpeed}");
                pbOut.AppendLine($"Dist: {cruiseDist}");
                pbOut.AppendLine($"Accuracy: {GetAccuracy(aimDir):f7}");
            }

            pbOut.AppendLine("\n-- Cruise Config --");
            pbOut.AppendLine("Cruise <dist> <speed>");
            //offset = option to stop short by x meters
            pbOut.AppendLine("Cruise GPS <speed> <offset>");
            pbOut.AppendLine("Abort");

            pbOut.AppendLine($"\n-- Detected Blocks --");
            pbOut.AppendLine($"- {thrusters[Base6Directions.Direction.Forward].Count} Forward Thrusters");
            pbOut.AppendLine($"- {thrusters[Base6Directions.Direction.Backward].Count} Backward Thrusters");
            pbOut.AppendLine($"- {thrusters[Base6Directions.Direction.Right].Count} Right Thrusters");
            pbOut.AppendLine($"- {thrusters[Base6Directions.Direction.Left].Count} Left Thrusters");
            pbOut.AppendLine($"- {thrusters[Base6Directions.Direction.Up].Count} Up Thrusters");
            pbOut.AppendLine($"- {thrusters[Base6Directions.Direction.Down].Count} Down Thrusters");
            pbOut.AppendLine($"- {gyros.Count} Gyro{(gyros.Count != 1 ? "s" : "")}");

            pbOut.AppendLine("\n-- Runtime Information --");
            pbOut.AppendLine($"Last Runtime: {Runtime.LastRunTimeMs}");
            pbOut.AppendLine($"Average Runtime: {profiler.RunningAverageMs:0.0000}");
            pbOut.AppendLine($"Max Runtime: {profiler.MaxRuntimeMsFast}");
            pbOut.AppendLine("\nWritten by StarCpt");

            Echo(pbOut.ToString());
            pbLCD?.WriteText(pbOut);

            pbOut.Clear();
        }

        public static string SecondsToDuration(double seconds, bool fractions = false)
        {
            if (double.IsNaN(seconds))
                return "NaN";

            if (double.IsInfinity(seconds))
                return "Infinity";

            seconds = Math.Abs(seconds);

            int hours = (int)seconds / 3600;

            seconds %= 3600;

            int minutes = (int)seconds / 60;

            seconds %= 60;

            if (hours > 0) return $"{hours.ToString("00")}:{minutes.ToString("00")}:{seconds.ToString("00")}{(fractions ? (seconds - (int)seconds).ToString(".000") : "")}";
            else return $"{minutes.ToString("00")}:{seconds.ToString("00")}";
        }
    }
}
