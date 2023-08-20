﻿using System;
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
                Abort();
                CommandReloadConfig();
            }

            if (!IsNavIdle)
            {
                return;
            }

            if (args.Length >= 3 && args[0].Equals("cruise"))
            {
                CommandCruise(args);
            }
            else if (args[0].Equals("retro") || args[0].Equals("retrograde"))
            {
                CommandRetrograde();
            }
            else if (args[0].Equals("match") || args[0].Equals("speedmatch"))
            {
                CommandSpeedMatch();
            }
        }

        private void CommandReloadConfig()
        {
            LoadCustomDataConfig();
        }

        private void CommandCruise(string[] args)
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
                cruiseController = new RetroCruiseControl(target, desiredSpeed, aimController, controller, gyros[0], thrusters)
                {
                    thrustOverrideMultiplier = (float)config.MaxThrustOverrideRatio,
                };
                cruiseController.CruiseTerminated += CruiseTerminated;
            }
            catch (Exception e)
            {
                optionalInfo = e.ToString();
            }
        }

        private void CommandRetrograde()
        {
            NavMode = NavModeEnum.Cruise;
            cruiseController = new Retrograde(aimController, controller, gyros[0]);
            cruiseController.CruiseTerminated += CruiseTerminated;
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
            NavMode = NavModeEnum.SpeedMatch;
            cruiseController = new SpeedMatch(target.Value.EntityId, wcApi, controller, thrusters, Me)
            {
                thrustOverrideMulti = (float)config.MaxThrustOverrideRatio,
            };
            cruiseController.CruiseTerminated += CruiseTerminated;
        }
    }
}