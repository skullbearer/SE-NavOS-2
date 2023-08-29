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
                Abort();
                return;
            }

            string[] args = argument.ToLower().Split(' ');

            if (args[0].Equals("reload"))
            {
                Abort(false);
                CommandReloadConfig();
            }

            if (/*!IsNavIdle || */args.Length < 1)
            {
                return;
            }

            bool cmdMatched = false;

            if (args.Length >= 3 && args[0].Equals("cruise"))
            {
                Abort();
                CommandCruise(args, argument);
                cmdMatched = true;
            }
            else if (args[0].Equals("retro") || args[0].Equals("retrograde"))
            {
                Abort();
                CommandRetrograde();
                cmdMatched = true;
            }
            else if (args[0].Equals("retroburn"))
            {
                Abort();
                CommandRetroburn();
                cmdMatched = true;
            }
            else if (args[0].Equals("prograde"))
            {
                Abort();
                CommandPrograde();
                cmdMatched = true;
            }
            else if (args[0].Equals("match") || args[0].Equals("speedmatch"))
            {
                Abort();
                CommandSpeedMatch();
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

                Vector3D offset = Vector3D.Zero;
                if (config.OffsetDirection == Config.OffsetType.Forward)
                {
                    offset = (target - controller.GetPosition()).SafeNormalize() * -config.CruiseOffset;
                }
                else if (config.OffsetDirection == Config.OffsetType.Side)
                {
                    offset = Vector3D.CalculatePerpendicularVector(target - controller.GetPosition()) * config.CruiseOffset;
                }

                InitRetroCruise(target + offset, desiredSpeed);
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
