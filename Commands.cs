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

            if (argument.ToLower().Contains("abort"))
            {
                Abort(false);
                return;
            }

            string[] args = argument.ToLower().Split(' ');

            if (args.Length < 1)
            {
                return;
            }

            if (args[0].Equals("reload"))
            {
                Abort(false);
                CommandReloadConfig();
            }

            bool cmdMatched = false;

            if (args.Length >= 3 && args[0].Equals("cruise"))
            {
                Abort(false);
                CommandCruise(args, argument);
                cmdMatched = true;
            }
            else if (args[0].Equals("retro") || args[0].Equals("retrograde"))
            {
                Abort(false);
                CommandRetrograde();
                cmdMatched = true;
            }
            else if (args[0].Equals("retroburn"))
            {
                Abort(false);
                CommandRetroburn();
                cmdMatched = true;
            }
            else if (args[0].Equals("prograde"))
            {
                Abort(false);
                CommandPrograde();
                cmdMatched = true;
            }
            else if (args[0].Equals("match") || args[0].Equals("speedmatch"))
            {
                Abort(false);
                CommandSpeedMatch();
                cmdMatched = true;
            }
            else if (args[0].Equals("orient"))
            {
                Abort(false);
                CommandOrient(argument);
                cmdMatched = true;
            }

            //TODO: Calibrate 180 Time

            if (cmdMatched)
            {
                //optionalInfo = "";
            }
        }

        private void CommandReloadConfig()
        {
            LoadCustomDataConfig();
        }

        private void CommandCruise(string[] args, string argument)
        {
            try
            {
                double desiredSpeed;
                Vector3D target;

                desiredSpeed = double.Parse(args[1]);

                double result;
                bool distanceCruise;
                if (distanceCruise = double.TryParse(args[2], out result))
                {
                    target = controller.GetPosition() + (controller.WorldMatrix.Forward * result);
                }
                else if (args[2].StartsWith("gps:", StringComparison.OrdinalIgnoreCase))
                {
                    string[] coords = argument.Substring(argument.IndexOf("GPS:")).Split(':');

                    double x = double.Parse(coords[2]);
                    double y = double.Parse(coords[3]);
                    double z = double.Parse(coords[4]);

                    target = new Vector3D(x, y, z);
                }
                else
                {
                    string[] coords = args[2].Split(':');

                    double x = double.Parse(coords[0]);
                    double y = double.Parse(coords[1]);
                    double z = double.Parse(coords[2]);

                    target = new Vector3D(x, y, z);
                }

                Vector3D offsetTarget = Vector3D.Zero;
                if (config.CruiseOffsetSideDist > 0)
                {
                    offsetTarget += Vector3D.CalculatePerpendicularVector(target - controller.GetPosition()) * config.CruiseOffsetSideDist;
                }
                if (config.CruiseOffsetDist > 0)
                {
                    offsetTarget += (target - controller.GetPosition()).SafeNormalize() * -config.CruiseOffsetDist;
                }

                InitRetroCruise(target + offsetTarget, desiredSpeed);
            }
            catch (Exception e)
            {
                optionalInfo = e.ToString();
            }
        }

        private void InitRetroCruise(Vector3D target, double speed)
        {
            NavMode = NavModeEnum.Cruise;
            cruiseController = new RetroCruiseControl(target, speed, aimController, controller, gyros[0], thrusters)
            {
                maxThrustOverrideRatio = (float)config.MaxThrustOverrideRatio,
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
            cruiseController = new Retrograde(aimController, controller, gyros[0]);
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.Retrograde}";
            SaveCustomDataConfig();
            optionalInfo = "";
        }

        private void CommandRetroburn()
        {
            NavMode = NavModeEnum.Retroburn;
            cruiseController = new Retroburn(aimController, controller, gyros[0], thrusters)
            {
                maxThrustOverrideRatio = (float)config.MaxThrustOverrideRatio,
            };
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.Retroburn}";
            SaveCustomDataConfig();
            optionalInfo = "";
        }

        private void CommandPrograde()
        {
            NavMode = NavModeEnum.Prograde;
            cruiseController = new Prograde(aimController, controller, gyros[0]);
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

                if (argument.Contains("GPS:"))
                {
                    string[] coords = argument.Substring(argument.IndexOf("GPS:")).Split(':');

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
            optionalInfo = "";
        }

        private void InitSpeedMatch(long targetId)
        {
            NavMode = NavModeEnum.SpeedMatch;
            cruiseController = new SpeedMatch(targetId, wcApi, controller, thrusters, Me)
            {
                maxThrustOverrideRatio = (float)config.MaxThrustOverrideRatio,
            };
            cruiseController.CruiseTerminated += CruiseTerminated;
            config.PersistStateData = $"{NavModeEnum.SpeedMatch}|{targetId}";
            SaveCustomDataConfig();
        }
    }
}
