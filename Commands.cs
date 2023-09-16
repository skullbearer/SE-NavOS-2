using IngameScript.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript
{
    partial class Program
    {
        private void HandleArgs(string argument)
        {
            if (argument.Length == 0)
            {
                return;
            }

            argument = argument.ToLower();

            if (argument.Contains("abort"))
            {
                AbortNav(false);
                return;
            }

            string[] args = argument.Split(' ');

            if (args.Length < 1)
            {
                return;
            }

            if (args[0].Equals("reload"))
            {
                AbortNav(false);
                LoadCustomDataConfig();
                return;
            }
            else if (args[0].Equals("maxthrustoverrideratio") || args[0].Equals("thrustratio"))
            {
                SetThrustRatio(args);
                return;
            }

            Action cmdAction = null;

            if (args.Length >= 3 && args[0].Equals("cruise"))
            {
                cmdAction = () => CommandCruise(args, argument);
            }
            else if (args[0] == "retro" || args[0] == "retrograde")
            {
                cmdAction = CommandRetrograde;
            }
            else if (args[0] == "retroburn")
            {
                cmdAction = CommandRetroburn;
            }
            else if (args[0] == "prograde")
            {
                cmdAction = CommandPrograde;
            }
            else if (args[0] == "match" || args[0] == "speedmatch")
            {
                cmdAction = CommandSpeedMatch;
            }
            else if (args[0] == "orient")
            {
                cmdAction = () => CommandOrient(argument);
            }
            else if (args[0] == "calibrate180")
            {
                //TODO: Calibrate 180 Time
            }

            if (cmdAction != null)
            {
                AbortNav(false);
                optionalInfo = "";
                cmdAction.Invoke();
            }
        }

        private void SetThrustRatio(string[] args)
        {
            if (args.Length < 2)
            {
                optionalInfo = "New override ratio argument not found!";
                return;
            }

            double result;
            if (!double.TryParse(args[1], out result))
            {
                optionalInfo = "Could not parse new override ratio";
                return;
            }

            config.MaxThrustOverrideRatio = result;
            SaveCustomDataConfig();

            if (cruiseController is IVariableMaxOverrideThrustController)
            {
                if (cruiseController is SpeedMatch)
                    ((SpeedMatch)cruiseController).MaxThrustRatio = config.IgnoreMaxThrustForSpeedMatch ? 1f : (float)result;
                else
                    ((IVariableMaxOverrideThrustController)cruiseController).MaxThrustRatio = (float)result;
            }

            optionalInfo = $"New thrust ratio set to {result:0.##}";
        }

        private void CommandCruise(string[] args, string argument)
        {
            try
            {
                double desiredSpeed = double.Parse(args[1]);
                Vector3D target;

                double result;
                bool distanceCruise;
                int gpsIndex = argument.IndexOf("gps:", StringComparison.OrdinalIgnoreCase);
                if (distanceCruise = double.TryParse(args[2], out result))
                {
                    target = controller.GetPosition() + (controller.WorldMatrix.Forward * result);
                }
                else if (gpsIndex >= 0)
                {
                    try
                    {
                        string[] coords = argument.Substring(gpsIndex).Split(':');

                        double x = double.Parse(coords[2]);
                        double y = double.Parse(coords[3]);
                        double z = double.Parse(coords[4]);

                        target = new Vector3D(x, y, z);
                    }
                    catch (Exception e)
                    {
                        optionalInfo = "Error occurred while parsing gps";
                        return;
                    }
                }
                else
                {
                    try
                    {
                        string[] coords = args[2].Split(':');

                        double x = double.Parse(coords[0]);
                        double y = double.Parse(coords[1]);
                        double z = double.Parse(coords[2]);

                        target = new Vector3D(x, y, z);
                    }
                    catch (Exception e)
                    {
                        optionalInfo = "Error occurred while parsing coords";
                        return;
                    }
                }

                Vector3D offsetTarget = Vector3D.Zero;

                if (!distanceCruise)
                {
                    if (config.CruiseOffsetSideDist > 0)
                    {
                        offsetTarget += Vector3D.CalculatePerpendicularVector(target - controller.GetPosition()) * config.CruiseOffsetSideDist;
                    }
                    if (config.CruiseOffsetDist > 0)
                    {
                        offsetTarget += (target - controller.GetPosition()).SafeNormalize() * -config.CruiseOffsetDist;
                    }
                }

                InitRetroCruise(target + offsetTarget, desiredSpeed);
                optionalInfo = "";
            }
            catch (Exception e)
            {
                optionalInfo = e.ToString();
            }
        }

        private void InitRetroCruise(Vector3D target, double speed)
        {
            NavMode = NavModeEnum.Cruise;
            cruiseController = new RetroCruiseControl(target, speed, aimController, controller, gyros, thrusters)
            {
                MaxThrustRatio = (float)config.MaxThrustOverrideRatio,
                decelStartMarginSeconds = config.Ship180TurnTimeSeconds * 1.5,
            };
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.Cruise}|{speed}";
            Storage = target.ToString();
            SaveCustomDataConfig();
        }

        private void CommandRetrograde()
        {
            NavMode = NavModeEnum.Retrograde;
            cruiseController = new Retrograde(aimController, controller, gyros);
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.Retrograde}";
            SaveCustomDataConfig();
            optionalInfo = "";
        }

        private void CommandRetroburn()
        {
            NavMode = NavModeEnum.Retroburn;
            cruiseController = new Retroburn(aimController, controller, gyros, thrusters)
            {
                MaxThrustRatio = (float)config.MaxThrustOverrideRatio,
            };
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.Retroburn}";
            SaveCustomDataConfig();
            optionalInfo = "";
        }

        private void CommandPrograde()
        {
            NavMode = NavModeEnum.Prograde;
            cruiseController = new Prograde(aimController, controller, gyros);
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.Prograde}";
            SaveCustomDataConfig();
            optionalInfo = "";
        }

        private void CommandSpeedMatch()
        {
            if (!wcApiActive)
            {
                try { wcApiActive = wcApi.Activate(Me); }
                catch { wcApiActive = false; }
            }
            if (!wcApiActive)
                return;
            var target = wcApi.GetAiFocus(Me.CubeGrid.EntityId);
            if ((target?.EntityId ?? 0) == 0)
                return;
            InitSpeedMatch(target.Value.EntityId);
            optionalInfo = "";
        }

        private void CommandOrient(string argument)
        {
            try
            {
                Vector3D target;

                int gpsIndex = argument.IndexOf("gps:", StringComparison.OrdinalIgnoreCase);
                if (gpsIndex >= 0)
                {
                    string[] coords = argument.Substring(gpsIndex).Split(':');

                    double x = double.Parse(coords[2]);
                    double y = double.Parse(coords[3]);
                    double z = double.Parse(coords[4]);

                    target = new Vector3D(x, y, z);
                }
                else
                {
                    optionalInfo = "Incorrect orient command params, no gps detected";
                    return;
                }

                InitOrient(target);
                optionalInfo = "";
            }
            catch (Exception e)
            {
                optionalInfo = e.ToString();
            }
        }

        private void InitOrient(Vector3D target)
        {
            NavMode = NavModeEnum.Orient;
            cruiseController = new Orient(aimController, controller, gyros, target);
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.Orient}";
            Storage = target.ToString();
            SaveCustomDataConfig();
        }

        private void InitSpeedMatch(long targetId)
        {
            NavMode = NavModeEnum.SpeedMatch;
            cruiseController = new SpeedMatch(targetId, wcApi, controller, thrusters, Me)
            {
                MaxThrustRatio = config.IgnoreMaxThrustForSpeedMatch ? 1f : (float)config.MaxThrustOverrideRatio,
            };
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.SpeedMatch}|{targetId}";
            SaveCustomDataConfig();
        }
    }
}
