using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage;

namespace IngameScript
{
    public struct VersionInfo
    {
        public uint Major;
        public uint Minor;
        public uint Patch;

        public VersionInfo(uint major, uint minor, uint patch)
        {
            this.Major = major;
            this.Minor = minor;
            this.Patch = patch;
        }

        public static bool TryParse(string versionString, out VersionInfo result)
        {
            var ver = new VersionInfo();

            if (string.IsNullOrWhiteSpace(versionString))
            {
                result = default(VersionInfo);
                return false;
            }

            string[] str = versionString.Split('.');
            try
            {
                for (int i = 0; i < Math.Min(str.Length, 3); i++)
                {
                    switch (i)
                    {
                        case 0: ver.Major = uint.Parse(str[i]); break;
                        case 1: ver.Minor = uint.Parse(str[i]); break;
                        case 2: ver.Patch = uint.Parse(str[i]); break;
                    }
                }
            }
            catch
            {
                //log exception

                result = default(VersionInfo);
                return false;
            }

            result = ver;
            return true;
        }

        public override string ToString() => $"{Major}.{Minor}.{Patch}";
        public string ToString(bool hidePatchNumberIfZero) => hidePatchNumberIfZero && Patch == 0 ? $"{Major}.{Minor}" : this.ToString();

        public static bool operator <(VersionInfo x, VersionInfo y)
        {
            if (x.Major != y.Major)
                return x.Major < y.Major;

            if (x.Minor != y.Minor)
                return x.Minor < y.Minor;

            return x.Patch < y.Patch;
        }

        public static bool operator >(VersionInfo x, VersionInfo y)
        {
            if (x.Major != y.Major)
                return x.Major > y.Major;

            if (x.Minor != y.Minor)
                return x.Minor > y.Minor;

            return x.Patch > y.Patch;
        }

        public static bool operator <=(VersionInfo x, VersionInfo y) => x < y || x == y;
        public static bool operator >=(VersionInfo x, VersionInfo y) => x > y || x == y;
        public static bool operator ==(VersionInfo x, VersionInfo y) => x.Equals(y);
        public static bool operator !=(VersionInfo x, VersionInfo y) => !x.Equals(y);

        public override bool Equals(object obj)
        {
            if (obj == null || !(obj is VersionInfo))
                return false;

            VersionInfo ver2 = (VersionInfo)obj;
            return Major == ver2.Major && Minor == ver2.Minor && Patch == ver2.Patch;
        }

        public override int GetHashCode() => MyTuple.CombineHashCodes(Major.GetHashCode(), Minor.GetHashCode(), Patch.GetHashCode());
    }
}
