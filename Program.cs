using IngameScript.Navigation;
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
    public enum NavModeEnum //legacy: all future modes should be a class derived from ICruiseController
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

        //Config is in the CustomData

        const string debugLcdName = "debugLcd";

        #endregion mdk preserve

        public event Action Update1 = null;
        public event Action Update10 = null;

        public NavModeEnum NavMode { get; set; }
        public bool IsNavIdle => NavMode == NavModeEnum.None;

        private readonly Dictionary<Direction, List<IMyThrust>> thrusters = new Dictionary<Direction, List<IMyThrust>>
        {
            { Direction.Forward, new List<IMyThrust>() },
            { Direction.Backward, new List<IMyThrust>() },
            { Direction.Right, new List<IMyThrust>() },
            { Direction.Left, new List<IMyThrust>() },
            { Direction.Up, new List<IMyThrust>() },
            { Direction.Down, new List<IMyThrust>() },
        };
        private List<IMyGyro> gyros = new List<IMyGyro>();
        private IMyCockpit controller;

        public static StringBuilder debug = new StringBuilder();
        private IMyTextSurface debugLcd;
        private IMyTextSurface consoleLcd;

        private IAimController aimController;
        private Profiler profiler;
        private WcPbApi wcApi;
        private bool wcApiActive = false;
        private ICruiseController cruiseController;

        private readonly DateTime bootTime;
        public const string programName = "NavOS";
        public const string versionStr = "2.7";
        public static VersionInfo versionInfo = new VersionInfo(2, 7, 0);

        private Config config;

        public Program()
        {
            LoadCustomDataConfig();

            Runtime.UpdateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update10;
            bootTime = DateTime.UtcNow;

            aimController = new JitAim(Me.CubeGrid.GridSizeEnum);
            profiler = new Profiler(this);
            wcApi = new WcPbApi();

            try { wcApiActive = wcApi.Activate(Me); }
            catch { wcApiActive = false; }

            UpdateBlocks();
            Abort();
        }

        private void SaveCustomDataConfig()
        {
            Me.CustomData = config.ToString();
        }

        private void LoadCustomDataConfig()
        {
            if (!Config.TryParse(Me.CustomData, out config))
            {
                config = Config.Default;
                SaveCustomDataConfig();
            }
        }

        public void Main(string argument, UpdateType updateSource)
        {
            profiler.Run();

            HandleArgs(argument);

            if (debugLcd != null)
            {
                debugLcd.WriteText(debug.ToString());
            }

            Update1?.Invoke();

            if (!IsNavIdle && cruiseController != null)
            {
                cruiseController.Run();
            }

            if ((updateSource & UpdateType.Update10) != 0)
            {
                Update10?.Invoke();

                WritePbOutput();
            }
        }

        private void Abort()
        {
            cruiseController?.Abort();

            DisableThrustOverrides();
            DisableGyroOverrides();
        }

        private void CruiseTerminated(ICruiseController source, string reason)
        {
            cruiseController = null;
            NavMode = NavModeEnum.None;

            optionalInfo = $"{source.Name} Terminated.\nReason: {reason}";
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

        private void DisableGyroOverrides()
        {
            foreach (var gyro in gyros)
            {
                gyro.GyroOverride = false;
                gyro.Pitch = 0;
                gyro.Yaw = 0;
                gyro.Roll = 0;
            }
        }

        private void UpdateBlocks()
        {
            foreach (var list in thrusters.Values)
            {
                list.Clear();
            }

            var controllers = new List<IMyCockpit>();
            GridTerminalSystem.GetBlocksOfType(controllers, b => b.CustomName.Contains(config.ShipControllerTag) && b.IsSameConstructAs(Me));
            if (controllers.Count == 0)
                throw new Exception($"No cockpit with \"{config.ShipControllerTag}\" found!");
            else controller = controllers[0];


            var tempThrusters = new List<IMyThrust>();
            var thrustBlockGroup = GridTerminalSystem.GetBlockGroupWithName(config.ThrustGroupName);
            if (thrustBlockGroup != null)
                thrustBlockGroup.GetBlocksOfType<IMyThrust>(tempThrusters, i => i.IsSameConstructAs(Me));
            else
                GridTerminalSystem.GetBlocksOfType<IMyThrust>(tempThrusters, i => i.IsSameConstructAs(Me));

            if (tempThrusters.Count == 0)
                throw new Exception("bruh, this ship's got no thrusters!!");

            foreach (var thruster in tempThrusters)
            {
                switch (GetBlockDirection(thruster.WorldMatrix.Forward, controller.WorldMatrix))
                {
                    case Direction.Backward:
                        thrusters[Direction.Forward].Add(thruster); break;
                    case Direction.Forward:
                        thrusters[Direction.Backward].Add(thruster); break;
                    case Direction.Left:
                        thrusters[Direction.Right].Add(thruster); break;
                    case Direction.Right:
                        thrusters[Direction.Left].Add(thruster); break;
                    case Direction.Down:
                        thrusters[Direction.Up].Add(thruster); break;
                    case Direction.Up:
                        thrusters[Direction.Down].Add(thruster); break;
                }
            }

            var gyroBlockGroup = GridTerminalSystem.GetBlockGroupWithName(config.GyroGroupName);
            if (gyroBlockGroup != null)
                gyroBlockGroup.GetBlocksOfType<IMyGyro>(gyros, i => i.IsSameConstructAs(Me) && i.IsFunctional);
            else
                GridTerminalSystem.GetBlocksOfType<IMyGyro>(gyros, i => i.IsSameConstructAs(Me) && i.IsFunctional);

            if (gyros.Count == 0)
                throw new Exception("No gyros");

            debugLcd = TryGetBlockWithName<IMyTextSurfaceProvider>(debugLcdName)?.GetSurface(0);
            consoleLcd = TryGetBlockWithName<IMyTextSurfaceProvider>(config.ConsoleLcdName)?.GetSurface(0);
        }

        private T TryGetBlockWithName<T>(string name) where T : class
        {
            IMyTerminalBlock block = GridTerminalSystem.GetBlockWithName(name);

            if (block == null || !(block is T))
            {
                return default(T);
            }

            return (T)block;
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
                throw new Exception();
        }

        private StringBuilder pbOut = new StringBuilder();
        public static string optionalInfo = "";

        private void WritePbOutput()
        {
            //PB Output
            const string programInfoStr = programName + " v" + versionStr + " | ";
            const string commandStr = @"
-- Commands --
Cruise <Speed> <distance>
Cruise <Speed> <X:Y:Z>
Retro
Match
Abort
Reload (the config)
";
            string avgRtStr = profiler.RunningAverageMs.ToString("0.0000");

            pbOut.Append(programInfoStr).Append(avgRtStr);
            TimeSpan upTime = DateTime.UtcNow - bootTime;
            pbOut.Append("\nUptime: ").AppendLine(SecondsToDuration(upTime.TotalSeconds));

            if (optionalInfo.Length > 0)
            {
                pbOut.AppendLine();
                pbOut.AppendLine(optionalInfo);
            }

            pbOut.Append("\n-- Loaded Config --\n");
            pbOut.Append(nameof(config.MaxThrustOverrideRatio)).Append('=').Append(config.MaxThrustOverrideRatio).AppendLine();
            pbOut.Append(nameof(config.ShipControllerTag)).Append('=').AppendLine(config.ShipControllerTag);
            pbOut.Append(nameof(config.ThrustGroupName)).Append('=').AppendLine(config.ThrustGroupName);
            pbOut.Append(nameof(config.GyroGroupName)).Append('=').AppendLine(config.GyroGroupName);
            pbOut.Append(nameof(config.ConsoleLcdName)).Append('=').AppendLine(config.ConsoleLcdName);

            pbOut.Append("\n-- Nav Info --");
            pbOut.Append("\nNavMode: ").Append(NavMode.ToString());
            pbOut.Append("\nDebug: ").Append(debugLcd != null);
            pbOut.AppendLine();

            cruiseController?.AppendStatus(pbOut);

            consoleLcd?.WriteText(pbOut);

            pbOut.Append(commandStr);

            pbOut.Append("\n-- Detected Blocks --\n");
            pbOut.Append(thrusters[Direction.Forward].Count).Append(" Forward Thrusters\n");
            pbOut.Append(thrusters[Direction.Backward].Count).Append(" Backward Thrusters\n");
            pbOut.Append(thrusters[Direction.Right].Count).Append(" Right Thrusters\n");
            pbOut.Append(thrusters[Direction.Left].Count).Append(" Left Thrusters\n");
            pbOut.Append(thrusters[Direction.Up].Count).Append(" Up Thrusters\n");
            pbOut.Append(thrusters[Direction.Down].Count).Append(" Down Thrusters\n");
            pbOut.Append(gyros.Count).Append(" Gyros\n");

            pbOut.Append("\n-- Runtime Information --");
            pbOut.Append("\nLast Runtime: ").Append(Runtime.LastRunTimeMs);
            pbOut.Append("\nAverage Runtime: ").Append(avgRtStr);
            pbOut.Append("\nMax Runtime: ").Append(profiler.MaxRuntimeMsFast);
            //pbOut.Append("\n\nNavOS by StarCpt");

            Echo(pbOut.ToString());

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
