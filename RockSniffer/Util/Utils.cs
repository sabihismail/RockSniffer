using Microsoft.Win32;
using System.Collections.Generic;
using System;

namespace RockSniffer.Util
{
    public static class Utils
    {
        public static Lazy<string> _hardwareHashLazy = new(GetHardwareHash());
        public static string HardwareHash => _hardwareHashLazy.Value;

        public static string GetHardwareHash()
        {
            string location = @"SOFTWARE\Microsoft\Cryptography";
            string name = "MachineGuid";

            using RegistryKey localMachineX64View = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey rk = localMachineX64View.OpenSubKey(location);

            if (rk == null)
            {
                throw new KeyNotFoundException(string.Format("Key Not Found: {0}", location));
            }

            object machineGuid = rk.GetValue(name);
            if (machineGuid == null)
            {
                throw new IndexOutOfRangeException(string.Format("Index Not Found: {0}", name));
            }

            return machineGuid.ToString();
        }
    }
}
