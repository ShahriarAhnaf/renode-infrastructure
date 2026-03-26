//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

// uncomment the line below to get more detailed logs
// note: dumping packets may severely lower performance
// #define DEBUG_PACKETS

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Debugging;
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
            cancellationToken = new CancellationTokenSource();
        }

        public void Dispose()
        {
            Shutdown();
        }

        public void Run()
        {
            server.Start(port);
        }

        public void Reset()
        {
            state = State.WaitForType;
            linkState = LinkState.Standby;
            currentPacketType = 0;
            expectedLength = 0;
            connectionHandle = 0;
            scanFilterDuplicates = false;

            buffer.Clear();
            cancellationToken = new CancellationTokenSource();
        }

        public int Channel { get; set; }

        public event Action<IRadio, byte[]> FrameSent;

        public void ReceiveFrame(byte[] frame, IRadio sender)
        {
            switch(linkState)
            {
            case LinkState.Scanning:
            {
                HandleAdvertisingPDU(frame);
                break;
            }

            case LinkState.Initiating:
            {
                HandleAdvertisingPDUWhileInitiating(frame);
                break;
            }

            case LinkState.Connected:
            {
                HandleDataPDU(frame);
                break;
            }
            }
        }

        private void SendResponse(IEnumerable<byte> bytes)
        {
            server.Send(bytes);

#if DEBUG_PACKETS
            this.Log(LogLevel.Noisy, "Count {0}: {1}", bytes.Count(), Misc.PrettyPrintCollectionHex(bytes));
#endif
        }

        private void Shutdown()
        {
            this.Log(LogLevel.Info, "Shutting down the server...");

            cancellationToken.Cancel();
            server.Stop();
            buffer.Clear();
        }

        private void HandleIncomingData(int b)
        {
#if DEBUG_PACKETS
            this.Log(LogLevel.Noisy, "Incoming byte: 0x{0:X}; state = {1}; buffer size = {2}", b, state, buffer.Count);
#endif

            if(b < 0)
            {
                Reset();
                return;
            }

            buffer.Add((byte)b);

            switch(state)
            {
            case State.WaitForType:
            {
                currentPacketType = (byte)b;
                switch(b)
                {
                case HCIPacketIndicator.Command:
                case HCIPacketIndicator.ACLData:
                {
                    state = State.WaitForHeader;
                    break;
                }

                default:
                {
                    this.Log(LogLevel.Error, "Unexpected HCI packet type: 0x{0:X}", b);
                    Shutdown();
                    break;
                }
                }
                break;
            }

            case State.WaitForHeader:
            {
                if(currentPacketType == HCIPacketIndicator.Command && buffer.Count >= HCICommandHeaderLength)
                {
                    expectedLength = HCICommandHeaderLength + buffer[3];
                    state = State.WaitForPayload;
                }
                else if(currentPacketType == HCIPacketIndicator.ACLData && buffer.Count >= HCIACLHeaderLength)
                {
                    expectedLength = HCIACLHeaderLength + (buffer[3] | (buffer[4] << 8));
                    state = State.WaitForPayload;
                }
                break;
            }

            case State.WaitForPayload:
            {
                DebugHelper.Assert(buffer.Count <= expectedLength);
                if(buffer.Count == expectedLength)
                {
                    var packet = buffer.ToArray();
                    buffer.Clear();

                    if(currentPacketType == HCIPacketIndicator.Command)
                    {
                        HandleHCICommand(packet);
                    }
                    else if(currentPacketType == HCIPacketIndicator.ACLData)
                    {
                        HandleACLDataFromHost(packet);
                    }

                    state = State.WaitForType;
                }
                break;
            }

            default:
                throw new ArgumentException(string.Format("Unexpected state: {0}", state));
            }
        }

        private void HandleHCICommand(byte[] packet)
        {
            DebugHelper.Assert(packet.Length >= HCICommandHeaderLength);
            var opcode = (ushort)(packet[1] | (packet[2] << 8));
            var paramLen = packet[3];
            var parameters = new byte[paramLen];
            if(paramLen > 0)
            {
                Array.Copy(packet, 4, parameters, 0, paramLen);
            }

            this.Log(LogLevel.Debug, "HCI Command: opcode=0x{0:X4}, paramLen={1}", opcode, paramLen);

            switch(opcode)
            {
            case HCIOpcode.Reset:
            {
                linkState = LinkState.Standby;
                connectionHandle = 0;
                SendCommandComplete(opcode, new byte[] { 0x00 });
                break;
            }

            case HCIOpcode.ReadLocalVersionInformation:
            {
                SendCommandComplete(opcode, new byte[]
                {
                    0x00,       // status
                    0x09,       // HCI version (BT 5.0)
                    0x00, 0x00, // HCI revision
                    0x09,       // LMP version
                    0xFF, 0x00, // manufacturer (0x00FF)
                    0x00, 0x00  // LMP subversion
                });
                break;
            }

            case HCIOpcode.ReadLocalSupportedCommands:
            {
                var cmds = new byte[65];
                cmds[0] = 0x00;
                cmds[1] |= 0x20;  // Set Event Mask
                cmds[6] |= 0x40;  // Reset
                cmds[15] |= 0x08; // Read Local Version Information
                cmds[15] |= 0x20; // Read Local Supported Features
                cmds[15] |= 0x40; // Read Local Supported Commands
                cmds[16] |= 0x02; // Read BD_ADDR
                cmds[26] |= 0x01; // LE Set Event Mask
                cmds[26] |= 0x02; // LE Read Buffer Size
                cmds[26] |= 0x04; // LE Read Local Supported Features
                cmds[27] |= 0x04; // LE Set Scan Parameters
                cmds[27] |= 0x08; // LE Set Scan Enable
                cmds[27] |= 0x10; // LE Create Connection
                cmds[27] |= 0x20; // LE Create Connection Cancel
                SendCommandComplete(opcode, cmds);
                break;
            }

            case HCIOpcode.ReadLocalSupportedFeatures:
            {
                var features = new byte[9];
                features[0] = 0x00;
                features[5] |= 0x40; // LE Supported (Controller)
                SendCommandComplete(opcode, features);
                break;
            }

            case HCIOpcode.ReadBDAddr:
            {
                var resp = new byte[7];
                resp[0] = 0x00;
                Array.Copy(bdAddr, 0, resp, 1, 6);
                SendCommandComplete(opcode, resp);
                break;
            }

            case HCIOpcode.SetEventMask:
            case HCIOpcode.LESetEventMask:
            {
                SendCommandComplete(opcode, new byte[] { 0x00 });
                break;
            }

            case HCIOpcode.LEReadBufferSize:
            {
                SendCommandComplete(opcode, new byte[]
                {
                    0x00,       // status
                    0xFB, 0x00, // LE ACL Data Packet Length (251)
                    0x08        // Total Num LE ACL Data Packets
                });
                break;
            }

            case HCIOpcode.LEReadBufferSizeV2:
            {
                SendCommandComplete(opcode, new byte[]
                {
                    0x00,       // status
                    0xFB, 0x00, // LE ACL Data Packet Length (251)
                    0x08,       // Total Num LE ACL Data Packets
                    0x00, 0x00, // ISO Data Packet Length
                    0x00        // Total Num ISO Data Packets
                });
                break;
            }

            case HCIOpcode.LEReadLocalSupportedFeatures:
            {
                var features = new byte[9];
                features[0] = 0x00;
                features[1] |= 0x01; // LE Encryption
                SendCommandComplete(opcode, features);
                break;
            }

            case HCIOpcode.ReadLocalSupportedCodecs:
            {
                SendCommandComplete(opcode, new byte[]
                {
                    0x00, // status
                    0x00, // Number_of_Supported_Standard_Codecs
                    0x00  // Number_of_Supported_Vendor_Specific_Codecs
                });
                break;
            }

            case HCIOpcode.LESetScanParameters:
            {
                if(parameters.Length >= 1)
                {
                    scanType = parameters[0];
                }
                SendCommandComplete(opcode, new byte[] { 0x00 });
                break;
            }

            case HCIOpcode.LESetScanEnable:
            {
                var enable = parameters.Length >= 1 && parameters[0] == 0x01;
                scanFilterDuplicates = parameters.Length >= 2 && parameters[1] == 0x01;

                if(enable)
                {
                    linkState = LinkState.Scanning;
                    Channel = BLEAdvertisingChannelFirst;
                    this.Log(LogLevel.Info, "BLE scanning enabled on channel {0}", Channel);
                }
                else
                {
                    linkState = LinkState.Standby;
                    this.Log(LogLevel.Info, "BLE scanning disabled");
                }
                SendCommandComplete(opcode, new byte[] { 0x00 });
                break;
            }

            case HCIOpcode.LECreateConnection:
            {
                if(parameters.Length < LECreateConnectionParametersLength)
                {
                    this.Log(LogLevel.Warning, "LE_Create_Connection: insufficient parameters ({0} bytes)", parameters.Length);
                    SendCommandStatus(opcode, 0x12);
                    break;
                }

                pendingPeerAddrType = parameters[5];
                pendingPeerAddr = new byte[6];
                Array.Copy(parameters, 6, pendingPeerAddr, 0, 6);
                pendingConnInterval = (ushort)(parameters[13] | (parameters[14] << 8));
                pendingConnLatency = (ushort)(parameters[17] | (parameters[18] << 8));
                pendingSupervisionTimeout = (ushort)(parameters[19] | (parameters[20] << 8));

                linkState = LinkState.Initiating;
                Channel = BLEAdvertisingChannelFirst;

                this.Log(LogLevel.Info, "LE Create Connection to {0}, interval={1}",
                    BitConverter.ToString(pendingPeerAddr), pendingConnInterval);

                SendCommandStatus(opcode, 0x00);
                break;
            }

            case HCIOpcode.LECreateConnectionCancel:
            {
                if(linkState == LinkState.Initiating)
                {
                    linkState = LinkState.Standby;
                    SendCommandComplete(opcode, new byte[] { 0x00 });
                    SendLEConnectionCompleteEvent(0x02, 0x0000, 0x00, new byte[6], 0, 0, 0);
                }
                else
                {
                    SendCommandComplete(opcode, new byte[] { 0x0C });
                }
                break;
            }

            default:
            {
                this.Log(LogLevel.Debug, "Unhandled HCI command 0x{0:X4}, returning generic success", opcode);
                SendCommandComplete(opcode, new byte[] { 0x00 });
                break;
            }
            }
        }

        private void HandleAdvertisingPDU(byte[] frame)
        {
            if(frame.Length < BLEAccessAddressLength + 2)
            {
                return;
            }

            var pduType = (byte)(frame[BLEAccessAddressLength] & 0x0F);
            var pduLength = frame[BLEAccessAddressLength + 1];
            if(frame.Length < BLEAccessAddressLength + 2 + pduLength || pduLength < BLEAddressLength)
            {
                return;
            }

            var advAddr = new byte[BLEAddressLength];
            Array.Copy(frame, BLEAccessAddressLength + 2, advAddr, 0, BLEAddressLength);

            var advDataLength = pduLength - BLEAddressLength;
            var advData = new byte[advDataLength];
            if(advDataLength > 0)
            {
                Array.Copy(frame, BLEAccessAddressLength + 2 + BLEAddressLength, advData, 0, advDataLength);
            }

            var txAdd = (byte)((frame[BLEAccessAddressLength] >> 6) & 0x01);

            this.Log(LogLevel.Debug, "ADV PDU type=0x{0:X} from {1}, data len={2}",
                pduType, BitConverter.ToString(advAddr), advDataLength);

            SendAdvertisingReportEvent(pduType, txAdd, advAddr, advData);
            CycleAdvertisingChannel();
        }

        private void HandleAdvertisingPDUWhileInitiating(byte[] frame)
        {
            if(frame.Length < BLEAccessAddressLength + 2)
            {
                return;
            }

            var pduType = (byte)(frame[BLEAccessAddressLength] & 0x0F);
            var pduLength = frame[BLEAccessAddressLength + 1];
            if(frame.Length < BLEAccessAddressLength + 2 + pduLength || pduLength < BLEAddressLength)
            {
                return;
            }

            if(pduType != BLEAdvPDUType.AdvInd && pduType != BLEAdvPDUType.AdvDirectInd)
            {
                return;
            }

            var advAddr = new byte[BLEAddressLength];
            Array.Copy(frame, BLEAccessAddressLength + 2, advAddr, 0, BLEAddressLength);

            if(!advAddr.SequenceEqual(pendingPeerAddr))
            {
                return;
            }

            TransmitConnectIndPDU(advAddr);

            connectionHandle = nextConnectionHandle++;
            linkState = LinkState.Connected;

            this.Log(LogLevel.Info, "Connected to {0}, handle=0x{1:X4}",
                BitConverter.ToString(advAddr), connectionHandle);

            SendLEConnectionCompleteEvent(
                0x00,
                connectionHandle,
                pendingPeerAddrType,
                pendingPeerAddr,
                pendingConnInterval,
                pendingConnLatency,
                pendingSupervisionTimeout);
        }

        private void HandleDataPDU(byte[] frame)
        {
            if(frame.Length < BLEAccessAddressLength + 2)
            {
                return;
            }

            var header = frame[BLEAccessAddressLength];
            var length = frame[BLEAccessAddressLength + 1];
            var llid = (byte)(header & 0x03);

            if(llid == 0x01 || llid == 0x02)
            {
                if(frame.Length < BLEAccessAddressLength + 2 + length)
                {
                    return;
                }

                var l2capData = new byte[length];
                Array.Copy(frame, BLEAccessAddressLength + 2, l2capData, 0, length);
                SendACLDataToHost(connectionHandle, l2capData);
            }
            else if(llid == 0x03)
            {
                this.Log(LogLevel.Debug, "LL Control PDU received, opcode=0x{0:X2}",
                    frame.Length > BLEAccessAddressLength + 2 ? frame[BLEAccessAddressLength + 2] : 0);
            }
        }

        private void HandleACLDataFromHost(byte[] packet)
        {
            DebugHelper.Assert(packet.Length >= HCIACLHeaderLength);

            var handleAndFlags = (ushort)(packet[1] | (packet[2] << 8));
            var handle = (ushort)(handleAndFlags & 0x0FFF);
            var dataLen = (ushort)(packet[3] | (packet[4] << 8));

            if(packet.Length < HCIACLHeaderLength + dataLen)
            {
                this.Log(LogLevel.Warning, "ACL data packet truncated: expected {0} bytes, got {1}", HCIACLHeaderLength + dataLen, packet.Length);
                return;
            }

            var data = new byte[dataLen];
            Array.Copy(packet, HCIACLHeaderLength, data, 0, dataLen);

            this.Log(LogLevel.Debug, "ACL TX handle=0x{0:X4} len={1}", handle, dataLen);

            TransmitDataPDU(data);
            SendNumberOfCompletedPacketsEvent(handle, 1);
        }

        private void TransmitDataPDU(byte[] data)
        {
            var pdu = new List<byte>(BLEAccessAddressLength + 2 + data.Length);
            pdu.AddRange(BLEAdvertisingAccessAddress);
            pdu.Add(0x02); // LLID=0x02 (start of L2CAP)
            pdu.Add((byte)data.Length);
            pdu.AddRange(data);

            FrameSent?.Invoke(this, pdu.ToArray());
        }

        private void TransmitConnectIndPDU(byte[] peerAddr)
        {
            var pdu = new List<byte>(BLEAccessAddressLength + 2 + ConnectIndPayloadLength);
            pdu.AddRange(BLEAdvertisingAccessAddress);

            pdu.Add(BLEAdvPDUType.ConnectInd);
            pdu.Add(ConnectIndPayloadLength);

            // InitA (6 bytes)
            pdu.AddRange(bdAddr);
            // AdvA (6 bytes)
            pdu.AddRange(peerAddr);

            // LLData (22 bytes)
            pdu.AddRange(new byte[] { 0x01, 0x02, 0x03, 0x04 }); // data channel access address
            pdu.AddRange(new byte[] { 0x55, 0x55, 0x55 });       // CRC init
            pdu.Add(0x03);                                         // WinSize
            pdu.Add(0x00); pdu.Add(0x00);                         // WinOffset
            pdu.Add((byte)(pendingConnInterval & 0xFF));
            pdu.Add((byte)(pendingConnInterval >> 8));
            pdu.Add((byte)(pendingConnLatency & 0xFF));
            pdu.Add((byte)(pendingConnLatency >> 8));
            pdu.Add((byte)(pendingSupervisionTimeout & 0xFF));
            pdu.Add((byte)(pendingSupervisionTimeout >> 8));
            pdu.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x1F }); // channel map (all 37 data channels)
            pdu.Add(0x05);                                               // hop=5, SCA=0

            FrameSent?.Invoke(this, pdu.ToArray());
        }

        private void SendCommandComplete(ushort opcode, byte[] returnParams)
        {
            var evt = new List<byte>(HCIEventHeaderLength + 3 + returnParams.Length);
            evt.Add(HCIPacketIndicator.Event);
            evt.Add(HCIEventCode.CommandComplete);
            evt.Add((byte)(3 + returnParams.Length));
            evt.Add(0x01); // Num_HCI_Command_Packets
            evt.Add((byte)(opcode & 0xFF));
            evt.Add((byte)(opcode >> 8));
            evt.AddRange(returnParams);

            SendResponse(evt);
        }

        private void SendCommandStatus(ushort opcode, byte status)
        {
            var evt = new List<byte>(HCIEventHeaderLength + 4);
            evt.Add(HCIPacketIndicator.Event);
            evt.Add(HCIEventCode.CommandStatus);
            evt.Add(0x04);
            evt.Add(status);
            evt.Add(0x01); // Num_HCI_Command_Packets
            evt.Add((byte)(opcode & 0xFF));
            evt.Add((byte)(opcode >> 8));

            SendResponse(evt);
        }

        private void SendAdvertisingReportEvent(byte eventType, byte addrType, byte[] addr, byte[] data)
        {
            var paramBody = new List<byte>(4 + BLEAddressLength + 1 + data.Length + 1);
            paramBody.Add(HCILESubevent.AdvertisingReport);
            paramBody.Add(0x01); // num reports
            paramBody.Add(eventType);
            paramBody.Add(addrType);
            paramBody.AddRange(addr);
            paramBody.Add((byte)data.Length);
            paramBody.AddRange(data);
            paramBody.Add(DefaultRSSI);

            var evt = new List<byte>(HCIEventHeaderLength + paramBody.Count);
            evt.Add(HCIPacketIndicator.Event);
            evt.Add(HCIEventCode.LEMetaEvent);
            evt.Add((byte)paramBody.Count);
            evt.AddRange(paramBody);

            SendResponse(evt);
        }

        private void SendLEConnectionCompleteEvent(byte status, ushort handle, byte peerAddrType,
            byte[] peerAddr, ushort interval, ushort latency, ushort timeout)
        {
            var paramBody = new List<byte>(1 + 1 + 2 + 1 + 1 + BLEAddressLength + 2 + 2 + 2 + 1);
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

            var evt = new List<byte>(HCIEventHeaderLength + paramBody.Count);
            evt.Add(HCIPacketIndicator.Event);
            evt.Add(HCIEventCode.LEMetaEvent);
            evt.Add((byte)paramBody.Count);
            evt.AddRange(paramBody);

            SendResponse(evt);
        }

        private void SendACLDataToHost(ushort handle, byte[] data)
        {
            var packet = new List<byte>(HCIACLHeaderLength + data.Length);
            packet.Add(HCIPacketIndicator.ACLData);
            var handleAndFlags = (ushort)(handle | (0x02 << 12));
            packet.Add((byte)(handleAndFlags & 0xFF));
            packet.Add((byte)(handleAndFlags >> 8));
            packet.Add((byte)(data.Length & 0xFF));
            packet.Add((byte)((data.Length >> 8) & 0xFF));
            packet.AddRange(data);

            SendResponse(packet);
        }

        private void SendNumberOfCompletedPacketsEvent(ushort handle, ushort count)
        {
            var evt = new List<byte>(HCIEventHeaderLength + 5);
            evt.Add(HCIPacketIndicator.Event);
            evt.Add(HCIEventCode.NumberOfCompletedPackets);
            evt.Add(0x05);
            evt.Add(0x01); // Number_of_Handles
            evt.Add((byte)(handle & 0xFF));
            evt.Add((byte)(handle >> 8));
            evt.Add((byte)(count & 0xFF));
            evt.Add((byte)(count >> 8));

            SendResponse(evt);
        }

        private void CycleAdvertisingChannel()
        {
            if(Channel == BLEAdvertisingChannelFirst)
            {
                Channel = BLEAdvertisingChannelSecond;
            }
            else if(Channel == BLEAdvertisingChannelSecond)
            {
                Channel = BLEAdvertisingChannelThird;
            }
            else
            {
                Channel = BLEAdvertisingChannelFirst;
            }
        }

        private State state;
        private LinkState linkState;
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

        private CancellationTokenSource cancellationToken;

        private readonly int port;
        private readonly byte[] bdAddr;
        private readonly List<byte> buffer;
        private readonly SocketServerProvider server;

        private static readonly byte[] BLEAdvertisingAccessAddress = { 0xD6, 0xBE, 0x89, 0x8E };

        private const int BLEAccessAddressLength = 4;
        private const int BLEAddressLength = 6;
        private const int BLEAdvertisingChannelFirst = 37;
        private const int BLEAdvertisingChannelSecond = 38;
        private const int BLEAdvertisingChannelThird = 39;
        private const byte ConnectIndPayloadLength = 34; // InitA(6) + AdvA(6) + LLData(22)
        private const int LECreateConnectionParametersLength = 25;
        private const byte DefaultRSSI = 0xCE; // -50 dBm as signed byte
        private const int HCICommandHeaderLength = 4;  // type(1) + opcode(2) + paramLen(1)
        private const int HCIACLHeaderLength = 5;       // type(1) + handle(2) + dataLen(2)
        private const int HCIEventHeaderLength = 3;     // type(1) + code(1) + paramLen(1)

        private static class HCIPacketIndicator
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

        private static class BLEAdvPDUType
        {
            public const byte AdvInd = 0x00;
            public const byte AdvDirectInd = 0x01;
            public const byte ConnectInd = 0x05;
        }

        private enum State
        {
            WaitForType,
            WaitForHeader,
            WaitForPayload,
        }

        private enum LinkState
        {
            Standby,
            Scanning,
            Initiating,
            Connected,
        }
    }
}
