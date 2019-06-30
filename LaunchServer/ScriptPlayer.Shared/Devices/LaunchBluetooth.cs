﻿/*
BSD 3-Clause License

Copyright (c) 2017, Fred Tungsten
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of the copyright holder nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace ScriptPlayer.Shared
{
    public class LaunchBluetooth : DeviceController
    {
        private readonly object _discoverylocker = new object();
        private bool _discover;
        public BluetoothLEAdvertisementWatcher BleWatcher { get; set; }

        public override void ScanForDevices()
        {
            Start();
        }

        public LaunchBluetooth()
        {
            BleWatcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            BleWatcher.Received += BleReceived;
        }

        public void Start()
        {
            lock (_discoverylocker)
            {
                if (_discover)
                    return;
            
                Debug.WriteLine("Start watching ...");
                _discover = true;
                BleWatcher.Start();
            }
        }

        public void Stop()
        {
            lock (_discoverylocker)
            {
                if (!_discover)
                    return;

                _discover = false;
                BleWatcher.Stop();
            }
        }

        public static bool IsLaunchPaired()
        {
            var scope = new ManagementScope(@"\\" + Environment.MachineName + @"\root\CIMV2");
            var sq = new SelectQuery("SELECT Name FROM Win32_PnPEntity WHERE Name='Launch'");
            var searcher = new ManagementObjectSearcher(scope, sq);
            var moc = searcher.Get();

            foreach (ManagementObject mo in moc)
            {
                object propName = mo.Properties["Name"].Value;
                Debug.WriteLine($"{propName} is paired!");
                return true;
            }

            return false;
        }

        private async void BleReceived(BluetoothLEAdvertisementWatcher w, BluetoothLEAdvertisementReceivedEventArgs btAdv)
        {
            if (w == null) return;
            if (btAdv == null) return;

            lock (_discoverylocker)
            {
                if (!_discover) return;
                Stop();
            }

            var uids = btAdv.Advertisement?.ServiceUuids;
            if (uids == null) return;

            Debug.WriteLine($"BLE RECEIVED, Services: {string.Join(", ", uids)}, aquiring device ...");

            var deviceAwaiting = BluetoothLEDevice.FromBluetoothAddressAsync(btAdv.BluetoothAddress);

            if (deviceAwaiting == null) return;

            BluetoothLEDevice device = await deviceAwaiting;

            if (device == null) return;

            Debug.WriteLine($"BLEWATCHER Found: {device.Name}, {device.DeviceId}");

            bool foundAndConnected = false;

            try
            {
                Thread.Sleep(3000);
                // SERVICES!!
                GattDeviceService service = (await device.GetGattServicesForUuidAsync(Launch.Uids.MainService))
                    .Services.FirstOrDefault();
                if (service == null) return;
                Debug.WriteLine($"{device.Name} Main Services found!");
                Debug.WriteLine("Service UUID found!");

                GattCharacteristic writeCharacteristics =
                    (await service.GetCharacteristicsForUuidAsync(Launch.Uids.WriteCharacteristics)).Characteristics
                    .FirstOrDefault();
                GattCharacteristic notifyCharacteristics =
                    (await service.GetCharacteristicsForUuidAsync(Launch.Uids.StatusNotificationCharacteristics))
                    .Characteristics.FirstOrDefault();
                GattCharacteristic commandCharacteristics =
                    (await service.GetCharacteristicsForUuidAsync(Launch.Uids.CommandCharacteristics))
                    .Characteristics.FirstOrDefault();

                if (writeCharacteristics == null || commandCharacteristics == null ||
                    notifyCharacteristics == null) return;

                Debug.WriteLine("Characteristics found!");

                Launch launch = new Launch(device, writeCharacteristics, notifyCharacteristics,
                    commandCharacteristics);

                bool init = await launch.Initialize();

                Debug.WriteLine("Launch Initialized: " + init.ToString().ToUpper() + "!");

                foundAndConnected = true;
                OnDeviceFound(launch);

            }
            catch (Exception e)
            {
                Debug.WriteLine("Exception: " + e.Message);
                device.Dispose();
            }
            finally
            {
                if (!foundAndConnected)
                {
                    Debug.WriteLine("Connect failed, try again ...");
                    Start();
                }
            }
        }
    }
}
