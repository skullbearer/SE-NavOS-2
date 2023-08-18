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

    public enum NavModeEnum
    {
        None = 0,
        Cruise = 1,
        Retro = 2, Retrograde = 2,
        Prograde = 3,
        SpeedMatch = 4,
    }

    public enum Direction : byte
    {
        Forward,
        Backward,
        Left,
        Right,
        Up,
        Down
    }

    public partial class Program : MyGridProgram
    {
        #region mdk preserve

        const string shipControllerTag = "Nav";
        //if empty or group doesn't exist uses all thrusters on the ship
        const string thrustGroup = "NavThrust";

        const string debugLcdName = "debugLcd";

        #endregion mdk preserve

        public event Action<NavModeEnum, NavModeEnum> NavModeChanged = delegate { };
        public event Action Update1 = null;
        public event Action Update10 = null;

        public NavModeEnum NavMode
        {
            get { return _navMode; }
            set
            {
                if (_navMode != value)
                {
                    var old = _navMode;
                    _navMode = value;
                    NavModeChanged.Invoke(old, value);
                }
            }
        }
        public bool IsNavIdle => NavMode == NavModeEnum.None;

        private readonly Dictionary<Direction, List<IMyThrust>> Thrusters = new Dictionary<Direction, List<IMyThrust>>
        {
            { Direction.Forward, new List<IMyThrust>() },
            { Direction.Backward, new List<IMyThrust>() },
            { Direction.Right, new List<IMyThrust>() },
            { Direction.Left, new List<IMyThrust>() },
            { Direction.Up, new List<IMyThrust>() },
            { Direction.Down, new List<IMyThrust>() },
        };
        readonly List<IMyGyro> gyros = new List<IMyGyro>();
        IMyCockpit controller;

        private NavModeEnum _navMode = NavModeEnum.None;

        public static StringBuilder debug = new StringBuilder();
        IMyTextPanel debugLcd;

        readonly DateTime bootTime;
        const string programName = "NavOS";
        const string versionInfo = "2.5";

        IAimController aim;
        Profiler profiler;
        WcPbApi wcApi;
        bool wcApiActive = false;
        ICruiseController cruise;

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

            if (debugLcd != null)
            {
                debugLcd.WriteText(debug.ToString());
            }

            if (argument.Length > 0)
                HandleArgs(argument);

            Update1?.Invoke();

            if ((updateSource & UpdateType.Update10) != 0)
            {
                Update10?.Invoke();

                WritePbOutput();

                if (NavMode == NavModeEnum.SpeedMatch)
                {
                    SpeedMatch();
                }
            }

            if (NavMode == NavModeEnum.Cruise && cruise != null)
            {
                cruise.Run();
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
                    Vector3D relativeSpeed = target.Value.Velocity - controller.GetShipVelocities().LinearVelocity;
                    if (Vector3D.Angle(relativeSpeed, controller.WorldMatrix.Forward) > Math.PI / 2)
                    {
                        float dot = Vector3.Dot(relativeSpeed, controller.WorldMatrix.Forward);
                        Thrusters[Direction.Backward].ForEach(t => t.ThrustOverridePercentage = dot);
                        Thrusters[Direction.Forward].ForEach(t => t.ThrustOverridePercentage = 0);
                    }
                    else
                    {
                        float dot = Vector3.Dot(relativeSpeed, controller.WorldMatrix.Backward);
                        Thrusters[Direction.Forward].ForEach(t => t.ThrustOverridePercentage = dot);
                        Thrusters[Direction.Backward].ForEach(t => t.ThrustOverridePercentage = 0);
                    }

                    if (Vector3D.Angle(relativeSpeed, controller.WorldMatrix.Right) > Math.PI / 2)
                    {
                        float dot = Vector3.Dot(relativeSpeed, controller.WorldMatrix.Right);
                        Thrusters[  Direction.Left].ForEach(t => t.ThrustOverridePercentage = dot);
                        Thrusters[Direction.Right].ForEach(t => t.ThrustOverridePercentage = 0);
                    }
                    else
                    {
                        float dot = Vector3.Dot(relativeSpeed, controller.WorldMatrix.Left);
                        Thrusters[Direction.Right].ForEach(t => t.ThrustOverridePercentage = dot);
                        Thrusters[Direction.Left].ForEach(t => t.ThrustOverridePercentage = 0);
                    }

                    if (Vector3D.Angle(relativeSpeed, controller.WorldMatrix.Up) > Math.PI / 2)
                    {
                        float dot = Vector3.Dot(relativeSpeed, controller.WorldMatrix.Up);
                        Thrusters[Direction.Down].ForEach(t => t.ThrustOverridePercentage = dot);
                        Thrusters[Direction.Up].ForEach(t => t.ThrustOverridePercentage = 0);
                    }
                    else
                    {
                        float dot = Vector3.Dot(relativeSpeed, controller.WorldMatrix.Down);
                        Thrusters[Direction.Up].ForEach(t => t.ThrustOverridePercentage = dot);
                        Thrusters[Direction.Down].ForEach(t => t.ThrustOverridePercentage = 0);
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
            string[] args = argument.ToLower().Split(' ');


            if (args[0] == "cruise" && args.Length >= 3 && IsNavIdle)
            {
                try
                {
                    double desiredSpeed;
                    Vector3D target;

                    desiredSpeed = double.Parse(args[1]);

                    double result;
                    if (double.TryParse(args[2], out result))
                    {
                        target = controller.GetPosition() + (controller.WorldMatrix.Forward * result);
                    }
                    else
                    {
                        string[] coords = args[2].Split(':');

                        double x = double.Parse(coords[0]);
                        double y = double.Parse(coords[1]);
                        double z = double.Parse(coords[2]);

                        target = new Vector3D(x, y, z);
                    }

                    NavMode = NavModeEnum.Cruise;
                    cruise = new RetroCruiseControl(target, desiredSpeed, aim, controller, gyros[0], Thrusters);
                    cruise.CruiseCompleted += () => { cruise = null; NavMode = NavModeEnum.None; };
                }
                catch (Exception e)
                {
                    optionalInfo = e.ToString();
                }
            }
            //else if (args[0].Equals("match", StringComparison.OrdinalIgnoreCase) && IsNavIdle)
            //{
            //    if (wcApiActive)
            //    {
            //        NavMode = NavModeEnum.SpeedMatch;
            //    }
            //}
            else if (argument.ToLower().Contains("abort"))
            {
                DisableThrustOverrides();
                NavMode = NavModeEnum.None;
                optionalInfo = "cruise aborted";
                cruise?.Abort();
            }
        }

        private void DisableThrustOverrides()
        {
            foreach (var list in Thrusters.Values)
            {
                foreach (var thruster in list)
                {
                    thruster.ThrustOverridePercentage = 0;
                }
            }
        }

        void UpdateBlocks()
        {
            Thrusters[Direction.Forward].Clear();
            Thrusters[Direction.Backward].Clear();

            var controllers = new List<IMyCockpit>();
            GridTerminalSystem.GetBlocksOfType(controllers, b => b.CustomName.ToLower().Contains(shipControllerTag.ToLower()) && b.IsSameConstructAs(Me));
            if (controllers.Count == 0)
                throw new Exception($"No cockpit with \"{shipControllerTag}\" found!");
            else controller = controllers.First();


            var tempThrusters = new List<IMyThrust>();
            try
            {
                GridTerminalSystem.GetBlockGroupWithName(thrustGroup).GetBlocksOfType(tempThrusters, b => b.IsSameConstructAs(Me));
            }
            catch
            {
                GridTerminalSystem.GetBlocksOfType(tempThrusters, b => b.IsSameConstructAs(Me));
            }
            if (tempThrusters.Count == 0)
                throw new Exception("bruh, this ship's got no thrusters!!");
            foreach (var thruster in tempThrusters)
            {
                switch (GetBlockDirection(thruster.WorldMatrix.Forward, controller.WorldMatrix))
                {
                    case Direction.Backward:
                        Thrusters[Direction.Forward].Add(thruster); break;
                    case Direction.Forward:
                        Thrusters[Direction.Backward].Add(thruster); break;
                    case Direction.Left:
                        Thrusters[Direction.Right].Add(thruster); break;
                    case Direction.Right:
                        Thrusters[Direction.Left].Add(thruster); break;
                    case Direction.Down:
                        Thrusters[Direction.Up].Add(thruster); break;
                    case Direction.Up:
                        Thrusters[Direction.Down].Add(thruster); break;
                }
            }

            GridTerminalSystem.GetBlocksOfType(gyros, b => b.IsSameConstructAs(Me) && b.IsFunctional);

            pbLCD = Me.GetSurface(0);

            try
            {
                debugLcd = GridTerminalSystem.GetBlockWithName(debugLcdName) as IMyTextPanel;
            }
            catch
            {

            }
        }

        public static Direction GetBlockDirection(Vector3D vector, MatrixD refMatrix)
        {
            if (vector == refMatrix.Forward)
                return Direction.Forward;
            else if (vector == refMatrix.Backward)
                return Direction.Backward;
            else if (vector == refMatrix.Right)
                return Direction.Right;
            else if (vector == refMatrix.Left)
                return Direction.Left;
            else if (vector == refMatrix.Up)
                return Direction.Up;
            else if (vector == refMatrix.Down)
                return Direction.Down;
            else
                throw new Exception("GetBlockDirection");
        }

        StringBuilder pbOut = new StringBuilder();
        string optionalInfo = "";
        IMyTextSurface pbLCD;
        void WritePbOutput()
        {
            //PB Output
            pbOut.Append(programName + " v" + versionInfo + " | Avg: ").Append(profiler.RunningAverageMs.ToString("0.0000"));
            TimeSpan upTime = DateTime.UtcNow - bootTime;
            pbOut.Append("\nUptime: ").AppendLine(SecondsToDuration(upTime.TotalSeconds)).AppendLine();

            pbOut.Append(optionalInfo);

            pbOut.Append("\n-- Nav Info --");
            pbOut.Append("\nNavMode: ").Append(NavMode.ToString());
            pbOut.Append("\nDebug: ").Append(debugLcd != null).AppendLine();

            cruise?.AppendStatus(pbOut);

            //if (NavMode == NavModeEnum.Cruise)
            //{
            //    if (useRetroCruise)
            //    {
            //        pbOut.AppendLine($"Offset: {retroOffset}");
            //    }
            //    pbOut.AppendLine($"Stage: {cruiseStage}");
            //    pbOut.AppendLine($"ETA: {SecondsToDuration(ETA)}");
            //    pbOut.AppendLine($"Remaining Dist: {remainingDist:0}");
            //    pbOut.AppendLine($"Speed: {cruiseSpeed}");
            //    pbOut.AppendLine($"Dist: {cruiseDist}");
            //    pbOut.AppendLine($"Accuracy: {GetAccuracy(aimDir):f7}");
            //}

            pbOut.Append("\n-- Commands --\n");
            pbOut.Append("Cruise <Speed> <Distance>\n");
            pbOut.Append("Cruise <Speed> <X:Y:Z>\n");
            pbOut.Append("Abort\n");

            pbOut.Append("\n-- Detected Blocks --\n");
            pbOut.Append(Thrusters[Direction.Forward].Count).Append(" Forward Thrusters\n");
            pbOut.Append(Thrusters[Direction.Backward].Count).Append(" Backward Thrusters\n");
            pbOut.Append(Thrusters[Direction.Right].Count).Append(" Right Thrusters\n");
            pbOut.Append(Thrusters[Direction.Left].Count).Append(" Left Thrusters\n");
            pbOut.Append(Thrusters[Direction.Up].Count).Append(" Up Thrusters\n");
            pbOut.Append(Thrusters[Direction.Down].Count).Append(" Down Thrusters\n");
            pbOut.Append(gyros.Count).Append(" Gyros\n");

            pbOut.Append("\n-- Runtime Information --");
            pbOut.Append("\nLast Runtime: ").Append(Runtime.LastRunTimeMs);
            pbOut.Append("\nAverage Runtime: ").Append(profiler.RunningAverageMs.ToString("0.0000"));
            pbOut.Append("\nMax Runtime: ").Append(profiler.MaxRuntimeMsFast);
            pbOut.Append("\n\nWritten by StarCpt");

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
