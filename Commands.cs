using Sandbox.ModAPI.Ingame;
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

            string argLower = argument.ToLower();

            if (argLower.Contains("abort"))
            {
                AbortNav(false);
                return;
            }

            string[] args = argLower.Split(' ');

            if (args.Length < 1)
            {
                return;
            }

            if (args[0].Equals("reload"))
            {
                AbortNav(false);
                LoadConfig(true);
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
                cmdAction = () => CommandCruise(args, argLower);
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
                cmdAction = () => CommandOrient(argLower);
            }
            else if (args[0] == "calibrateturn")
            {
                cmdAction = CommandCalibrateTurnTime;
            }
            else if (args[0] == "thrust")
            {
                float ratio;
                if (args.Length >= 3 && args[1] == "set" && float.TryParse(args[2], out ratio))
                {
                    if (ratio < 0 || ratio > 1.01)
                    {
                        optionalInfo = "Ratio must be between 0.0 and 1.0!";
                    }
                    else
                    {
                        optionalInfo = $"Forward thrust override set to {ratio * 100:0.###}%";
                        foreach (IMyThrust thrust in thrusters[Direction.Forward])
                        {
                            thrust.ThrustOverridePercentage = ratio;
                        }
                    }
                }
            }
            else if (args.Length >= 2 && args[0] == "journey")
            {
                optionalInfo = "";
                string failReason;
                if (args[1] == "load")
                    cmdAction = InitJourney;
                else if (cruiseController is Journey && !((Journey)cruiseController).HandleJourneyCommand(args, argument, out failReason))
                    optionalInfo = failReason;
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

            if (result < 0 || result > 1.01)
            {
                optionalInfo = "Ratio must be between 0.0 and 1.0!";
                return;
            }

            result = MathHelper.Clamp(result, 0, 1);

            config.MaxThrustOverrideRatio = result;
            SaveConfig();

            if (cruiseController is SpeedMatch)
                thrustController.MaxThrustRatio = config.IgnoreMaxThrustForSpeedMatch ? 1f : (float)result;
            else
                thrustController.MaxThrustRatio = (float)result;

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
                    if (config.CruiseOffsetDist > 0)
                    {
                        if (config.CruiseOffsetSideDist == 0)
                        {
                            optionalInfo = "Side offset cannot be zero when using offset";
                            return;
                        }
                        offsetTarget += (target - controller.GetPosition()).SafeNormalize() * -config.CruiseOffsetDist;
                    }
                    if (config.CruiseOffsetSideDist > 0)
                    {
                        offsetTarget += Vector3D.CalculatePerpendicularVector(target - controller.GetPosition()) * config.CruiseOffsetSideDist;
                    }
                }

                InitRetroCruise(target + offsetTarget, desiredSpeed);
            }
            catch (Exception e)
            {
                optionalInfo = e.ToString();
            }
        }

        private void InitRetroCruise(Vector3D target, double speed, RetroCruiseControl.RetroCruiseStage stage = RetroCruiseControl.RetroCruiseStage.None, bool saveConfig = true)
        {
            NavMode = NavModeEnum.Cruise;
            thrustController.MaxThrustRatio = (float)config.MaxThrustOverrideRatio;
            cruiseController = new RetroCruiseControl(target, speed, aimController, controller, gyros, thrustController, otherThrustController, this, stage)
            {
                decelStartMarginSeconds = config.Ship180TurnTimeSeconds * 1.5,
            };
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.Cruise}|{speed}|{stage}";
            Storage = target.ToString();
            if (saveConfig)
            {
                SaveConfig();
            }
        }

        private void CommandRetrograde()
        {
            NavMode = NavModeEnum.Retrograde;
            cruiseController = new Retrograde(aimController, controller, gyros);
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.Retrograde}";
            SaveConfig();
        }

        private void CommandRetroburn()
        {
            NavMode = NavModeEnum.Retroburn;
            thrustController.MaxThrustRatio = (float)config.MaxThrustOverrideRatio;
            cruiseController = new Retroburn(aimController, controller, gyros, thrustController);
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.Retroburn}";
            SaveConfig();
        }

        private void CommandPrograde()
        {
            NavMode = NavModeEnum.Prograde;
            cruiseController = new Prograde(aimController, controller, gyros);
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.Prograde}";
            SaveConfig();
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

        private void CommandCalibrateTurnTime()
        {
            NavMode = NavModeEnum.CalibrateTurnTime;
            cruiseController = new CalibrateTurnTime(config, aimController, controller, gyros);
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.CalibrateTurnTime}";
            SaveConfig();
        }

        private void InitOrient(Vector3D target)
        {
            NavMode = NavModeEnum.Orient;
            cruiseController = new Orient(aimController, controller, gyros, target);
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.Orient}";
            Storage = target.ToString();
            SaveConfig();
        }

        private void InitSpeedMatch(long targetId)
        {
            NavMode = NavModeEnum.SpeedMatch;
            thrustController.MaxThrustRatio = config.IgnoreMaxThrustForSpeedMatch ? 1f : (float)config.MaxThrustOverrideRatio;
            cruiseController = new SpeedMatch(targetId, wcApi, controller, Me, thrustController, otherThrustController, config.RequireDampenersForSpeedMatch);
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.SpeedMatch}|{targetId}";
            SaveConfig();
        }

        private void InitJourney()
        {
            NavMode = NavModeEnum.Journey;
            thrustController.MaxThrustRatio = (float)config.MaxThrustOverrideRatio;
            cruiseController = new Journey(aimController, controller, gyros, config.Ship180TurnTimeSeconds * 1.5, thrustController, otherThrustController, config.DeactivateForwardThrustInCruise, this);
            cruiseController.CruiseTerminated += CruiseTerminated;
        }
    }
}
