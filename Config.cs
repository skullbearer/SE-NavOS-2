using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IngameScript
{
    internal class Config
    {
        public static Config Default { get; } = new Config();

        public double MaxThrustOverrideRatio { get; set; } = 1.0;
        public string ShipControllerTag { get; set; } = "Nav";
        public string ThrustGroupName { get; set; } = "NavThrust";
        public string GyroGroupName { get; set; } = "NavGyros";
        public string ConsoleLcdName { get; set; } = "consoleLcd";

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

            string[] lines = str.Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("//"))
                    continue;

                string[] substrings = lines[i].Split('=');
                try
                {
                    switch (substrings[0])
                    {
                        case nameof(MaxThrustOverrideRatio):
                            conf.MaxThrustOverrideRatio = double.Parse(substrings[1]); break;
                        case nameof(ShipControllerTag):
                            conf.ShipControllerTag = substrings[1]; break;
                        case nameof(ThrustGroupName):
                            conf.ThrustGroupName = substrings[1]; break;
                        case nameof(GyroGroupName):
                            conf.GyroGroupName = substrings[1]; break;
                        case nameof(ConsoleLcdName):
                            conf.ConsoleLcdName = substrings[1]; break;
                    }
                }
                catch
                {
                    //log exception
                }
            }

            config = conf;
            return true;
        }

        public override string ToString()
        {
            StringBuilder strb = new StringBuilder();

            strb.AppendLine($"NavConfig|{Program.versionInfo.ToString(false)}");
            strb.AppendLine();
            strb.AppendLine("// Maximum thrust override. 0 to 1 (Dont use 0)");
            strb.AppendLine($"{nameof(MaxThrustOverrideRatio)}={MaxThrustOverrideRatio}");
            strb.AppendLine();
            //strb.AppendLine("// ");
            strb.AppendLine($"{nameof(ShipControllerTag)}={ShipControllerTag}");
            strb.AppendLine();
            strb.AppendLine("// If this group doesn't exist it uses all thrusters");
            strb.AppendLine($"{nameof(ThrustGroupName)}={ThrustGroupName}");
            strb.AppendLine();
            strb.AppendLine("// If this group doesn't exist it uses all gyros");
            strb.AppendLine($"{nameof(GyroGroupName)}={GyroGroupName}");
            strb.AppendLine();
            strb.AppendLine("// Copies pb output to this lcd is it exists");
            strb.AppendLine($"{nameof(ConsoleLcdName)}={ConsoleLcdName}");

            return strb.ToString();
        }
    }
}
