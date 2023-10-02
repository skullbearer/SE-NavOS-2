using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRageMath;

namespace IngameScript
{
    public static class Utils
    {
        public static Vector3D SafeNormalize(this Vector3D a)
        {
            if (Vector3D.IsZero(a))
                return Vector3D.Zero;
            if (Vector3D.IsUnit(ref a))
                return a;
            return Vector3D.Normalize(a);
        }

        public static StringBuilder AppendTime(this StringBuilder strb, double totalSeconds)
        {
            int minutes = (int)totalSeconds / 60;
            totalSeconds %= 60;
            strb.Append(minutes).Append(":").Append(totalSeconds.ToString("00.0"));
            return strb;
        }

        public static bool TryParseGps(string str, out string name, out Vector3D result)
        {
            name = default(string);
            result = default(Vector3D);
            int startIndex = str.IndexOf("gps:", StringComparison.CurrentCultureIgnoreCase);
            if (startIndex < 0)
                return false;
            string[] args = str.Substring(startIndex).Split(':');
            if (args.Length < 5)
                return false;
            name = args[1];
            double x, y, z;
            if (!double.TryParse(args[2], out x) || !double.TryParse(args[3], out y) || !double.TryParse(args[4], out z))
                return false;
            result = new Vector3D(x, y, z);
            return true;
        }
    }
}
