using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IngameScript
{
    internal class Config
    {
        public enum OffsetType
        {
            None,
            Forward,
            Side,
        }

        public static Config Default { get; } = new Config();
        public static VersionInfo ConfigVersion { get; } = new VersionInfo(1, 2, 0);

        public string PersistStateData { get; set; } = "";
        public double MaxThrustOverrideRatio { get; set; } = 1.0;
        public bool IgnoreMaxThrustForSpeedMatch { get; set; } = false;
        public string ShipControllerTag { get; set; } = "Nav";
        public string ThrustGroupName { get; set; } = "NavThrust";
        public string GyroGroupName { get; set; } = "NavGyros";
        public string ConsoleLcdName { get; set; } = "consoleLcd";
        private double CruiseOffset { get; set; } = 0;
        private OffsetType OffsetDirection { get; set; } = OffsetType.None;
        public double CruiseOffsetDist { get; set; } = 0;
        public double CruiseOffsetSideDist { get; set; } = 0;
        public double Ship180TurnTimeSeconds { get; set; } = 10.0;

        private Config()
        {

        }

        public static bool TryParse(string str, out Config config)
        {
            var conf = new Config();

            if (string.IsNullOrWhiteSpace(str) || !str.StartsWith("NavConfig"))
            {
                config = null;
                return false;
            }

            string[] lines = str.Split(Environment.NewLine.ToCharArray());
            //try
            //{
            //    VersionInfo ver;
            //    if (lines.Length >= 1 && VersionInfo.TryParse(lines[0].Split('|').Last().Trim(), out ver) && ver <= new VersionInfo(2, 10, 0))
            //    {
            //
            //    }
            //}
            //catch { }

            Dictionary<string, string> confValues = new Dictionary<string, string>();

            for (int i = 1; i < lines.Length; i++)
            {
                if (String.IsNullOrWhiteSpace(lines[i]) || lines[i].StartsWith("//"))
                    continue;

                string[] substrings = lines[i].Split('=');
                if (substrings.Length >= 2 && substrings[0] != null && substrings[1] != null)
                {
                    if (!confValues.ContainsKey(substrings[0]))
                    {
                        confValues.Add(substrings[0], substrings[1]);
                    }
                    else
                    {
                        confValues[substrings[0]] = substrings[1];
                    }
                }
            }

            string result;

            if (confValues.TryGetValue(nameof(PersistStateData), out result))
                conf.PersistStateData = result;

            if (confValues.TryGetValue(nameof(MaxThrustOverrideRatio), out result))
            {
                double val;
                if (double.TryParse(result, out val))
                    conf.MaxThrustOverrideRatio = val;
            }

            if (confValues.TryGetValue(nameof(IgnoreMaxThrustForSpeedMatch), out result))
            {
                bool val;
                if (bool.TryParse(result, out val))
                    conf.IgnoreMaxThrustForSpeedMatch = val;
            }

            if (confValues.TryGetValue(nameof(ShipControllerTag), out result))
                conf.ShipControllerTag = result;

            if (confValues.TryGetValue(nameof(ThrustGroupName), out result))
                conf.ThrustGroupName = result;

            if (confValues.TryGetValue(nameof(GyroGroupName), out result))
                conf.GyroGroupName = result;

            if (confValues.TryGetValue(nameof(ConsoleLcdName), out result))
                conf.ConsoleLcdName = result;

            if (confValues.TryGetValue(nameof(CruiseOffsetDist), out result))
            {
                double val;
                if (double.TryParse(result, out val))
                    conf.CruiseOffsetDist += val;
            }

            if (confValues.TryGetValue(nameof(CruiseOffsetSideDist), out result))
            {
                double val;
                if (double.TryParse(result, out val))
                    conf.CruiseOffsetSideDist += val;
            }

            //support for v1.10 or older configs
            if (confValues.TryGetValue(nameof(OffsetDirection), out result))
            {
                OffsetType enumResult;
                double val;
                if (Enum.TryParse<OffsetType>(result, true, out enumResult) &&
                    enumResult != OffsetType.None &&
                    confValues.TryGetValue(nameof(CruiseOffset), out result) &&
                    double.TryParse(result, out val))
                {
                    if (enumResult == OffsetType.Side)
                    {
                        conf.CruiseOffsetSideDist += val;
                    }
                    else if (enumResult == OffsetType.Forward)
                    {
                        conf.CruiseOffsetDist += val;
                    }
                }
            }

            if (confValues.TryGetValue(nameof(Ship180TurnTimeSeconds), out result))
            {
                double val;
                if (double.TryParse(result, out val))
                    conf.Ship180TurnTimeSeconds = val;
            }

            config = conf;
            return true;
        }

        public override string ToString()
        {
            StringBuilder strb = new StringBuilder();

            strb.Append($"NavConfig | {Program.versionInfo.ToString(false)} | {ConfigVersion.ToString(false)}\n");
            strb.Append("// Remember to recompile after you change the config!\n");
            strb.Append($"{nameof(PersistStateData)}={PersistStateData}\n\n");
            strb.Append("// Maximum thrust override. 0 to 1 (Dont use 0)\n");
            strb.Append($"{nameof(MaxThrustOverrideRatio)}={MaxThrustOverrideRatio}\n");
            strb.Append($"{nameof(IgnoreMaxThrustForSpeedMatch)}={IgnoreMaxThrustForSpeedMatch}\n\n");
            strb.Append("// Tag for the controller used for ship orientation");
            strb.Append($"{nameof(ShipControllerTag)}={ShipControllerTag}\n\n");
            strb.Append("// If this group doesn't exist it uses all thrusters\n");
            strb.Append($"{nameof(ThrustGroupName)}={ThrustGroupName}\n\n");
            strb.Append("// If this group doesn't exist it uses all gyros\n");
            strb.Append($"{nameof(GyroGroupName)}={GyroGroupName}\n\n");
            strb.Append("// Copies pb output to this lcd is it exists\n");
            strb.Append($"{nameof(ConsoleLcdName)}={ConsoleLcdName}\n\n");
            strb.Append("// Cruise offset distances in meters\n");
            strb.Append($"{nameof(CruiseOffsetDist)}={CruiseOffsetDist}\n");
            strb.Append($"{nameof(CruiseOffsetSideDist)}={CruiseOffsetSideDist}\n\n");
            strb.Append("// Time for the ship to do a 180 degree turn in seconds\n");
            strb.Append($"{nameof(Ship180TurnTimeSeconds)}={Ship180TurnTimeSeconds}");

            return strb.ToString();
        }
    }
}
