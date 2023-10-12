using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript
{
    public class Journey : ICruiseController
    {
        public event CruiseTerminateEventDelegate CruiseTerminated = delegate { };

        public string Name => nameof(Journey);

        IMyShipController shipController;
        IAimController aimControl;
        IList<IMyGyro> gyros;
        double decelStartMarginSeconds;
        IVariableThrustController thrustControl;
        Program prog;

        private RetroCruiseControl cruiseControl;
        private List<Waypoint> waypoints;
        private bool started = false;
        private int currentStep = 0;

        public Journey(
            IAimController aimControl,
            IMyShipController controller,
            IList<IMyGyro> gyros,
            double decelStartMarginSeconds,
            IVariableThrustController thrustControl,
            Program program)
        {
            this.aimControl = aimControl;
            this.shipController = controller;
            this.gyros = gyros;
            this.decelStartMarginSeconds = decelStartMarginSeconds;
            this.thrustControl = thrustControl;
            this.prog = program;

            waypoints = new List<Waypoint>();
        }

        public Journey(
            IAimController aimControl,
            IMyShipController controller,
            IList<IMyGyro> gyros,
            double decelStartMarginSeconds,
            IVariableThrustController thrustControl,
            Program program,
            List<Waypoint> waypoints,
            int step)
            : this(aimControl, controller, gyros, decelStartMarginSeconds, thrustControl, program)
        {
            this.waypoints = waypoints;
            InitStep(step);
        }

        public void AppendStatus(StringBuilder strb)
        {
            strb.Append("\n-- Journey Status --");
            if (!started)
                strb.Append("\nRecording Waypoints...\nCommands:\nJourney Add <Speed> <GPS>\nJourney Remove <Num>\nJourney Start\n\nWaypoints:");
            else
                strb.Append("\nCurrent Step:").Append(currentStep + 1).Append("\nWaypoints:");
            for (int i = 0; i < waypoints.Count; i++)
            {
                var step = waypoints[i];
                strb.Append("\n\n#").Append(i + 1).Append(": ").Append(step.Name)
                    .Append($"\nTarget: X:{step.Target.X:0.0} Y:{step.Target.Y:0.0} Z:{step.Target.Z:0.0}")
                    .Append("\nSpeed: ").Append(step.DesiredSpeed);
            }
            strb.Append('\n');
            if (started)
                cruiseControl.AppendStatus(strb);
        }

        public bool HandleJourneyCommand(string[] args, string fullCmdStr, out string failReason)
        {
            failReason = "Unknown Journey Command!";

            if (args.Length < 2)
            {
                return false;
            }
            else if (started)
            {
                failReason = "Cannot run Journey commands once started";
                return false;
            }

            //all commands:
            //journey init - handled elsewhere
            //journey add <speed> <gps>
            //journey remove <num>
            //journey start
            //journey pause - abort while preserving the sequence. not added yet

            if (args[1] == "add" && args.Length >= 4)
            {
                string name;
                double speed;
                Vector3D target;
                if (!double.TryParse(args[2], out speed) || !Utils.TryParseGps(fullCmdStr, out name, out target))
                {
                    failReason = "Invalid arguments";
                    return false;
                }

                waypoints.Add(new Waypoint(name, speed, target));
                return true;
            }
            else if (args[1] == "remove" && args.Length >= 3)
            {
                int num;
                if (!int.TryParse(args[2], out num))
                {
                    failReason = "Couldn't parse removal number";
                    return false;
                }
                else if (num < 1 || num > waypoints.Count)
                {
                    failReason = "Number is out of range";
                    return false;
                }

                waypoints.RemoveAt(--num);
                return true;
            }
            else if (args[1] == "start")
            {
                if (waypoints.Count == 0)
                {
                    failReason = "No waypoints set!";
                    return false;
                }

                InitStep(0);
                return true;
            }

            return false;
        }
            
        private void InitStep(int index)
        {
            var step = waypoints[index];
            Vector3D targetOffset = Vector3D.Zero;

            if (index == waypoints.Count - 1)
            {
                if (prog.config.CruiseOffsetSideDist > 0)
                {
                    targetOffset += Vector3D.CalculatePerpendicularVector(step.Target - shipController.GetPosition()) * prog.config.CruiseOffsetSideDist;
                }
            }

            started = true;
            currentStep = index;
            cruiseControl = new RetroCruiseControl(step.Target + targetOffset, step.DesiredSpeed, aimControl, shipController, gyros, thrustControl)
            {
                decelStartMarginSeconds = this.decelStartMarginSeconds,
            };
            cruiseControl.CruiseTerminated += OnCruiseTerminated;
            SavePersistantData();
        }

        private void SavePersistantData()
        {
            prog.config.PersistStateData = $"{NavModeEnum.Journey}|{currentStep}";
            StringBuilder strb = new StringBuilder();
            for (int i = 0;;)
            {
                strb.Append(waypoints[i].ToString());
                i++;
                if (i < waypoints.Count)
                    strb.Append('|');
                else
                    break;
            }
            prog.Me.CustomData = prog.config.ToString();
            prog.SetStorage(strb.ToString());
        }

        public void Run()
        {
            if (started)
            {
                cruiseControl.Run();
            }
        }

        private void OnCruiseTerminated(ICruiseController sender, string reason)
        {
            if (reason == "No functional gyros found")
            {
                Terminate(reason);
            }
            else if (Vector3D.DistanceSquared(shipController.GetPosition(), waypoints[currentStep].Target) <= 100 * 100)
            {
                currentStep++;
                if (currentStep < waypoints.Count)
                    InitStep(currentStep);
                else
                    Terminate("Destination Reached");
            }
            else
            {
                Terminate($"Journey terminated unexpectedly." +
                    $"\nRetroCruise terminate reason: {reason}" +
                    $"\nStep: {currentStep + 1} of {waypoints.Count}" +
                    $"\nDistanceToCurrentTarget: {Vector3D.Distance(shipController.GetPosition(), waypoints[currentStep].Target):0.00}" +
                    $"\nCurrent Speed: {shipController.GetShipSpeed():0.00}");
            }
        }

        public void Abort() => Terminate("Aborted");

        public void Terminate(string reason)
        {
            thrustControl.ResetThrustOverrides();
            if (cruiseControl != null)
            {
                cruiseControl.TurnOnAllThrusters();
                cruiseControl.ResetGyroOverride();
                cruiseControl = null;
            }
            CruiseTerminated.Invoke(this, reason);
        }

        public static bool TryParseWaypoints(string persistData, string storage, out List<Waypoint> waypoints, out int step)
        {
            waypoints = null;
            step = 0;
            try
            {
                step = int.Parse(persistData.Split('|')[1]);
                string[] steps = storage.Split('|');
                if (steps.Length == 0 || step < 0 || step > steps.Length - 1)
                    return false;
                var instructions = new List<Waypoint>();
                for (int i = 0; i < steps.Length; i++)
                {
                    string[] args = steps[i].Split('/');
                    Vector3D target;
                    double speed;
                    if (!double.TryParse(args[1], out speed) || !Vector3D.TryParse(args[2], out target))
                        return false;
                    instructions.Add(new Waypoint(args[0], speed, target));
                }
                waypoints = instructions;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public struct Waypoint
        {
            public string Name;
            public double DesiredSpeed;
            public Vector3D Target;

            public Waypoint(string name, double desiredSpeed, Vector3D target)
            {
                Name = name;
                DesiredSpeed = desiredSpeed;
                Target = target;
            }

            public override string ToString() => $"{Name}/{DesiredSpeed}/{Target}";
        }
    }
}
