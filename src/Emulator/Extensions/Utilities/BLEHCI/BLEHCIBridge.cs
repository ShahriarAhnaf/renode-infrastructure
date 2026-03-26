//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

// Uncomment to dump raw HCI packets:
// #define DEBUG_PACKETS

using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Wireless;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Extensions.Utilities.BLEHCI
{
    public static class BLEHCIBridgeExtensions
    {
        public static void CreateBLEHCIBridge(this Emulation emulation, int port = 3456, string name = "blehci")
        {
            var bridge = new BLEHCIBridge(port);
            bridge.Run();
            emulation.ExternalsManager.AddExternal(bridge, name);
            emulation.HostMachine.AddHostMachineElement(bridge, name);
        }
    }

    // ISlipRadio is used so WirelessMedium delivers frames immediately
    // without calling GetMachine() (which would fail for a non-peripheral radio).
    public class BLEHCIBridge : ISlipRadio, IHostMachineElement
    {
        public BLEHCIBridge(int port)
        {
            this.port = port;
            server = new SocketServerProvider(false, serverName: "BLEHCI");
            server.DataReceived += HandleIncomingData;
            server.ConnectionClosed += Reset;
            buffer = new List<byte>();
            bdAddr = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        }

        // ── IRadio ────────────────────────────────────────────────

        public int Channel { get; set; }

        public event Action<IRadio, byte[]> FrameSent;

        public void ReceiveFrame(byte[] frame, IRadio sender)
        {
            if(state == LinkLayerState.Scanning)
            {
                HandleAdvertisingPDU(frame);
            }
            else if(state == LinkLayerState.Initiating)
            {
                HandleAdvertisingPDUWhileInitiating(frame);
            }
            else if(state == LinkLayerState.Connected)
            {
                HandleDataPDU(frame);
            }
        }

        // ── TCP Server / Lifecycle ────────────────────────────────

        public void Run()
        {
            server.Start(port);
            this.Log(LogLevel.Info, "BLE HCI bridge listening on port {0}", port);
        }

        public void Dispose()
        {
            server.Stop();
        }

        public void Reset()
        {
            state = LinkLayerState.Standby;
            hciState = HCIParseState.WaitForType;
            scanFilterDuplicates = false;
            connectionHandle = 0;
            buffer.Clear();
        }

        // ── HCI-H4 Packet Reassembly ─────────────────────────────

        private void HandleIncomingData(int b)
        {
            if(b < 0)
            {
                Reset();
                return;
            }

            buffer.Add((byte)b);

            switch(hciState)
            {
            case HCIParseState.WaitForType:
                currentPacketType = (byte)b;
                if(b == HCIPacketType.Command || b == HCIPacketType.ACLData)
                {
                    hciState = HCIParseState.WaitForHeader;
                }
                else
                {
                    this.Log(LogLevel.Warning, "Unknown HCI packet type 0x{0:X2}, resetting parser", b);
                    buffer.Clear();
                }
                break;

            case HCIParseState.WaitForHeader:
                if(currentPacketType == HCIPacketType.Command && buffer.Count >= 4)
                {
                    expectedLength = 4 + buffer[3];
                    hciState = HCIParseState.WaitForPayload;
                }
                else if(currentPacketType == HCIPacketType.ACLData && buffer.Count >= 5)
                {
                    expectedLength = 5 + (buffer[3] | (buffer[4] << 8));
                    hciState = HCIParseState.WaitForPayload;
                }
                break;

            case HCIParseState.WaitForPayload:
                if(buffer.Count >= expectedLength)
                {
                    var packet = buffer.ToArray();
                    buffer.Clear();
                    hciState = HCIParseState.WaitForType;
                    ProcessHCIPacket(packet);
                }
                break;
            }
        }

        private void ProcessHCIPacket(byte[] packet)
        {
#if DEBUG_PACKETS
            this.Log(LogLevel.Debug, "HCI RX: {0}", BitConverter.ToString(packet));
#endif
            if(packet[0] == HCIPacketType.Command)
            {
                ProcessHCICommand(packet);
            }
            else if(packet[0] == HCIPacketType.ACLData)
            {
                ProcessACLData(packet);
            }
        }

        // ── HCI Command Dispatch ─────────────────────────────────

        private void ProcessHCICommand(byte[] packet)
        {
            ushort opcode = (ushort)(packet[1] | (packet[2] << 8));
            byte paramLen = packet[3];
            byte[] parameters = new byte[paramLen];
            if(paramLen > 0)
            {
                Array.Copy(packet, 4, parameters, 0, paramLen);
            }

            this.Log(LogLevel.Debug, "HCI Command opcode=0x{0:X4} paramLen={1}", opcode, paramLen);

            switch(opcode)
            {
            case HCIOpcode.Reset:
                HandleReset();
                break;
            case HCIOpcode.ReadLocalVersionInformation:
                HandleReadLocalVersion();
                break;
            case HCIOpcode.ReadLocalSupportedCommands:
                HandleReadLocalSupportedCommands();
                break;
            case HCIOpcode.ReadLocalSupportedFeatures:
                HandleReadLocalSupportedFeatures();
                break;
            case HCIOpcode.ReadBDAddr:
                HandleReadBDAddr();
                break;
            case HCIOpcode.SetEventMask:
                HandleSetEventMask(parameters);
                break;
            case HCIOpcode.LESetEventMask:
                HandleLESetEventMask(parameters);
                break;
            case HCIOpcode.LEReadBufferSize:
                HandleLEReadBufferSize();
                break;
            case HCIOpcode.LEReadLocalSupportedFeatures:
                HandleLEReadLocalSupportedFeatures();
                break;
            case HCIOpcode.LESetScanParameters:
                HandleLESetScanParameters(parameters);
                break;
            case HCIOpcode.LESetScanEnable:
                HandleLESetScanEnable(parameters);
                break;
            case HCIOpcode.LECreateConnection:
                HandleLECreateConnection(parameters);
                break;
            case HCIOpcode.LECreateConnectionCancel:
                HandleLECreateConnectionCancel();
                break;
            case HCIOpcode.ReadLocalSupportedCodecs:
                HandleReadLocalSupportedCodecs();
                break;
            case HCIOpcode.LEReadBufferSizeV2:
                HandleLEReadBufferSizeV2();
                break;
            default:
                this.Log(LogLevel.Debug, "Unhandled HCI command 0x{0:X4}, returning generic success", opcode);
                SendCommandComplete(opcode, new byte[] { 0x00 });
                break;
            }
        }

        // ── HCI Command Handlers ─────────────────────────────────

        private void HandleReset()
        {
            state = LinkLayerState.Standby;
            connectionHandle = 0;
            SendCommandComplete(HCIOpcode.Reset, new byte[] { 0x00 });
        }

        private void HandleReadLocalVersion()
        {
            SendCommandComplete(HCIOpcode.ReadLocalVersionInformation, new byte[]
            {
                0x00,       // status: success
                0x09,       // HCI version: BT 5.0
                0x00, 0x00, // HCI revision
                0x09,       // LMP version: 5.0
                0xFF, 0x00, // manufacturer: 0x00FF (Renode)
                0x00, 0x00  // LMP subversion
            });
        }

        private void HandleReadLocalSupportedCommands()
        {
            var cmds = new byte[65];
            cmds[0] = 0x00; // status
            // Octet 0, bit 5: Set Event Mask
            cmds[1] |= 0x20;
            // Octet 5, bit 6: Reset
            cmds[6] |= 0x40;
            // Octet 14, bit 3: Read Local Version Information
            cmds[15] |= 0x08;
            // Octet 14, bit 5: Read Local Supported Features
            cmds[15] |= 0x20;
            // Octet 14, bit 6: Read Local Supported Commands
            // (Octet 14 = index 15 in 1-indexed param array → index 15 in 0-indexed with status at [0])
            cmds[15] |= 0x40;
            // Octet 15, bit 1: Read BD_ADDR
            cmds[16] |= 0x02;
            // Octet 25, bit 0: LE Set Event Mask
            cmds[26] |= 0x01;
            // Octet 25, bit 1: LE Read Buffer Size
            cmds[26] |= 0x02;
            // Octet 25, bit 2: LE Read Local Supported Features
            cmds[26] |= 0x04;
            // Octet 26, bit 2: LE Set Scan Parameters
            cmds[27] |= 0x04;
            // Octet 26, bit 3: LE Set Scan Enable
            cmds[27] |= 0x08;
            // Octet 26, bit 4: LE Create Connection
            cmds[27] |= 0x10;
            // Octet 26, bit 5: LE Create Connection Cancel
            cmds[27] |= 0x20;
            SendCommandComplete(HCIOpcode.ReadLocalSupportedCommands, cmds);
        }

        private void HandleReadLocalSupportedFeatures()
        {
            var features = new byte[9];
            features[0] = 0x00; // status
            features[5] |= 0x40; // bit 38: LE Supported (Controller)
            SendCommandComplete(HCIOpcode.ReadLocalSupportedFeatures, features);
        }

        private void HandleReadBDAddr()
        {
            var resp = new byte[7];
            resp[0] = 0x00; // status
            Array.Copy(bdAddr, 0, resp, 1, 6);
            SendCommandComplete(HCIOpcode.ReadBDAddr, resp);
        }

        private void HandleSetEventMask(byte[] p)
        {
            SendCommandComplete(HCIOpcode.SetEventMask, new byte[] { 0x00 });
        }

        private void HandleLESetEventMask(byte[] p)
        {
            SendCommandComplete(HCIOpcode.LESetEventMask, new byte[] { 0x00 });
        }

        private void HandleLEReadBufferSize()
        {
            SendCommandComplete(HCIOpcode.LEReadBufferSize, new byte[]
            {
                0x00,       // status
                0xFB, 0x00, // LE ACL Data Packet Length: 251
                0x08        // Total Num LE ACL Data Packets: 8
            });
        }

        private void HandleLEReadBufferSizeV2()
        {
            SendCommandComplete(HCIOpcode.LEReadBufferSizeV2, new byte[]
            {
                0x00,       // status
                0xFB, 0x00, // LE ACL Data Packet Length: 251
                0x08,       // Total Num LE ACL Data Packets: 8
                0x00, 0x00, // ISO Data Packet Length: 0
                0x00        // Total Num ISO Data Packets: 0
            });
        }

        private void HandleLEReadLocalSupportedFeatures()
        {
            var features = new byte[9];
            features[0] = 0x00; // status
            features[1] |= 0x01; // LE Encryption
            SendCommandComplete(HCIOpcode.LEReadLocalSupportedFeatures, features);
        }

        private void HandleReadLocalSupportedCodecs()
        {
            SendCommandComplete(HCIOpcode.ReadLocalSupportedCodecs, new byte[]
            {
                0x00, // status
                0x00, // Number_of_Supported_Standard_Codecs
                0x00  // Number_of_Supported_Vendor_Specific_Codecs
            });
        }

        private void HandleLESetScanParameters(byte[] p)
        {
            if(p.Length >= 1)
            {
                scanType = p[0];
            }
            SendCommandComplete(HCIOpcode.LESetScanParameters, new byte[] { 0x00 });
        }

        private void HandleLESetScanEnable(byte[] p)
        {
            bool enable = p.Length >= 1 && p[0] == 0x01;
            scanFilterDuplicates = p.Length >= 2 && p[1] == 0x01;

            if(enable)
            {
                state = LinkLayerState.Scanning;
                Channel = BLEAdvertisingChannel0;
                this.Log(LogLevel.Info, "BLE scanning enabled, listening on channel {0}", Channel);
            }
            else
            {
                state = LinkLayerState.Standby;
                this.Log(LogLevel.Info, "BLE scanning disabled");
            }
            SendCommandComplete(HCIOpcode.LESetScanEnable, new byte[] { 0x00 });
        }

        private void HandleLECreateConnection(byte[] p)
        {
            // HCI_LE_Create_Connection parameters:
            //   [0-1]  Scan Interval
            //   [2-3]  Scan Window
            //   [4]    Initiator Filter Policy
            //   [5]    Peer Address Type
            //   [6-11] Peer Address
            //   [12]   Own Address Type
            //   [13-14] Conn Interval Min
            //   [15-16] Conn Interval Max
            //   [17-18] Conn Latency
            //   [19-20] Supervision Timeout
            //   [21-22] Minimum CE Length
            //   [23-24] Maximum CE Length
            if(p.Length < 25)
            {
                this.Log(LogLevel.Warning, "LE_Create_Connection: insufficient parameters ({0} bytes)", p.Length);
                SendCommandStatus(HCIOpcode.LECreateConnection, 0x12); // Invalid HCI Command Parameters
                return;
            }

            pendingPeerAddrType = p[5];
            pendingPeerAddr = new byte[6];
            Array.Copy(p, 6, pendingPeerAddr, 0, 6);
            pendingConnInterval = (ushort)(p[13] | (p[14] << 8));
            pendingConnLatency = (ushort)(p[17] | (p[18] << 8));
            pendingSupervisionTimeout = (ushort)(p[19] | (p[20] << 8));

            state = LinkLayerState.Initiating;
            Channel = BLEAdvertisingChannel0;

            this.Log(LogLevel.Info, "LE Create Connection to {0}, interval={1}",
                BitConverter.ToString(pendingPeerAddr), pendingConnInterval);

            SendCommandStatus(HCIOpcode.LECreateConnection, 0x00);
        }

        private void HandleLECreateConnectionCancel()
        {
            if(state == LinkLayerState.Initiating)
            {
                state = LinkLayerState.Standby;
                SendCommandComplete(HCIOpcode.LECreateConnectionCancel, new byte[] { 0x00 });

                SendLEConnectionComplete(0x02, 0x0000, 0x00, new byte[6], 0, 0, 0);
            }
            else
            {
                SendCommandComplete(HCIOpcode.LECreateConnectionCancel, new byte[] { 0x0C }); // Command Disallowed
            }
        }

        // ── Advertising PDU Handling (Scanning) ──────────────────

        private void HandleAdvertisingPDU(byte[] frame)
        {
            if(frame.Length < BLEAccessAddressLength + 2)
            {
                return;
            }

            byte pduType = (byte)(frame[BLEAccessAddressLength] & 0x0F);
            byte pduLength = frame[BLEAccessAddressLength + 1];
            if(frame.Length < BLEAccessAddressLength + 2 + pduLength)
            {
                return;
            }

            if(pduLength < 6)
            {
                return;
            }

            byte[] payload = new byte[pduLength];
            Array.Copy(frame, BLEAccessAddressLength + 2, payload, 0, pduLength);

            byte[] advAddr = new byte[6];
            Array.Copy(payload, 0, advAddr, 0, 6);

            byte[] advData = new byte[pduLength - 6];
            if(advData.Length > 0)
            {
                Array.Copy(payload, 6, advData, 0, advData.Length);
            }

            byte txAdd = (byte)((frame[BLEAccessAddressLength] >> 6) & 0x01);

            this.Log(LogLevel.Debug, "ADV PDU type=0x{0:X} from {1}, data len={2}",
                pduType, BitConverter.ToString(advAddr), advData.Length);

            SendAdvertisingReport(pduType, txAdd, advAddr, advData);

            CycleAdvertisingChannel();
        }

        private void HandleAdvertisingPDUWhileInitiating(byte[] frame)
        {
            if(frame.Length < BLEAccessAddressLength + 2)
            {
                return;
            }

            byte pduType = (byte)(frame[BLEAccessAddressLength] & 0x0F);
            byte pduLength = frame[BLEAccessAddressLength + 1];
            if(frame.Length < BLEAccessAddressLength + 2 + pduLength || pduLength < 6)
            {
                return;
            }

            // Only respond to ADV_IND (0x00) or ADV_DIRECT_IND (0x01)
            if(pduType != 0x00 && pduType != 0x01)
            {
                return;
            }

            byte[] advAddr = new byte[6];
            Array.Copy(frame, BLEAccessAddressLength + 2, advAddr, 0, 6);

            if(!advAddr.SequenceEqual(pendingPeerAddr))
            {
                return;
            }

            SendConnectInd(advAddr);

            connectionHandle = nextConnectionHandle++;
            state = LinkLayerState.Connected;

            this.Log(LogLevel.Info, "Connected to {0}, handle=0x{1:X4}",
                BitConverter.ToString(advAddr), connectionHandle);

            SendLEConnectionComplete(
                0x00,
                connectionHandle,
                pendingPeerAddrType,
                pendingPeerAddr,
                pendingConnInterval,
                pendingConnLatency,
                pendingSupervisionTimeout);
        }

        // ── Data PDU Handling (Connected) ─────────────────────────

        private void HandleDataPDU(byte[] frame)
        {
            if(frame.Length < BLEAccessAddressLength + 2)
            {
                return;
            }

            byte header = frame[BLEAccessAddressLength];
            byte length = frame[BLEAccessAddressLength + 1];

            byte llid = (byte)(header & 0x03);

            if(llid == 0x01 || llid == 0x02)
            {
                // L2CAP data → forward as ACL
                if(frame.Length < BLEAccessAddressLength + 2 + length)
                {
                    return;
                }

                byte[] l2capData = new byte[length];
                Array.Copy(frame, BLEAccessAddressLength + 2, l2capData, 0, length);

                SendACLData(connectionHandle, l2capData);
            }
            else if(llid == 0x03)
            {
                this.Log(LogLevel.Debug, "LL Control PDU received, opcode=0x{0:X2}",
                    frame.Length > BLEAccessAddressLength + 2 ? frame[BLEAccessAddressLength + 2] : 0);
            }
        }

        private void ProcessACLData(byte[] packet)
        {
            // ACL packet: [0]=0x02, [1-2]=handle|flags, [3-4]=length, [5+]=data
            if(packet.Length < 5)
            {
                return;
            }

            ushort handleAndFlags = (ushort)(packet[1] | (packet[2] << 8));
            ushort handle = (ushort)(handleAndFlags & 0x0FFF);
            ushort dataLen = (ushort)(packet[3] | (packet[4] << 8));

            if(packet.Length < 5 + dataLen)
            {
                return;
            }

            byte[] data = new byte[dataLen];
            Array.Copy(packet, 5, data, 0, dataLen);

            this.Log(LogLevel.Debug, "ACL TX handle=0x{0:X4} len={1}", handle, dataLen);

            // Build a BLE data PDU and send via radio
            // LLID=0x02 for start of L2CAP, PB flag
            var blePdu = new List<byte>();
            // Access address (advertising AA for simplicity in initial impl;
            // in a full implementation this would be the connection's AA)
            blePdu.AddRange(BLEAdvertisingAccessAddress);
            blePdu.Add(0x02); // header: LLID=0x02 (start of L2CAP), NESN=0, SN=0, MD=0
            blePdu.Add((byte)dataLen);
            blePdu.AddRange(data);

            var frameSent = FrameSent;
            if(frameSent != null)
            {
                frameSent(this, blePdu.ToArray());
            }

            SendNumberOfCompletedPackets(handle, 1);
        }

        // ── HCI Event Builders ────────────────────────────────────

        private void SendCommandComplete(ushort opcode, byte[] returnParams)
        {
            var evt = new List<byte>();
            evt.Add(HCIPacketType.Event);
            evt.Add(HCIEventCode.CommandComplete);
            evt.Add((byte)(3 + returnParams.Length)); // parameter total length
            evt.Add(0x01); // Num_HCI_Command_Packets
            evt.Add((byte)(opcode & 0xFF));
            evt.Add((byte)(opcode >> 8));
            evt.AddRange(returnParams);

#if DEBUG_PACKETS
            this.Log(LogLevel.Debug, "HCI TX: {0}", BitConverter.ToString(evt.ToArray()));
#endif
            server.Send(evt);
        }

        private void SendCommandStatus(ushort opcode, byte status)
        {
            var evt = new List<byte>();
            evt.Add(HCIPacketType.Event);
            evt.Add(HCIEventCode.CommandStatus);
            evt.Add(0x04); // parameter total length
            evt.Add(status);
            evt.Add(0x01); // Num_HCI_Command_Packets
            evt.Add((byte)(opcode & 0xFF));
            evt.Add((byte)(opcode >> 8));

            server.Send(evt);
        }

        private void SendAdvertisingReport(byte eventType, byte addrType, byte[] addr, byte[] data)
        {
            var paramBody = new List<byte>();
            paramBody.Add(HCILESubevent.AdvertisingReport);
            paramBody.Add(0x01);          // num reports
            paramBody.Add(eventType);     // event type (ADV_IND=0, etc.)
            paramBody.Add(addrType);      // address type
            paramBody.AddRange(addr);     // 6-byte address
            paramBody.Add((byte)data.Length);
            paramBody.AddRange(data);
            paramBody.Add(0xCE);          // RSSI: -50 dBm (signed byte)

            var evt = new List<byte>();
            evt.Add(HCIPacketType.Event);
            evt.Add(HCIEventCode.LEMetaEvent);
            evt.Add((byte)paramBody.Count);
            evt.AddRange(paramBody);

            server.Send(evt);
        }

        private void SendLEConnectionComplete(byte status, ushort handle, byte peerAddrType,
            byte[] peerAddr, ushort interval, ushort latency, ushort timeout)
        {
            var paramBody = new List<byte>();
            paramBody.Add(HCILESubevent.ConnectionComplete);
            paramBody.Add(status);
            paramBody.Add((byte)(handle & 0xFF));
            paramBody.Add((byte)(handle >> 8));
            paramBody.Add(0x00); // role: master
            paramBody.Add(peerAddrType);
            paramBody.AddRange(peerAddr);
            paramBody.Add((byte)(interval & 0xFF));
            paramBody.Add((byte)(interval >> 8));
            paramBody.Add((byte)(latency & 0xFF));
            paramBody.Add((byte)(latency >> 8));
            paramBody.Add((byte)(timeout & 0xFF));
            paramBody.Add((byte)(timeout >> 8));
            paramBody.Add(0x00); // master clock accuracy

            var evt = new List<byte>();
            evt.Add(HCIPacketType.Event);
            evt.Add(HCIEventCode.LEMetaEvent);
            evt.Add((byte)paramBody.Count);
            evt.AddRange(paramBody);

            server.Send(evt);
        }

        private void SendACLData(ushort handle, byte[] data)
        {
            var packet = new List<byte>();
            packet.Add(HCIPacketType.ACLData);
            // handle with PB=0x02 (first automatically-flushable), BC=0x00
            ushort handleAndFlags = (ushort)(handle | (0x02 << 12));
            packet.Add((byte)(handleAndFlags & 0xFF));
            packet.Add((byte)(handleAndFlags >> 8));
            packet.Add((byte)(data.Length & 0xFF));
            packet.Add((byte)((data.Length >> 8) & 0xFF));
            packet.AddRange(data);

            server.Send(packet);
        }

        private void SendNumberOfCompletedPackets(ushort handle, ushort count)
        {
            var evt = new List<byte>();
            evt.Add(HCIPacketType.Event);
            evt.Add(HCIEventCode.NumberOfCompletedPackets);
            evt.Add(0x05); // parameter total length
            evt.Add(0x01); // Number_of_Handles
            evt.Add((byte)(handle & 0xFF));
            evt.Add((byte)(handle >> 8));
            evt.Add((byte)(count & 0xFF));
            evt.Add((byte)(count >> 8));

            server.Send(evt);
        }

        // ── CONNECT_IND PDU ──────────────────────────────────────

        private void SendConnectInd(byte[] peerAddr)
        {
            // Build CONNECT_IND PDU and transmit via FrameSent
            var pdu = new List<byte>();

            // Access address (advertising)
            pdu.AddRange(BLEAdvertisingAccessAddress);

            // PDU header: type=CONNECT_IND (0x05), TxAdd based on our addr, RxAdd=0
            pdu.Add(0x05);
            // PDU length: InitA(6) + AdvA(6) + LLData(22) = 34
            pdu.Add(34);

            // InitA: our address
            pdu.AddRange(bdAddr);
            // AdvA: peer address
            pdu.AddRange(peerAddr);

            // LLData (22 bytes):
            // Access Address for data channel (4 bytes) — use a fixed value
            pdu.AddRange(new byte[] { 0x01, 0x02, 0x03, 0x04 });
            // CRC Init (3 bytes)
            pdu.AddRange(new byte[] { 0x55, 0x55, 0x55 });
            // WinSize (1 byte)
            pdu.Add(0x03);
            // WinOffset (2 bytes)
            pdu.Add(0x00);
            pdu.Add(0x00);
            // Interval (2 bytes)
            pdu.Add((byte)(pendingConnInterval & 0xFF));
            pdu.Add((byte)(pendingConnInterval >> 8));
            // Latency (2 bytes)
            pdu.Add((byte)(pendingConnLatency & 0xFF));
            pdu.Add((byte)(pendingConnLatency >> 8));
            // Timeout (2 bytes)
            pdu.Add((byte)(pendingSupervisionTimeout & 0xFF));
            pdu.Add((byte)(pendingSupervisionTimeout >> 8));
            // Channel Map (5 bytes) — all 37 data channels enabled
            pdu.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x1F });
            // Hop + SCA (1 byte) — hop=5, SCA=0
            pdu.Add(0x05);

            var frameSent = FrameSent;
            if(frameSent != null)
            {
                frameSent(this, pdu.ToArray());
            }
        }

        // ── Channel Cycling ──────────────────────────────────────

        private void CycleAdvertisingChannel()
        {
            if(Channel == BLEAdvertisingChannel0)
            {
                Channel = BLEAdvertisingChannel1;
            }
            else if(Channel == BLEAdvertisingChannel1)
            {
                Channel = BLEAdvertisingChannel2;
            }
            else
            {
                Channel = BLEAdvertisingChannel0;
            }
        }

        // ── State ────────────────────────────────────────────────

        private LinkLayerState state;
        private HCIParseState hciState;
        private byte currentPacketType;
        private int expectedLength;
        private byte scanType;
        private bool scanFilterDuplicates;

        private ushort connectionHandle;
        private ushort nextConnectionHandle = 0x0001;

        private byte pendingPeerAddrType;
        private byte[] pendingPeerAddr;
        private ushort pendingConnInterval;
        private ushort pendingConnLatency;
        private ushort pendingSupervisionTimeout;

        private readonly int port;
        private readonly byte[] bdAddr;
        private readonly List<byte> buffer;
        private readonly SocketServerProvider server;

        // ── Constants ────────────────────────────────────────────

        private static readonly byte[] BLEAdvertisingAccessAddress = { 0xD6, 0xBE, 0x89, 0x8E };

        private const int BLEAccessAddressLength = 4;
        private const int BLEAdvertisingChannel0 = 37;
        private const int BLEAdvertisingChannel1 = 38;
        private const int BLEAdvertisingChannel2 = 39;

        private static class HCIPacketType
        {
            public const byte Command = 0x01;
            public const byte ACLData = 0x02;
            public const byte Event = 0x04;
        }

        private static class HCIEventCode
        {
            public const byte CommandComplete = 0x0E;
            public const byte CommandStatus = 0x0F;
            public const byte NumberOfCompletedPackets = 0x13;
            public const byte LEMetaEvent = 0x3E;
        }

        private static class HCILESubevent
        {
            public const byte ConnectionComplete = 0x01;
            public const byte AdvertisingReport = 0x02;
        }

        private static class HCIOpcode
        {
            public const ushort SetEventMask = 0x0C01;
            public const ushort Reset = 0x0C03;
            public const ushort ReadLocalSupportedCodecs = 0x0C56;
            public const ushort ReadLocalVersionInformation = 0x1001;
            public const ushort ReadLocalSupportedCommands = 0x1002;
            public const ushort ReadLocalSupportedFeatures = 0x1003;
            public const ushort ReadBDAddr = 0x1009;
            public const ushort LESetEventMask = 0x2001;
            public const ushort LEReadBufferSize = 0x2002;
            public const ushort LEReadLocalSupportedFeatures = 0x2003;
            public const ushort LESetScanParameters = 0x200B;
            public const ushort LESetScanEnable = 0x200C;
            public const ushort LECreateConnection = 0x200D;
            public const ushort LECreateConnectionCancel = 0x200E;
            public const ushort LEReadBufferSizeV2 = 0x2060;
        }

        private enum LinkLayerState
        {
            Standby,
            Scanning,
            Initiating,
            Connected
        }

        private enum HCIParseState
        {
            WaitForType,
            WaitForHeader,
            WaitForPayload
        }
    }
}
