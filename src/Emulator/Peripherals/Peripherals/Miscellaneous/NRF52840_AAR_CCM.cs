//
// Copyright (c) 2010-2025 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Linq;
using System.Security.Cryptography;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities.Crypto;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // AAR and CCM share base address 0x4000F000 on NRF52840.
    // AAR TASKS_START (0x000) overlaps with CCM TASKS_KSGEN (0x000) by design:
    // PPI channel 23 triggers "AAR Start" and PPI channel 24 triggers "CCM KSGen"
    // both at the same task address. The ENABLE register determines which is active.
    public sealed class NRF52840_AAR_CCM : BasicDoubleWordPeripheral, IKnownSize, INRFEventProvider
    {
        public NRF52840_AAR_CCM(IMachine machine) : base(machine)
        {
            IRQ = new GPIO();
            DefineRegisters();
        }

        public override void Reset()
        {
            base.Reset();
            micValid = false;
            UpdateInterrupts();
        }

        public long Size => 0x1000;

        public GPIO IRQ { get; }

        public event Action<uint> EventTriggered;

        private void DefineRegisters()
        {
            // 0x000: AAR TASKS_START / CCM TASKS_KSGEN
            Registers.TasksStartKsgen.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASKS_START/TASKS_KSGEN",
                    writeCallback: (_, value) =>
                    {
                        if(!value)
                        {
                            return;
                        }
                        if(IsAarEnabled)
                        {
                            RunAddressResolution();
                        }
                        else if(IsCcmEnabled)
                        {
                            RunKeyStreamGeneration();
                        }
                    })
                .WithReservedBits(1, 31)
            ;

            // 0x004: CCM TASKS_CRYPT
            Registers.TasksCrypt.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASKS_CRYPT",
                    writeCallback: (_, value) =>
                    {
                        if(value && IsCcmEnabled)
                        {
                            RunCrypt();
                        }
                    })
                .WithReservedBits(1, 31)
            ;

            // 0x008: AAR TASKS_STOP / CCM TASKS_STOP
            Registers.TasksStop.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASKS_STOP")
                .WithReservedBits(1, 31)
            ;

            // 0x00C: CCM TASKS_RATEOVERRIDE
            Registers.TasksRateOverride.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASKS_RATEOVERRIDE")
                .WithReservedBits(1, 31)
            ;

            // 0x100: AAR EVENTS_END / CCM EVENTS_ENDKSGEN
            Registers.EventsEndKsgen.Define(this)
                .WithFlag(0, out eventEndKsgen, name: "EVENTS_END/EVENTS_ENDKSGEN")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            // 0x104: AAR EVENTS_RESOLVED / CCM EVENTS_ENDCRYPT
            Registers.EventsResolvedEndcrypt.Define(this)
                .WithFlag(0, out eventResolvedEndcrypt, name: "EVENTS_RESOLVED/EVENTS_ENDCRYPT")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            // 0x108: AAR EVENTS_NOTRESOLVED / CCM EVENTS_ERROR
            Registers.EventsNotresolvedError.Define(this)
                .WithFlag(0, out eventNotresolvedError, name: "EVENTS_NOTRESOLVED/EVENTS_ERROR")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            // 0x200: CCM SHORTS
            Registers.Shorts.Define(this, name: "SHORTS")
                .WithFlag(0, out shortEndksgenCrypt, name: "ENDKSGEN_CRYPT")
                .WithReservedBits(1, 31)
            ;

            // 0x304: INTENSET (shared)
            Registers.InterruptEnableSet.Define(this, name: "INTENSET")
                .WithFlag(0, out interruptEndKsgenEnabled, FieldMode.Set | FieldMode.Read, name: "END/ENDKSGEN")
                .WithFlag(1, out interruptResolvedEndcryptEnabled, FieldMode.Set | FieldMode.Read, name: "RESOLVED/ENDCRYPT")
                .WithFlag(2, out interruptNotresolvedErrorEnabled, FieldMode.Set | FieldMode.Read, name: "NOTRESOLVED/ERROR")
                .WithReservedBits(3, 29)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            // 0x308: INTENCLR (shared)
            Registers.InterruptEnableClear.Define(this, name: "INTENCLR")
                .WithFlag(0,
                    writeCallback: (_, value) => interruptEndKsgenEnabled.Value &= !value,
                    valueProviderCallback: _ => interruptEndKsgenEnabled.Value, name: "END/ENDKSGEN")
                .WithFlag(1,
                    writeCallback: (_, value) => interruptResolvedEndcryptEnabled.Value &= !value,
                    valueProviderCallback: _ => interruptResolvedEndcryptEnabled.Value, name: "RESOLVED/ENDCRYPT")
                .WithFlag(2,
                    writeCallback: (_, value) => interruptNotresolvedErrorEnabled.Value &= !value,
                    valueProviderCallback: _ => interruptNotresolvedErrorEnabled.Value, name: "NOTRESOLVED/ERROR")
                .WithReservedBits(3, 29)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            // 0x400: AAR STATUS / CCM MICSTATUS
            Registers.StatusMicstatus.Define(this, name: "STATUS/MICSTATUS")
                .WithValueField(0, 4, FieldMode.Read, name: "STATUS/MICSTATUS",
                    valueProviderCallback: _ =>
                    {
                        if(IsAarEnabled)
                        {
                            return aarMatchIndex;
                        }
                        return micValid ? 1u : 0u;
                    })
                .WithReservedBits(4, 28)
            ;

            // 0x500: AAR ENABLE / CCM ENABLE
            Registers.Enable.Define(this, name: "ENABLE")
                .WithValueField(0, 2, out enableField, name: "ENABLE")
                .WithReservedBits(2, 30)
            ;

            // 0x504: AAR NIRK / CCM MODE
            Registers.NirkMode.Define(this, name: "NIRK/MODE")
                .WithValueField(0, 5, out nirkModeField, name: "NIRK/MODE")
                .WithReservedBits(5, 3)
                .WithValueField(8, 1, out ccmDataRate, name: "DATARATE")
                .WithReservedBits(9, 15)
                .WithValueField(24, 1, out ccmLengthMode, name: "LENGTH")
                .WithReservedBits(25, 7)
            ;

            // 0x508: AAR IRKPTR / CCM CNFPTR
            Registers.IrkptrCnfptr.Define(this, name: "IRKPTR/CNFPTR")
                .WithValueField(0, 32, out irkptrCnfptr, name: "IRKPTR/CNFPTR")
            ;

            // 0x50C: CCM INPTR
            Registers.Inptr.Define(this, name: "INPTR")
                .WithValueField(0, 32, out inptr, name: "INPTR")
            ;

            // 0x510: AAR ADDRPTR / CCM OUTPTR
            Registers.AddrptrOutptr.Define(this, name: "ADDRPTR/OUTPTR")
                .WithValueField(0, 32, out addrptrOutptr, name: "ADDRPTR/OUTPTR")
            ;

            // 0x514: AAR SCRATCHPTR / CCM SCRATCHPTR
            Registers.Scratchptr.Define(this, name: "SCRATCHPTR")
                .WithValueField(0, 32, out scratchptr, name: "SCRATCHPTR")
            ;

            // 0x518: CCM MAXPACKETSIZE
            Registers.MaxPacketSize.Define(this, name: "MAXPACKETSIZE")
                .WithValueField(0, 8, out maxPacketSize, name: "MAXPACKETSIZE")
                .WithReservedBits(8, 24)
            ;

            // 0x51C: CCM RATEOVERRIDE
            Registers.RateOverride.Define(this, name: "RATEOVERRIDE")
                .WithValueField(0, 2, name: "RATEOVERRIDE")
                .WithReservedBits(2, 30)
            ;
        }

        private bool IsAarEnabled => enableField.Value == AarEnableValue;
        private bool IsCcmEnabled => enableField.Value == CcmEnableValue;
        private bool IsExtendedLength => ccmLengthMode.Value == 1;
        private bool IsEncryptMode => (nirkModeField.Value & 1) == 0;

        private void RunAddressResolution()
        {
            var nIrks = (int)nirkModeField.Value;
            if(nIrks == 0)
            {
                nIrks = 1;
            }

            this.Log(LogLevel.Debug, "AAR: Starting address resolution with {0} IRKs, ADDRPTR=0x{1:X}, IRKPTR=0x{2:X}",
                nIrks, addrptrOutptr.Value, irkptrCnfptr.Value);

            // Read the 6-byte address from the packet pointed to by ADDRPTR.
            // The address is the BLE address (6 bytes).
            // Per the datasheet, ADDRPTR points to the start of the packet; the address
            // starts after S0 (1 byte) + LENGTH (1 byte), so at offset 2 for a standard BLE packet.
            // However, the AAR actually reads 6 bytes starting from the address portion.
            // The Bluetooth spec says: hash = ah(IRK, prand) where prand is the upper 3 bytes
            // of the resolvable address, and hash is the lower 3 bytes.
            var addrBytes = sysbus.ReadBytes(addrptrOutptr.Value + 3, 6);
            var hash = new byte[3];
            var prand = new byte[3];
            Array.Copy(addrBytes, 0, hash, 0, 3);
            Array.Copy(addrBytes, 3, prand, 0, 3);

            aarMatchIndex = 0;
            var resolved = false;

            for(int i = 0; i < nIrks; i++)
            {
                var irk = sysbus.ReadBytes(irkptrCnfptr.Value + (ulong)(i * IrkSize), IrkSize);

                // ah(IRK, prand) = e(IRK, prand') mod 2^24
                // prand' is prand zero-padded to 16 bytes (MSB)
                var plaintextBlock = new byte[16];
                plaintextBlock[13] = prand[2];
                plaintextBlock[14] = prand[1];
                plaintextBlock[15] = prand[0];

                var block = Block.UsingBytes(plaintextBlock);
                using(var aes = AesProvider.GetEcbProvider(irk))
                {
                    aes.EncryptBlockInSitu(block);
                }

                // Compare lowest 3 bytes of result (big-endian output) with hash
                var computedHash = new byte[3];
                computedHash[0] = block.Buffer[15];
                computedHash[1] = block.Buffer[14];
                computedHash[2] = block.Buffer[13];

                if(computedHash[0] == hash[0] && computedHash[1] == hash[1] && computedHash[2] == hash[2])
                {
                    aarMatchIndex = (uint)i;
                    resolved = true;
                    this.Log(LogLevel.Debug, "AAR: Address resolved using IRK index {0}", i);
                    break;
                }
            }

            eventEndKsgen.Value = true;
            EventTriggered?.Invoke((uint)Registers.EventsEndKsgen);

            if(resolved)
            {
                eventResolvedEndcrypt.Value = true;
                EventTriggered?.Invoke((uint)Registers.EventsResolvedEndcrypt);
            }
            else
            {
                this.Log(LogLevel.Debug, "AAR: Address not resolved after checking {0} IRKs", nIrks);
                eventNotresolvedError.Value = true;
                EventTriggered?.Invoke((uint)Registers.EventsNotresolvedError);
            }

            UpdateInterrupts();
        }

        private void RunKeyStreamGeneration()
        {
            this.Log(LogLevel.Debug, "CCM: Starting key-stream generation, CNFPTR=0x{0:X}", irkptrCnfptr.Value);

            // Read the CCM data structure: 16-byte key + 8-byte packet counter + 1-byte direction + 8-byte IV
            var key = sysbus.ReadBytes(irkptrCnfptr.Value, KeySize);
            var pktCtr = sysbus.ReadBytes(irkptrCnfptr.Value + KeySize, 8);
            var directionByte = sysbus.ReadBytes(irkptrCnfptr.Value + KeySize + 8, 1);
            var iv = sysbus.ReadBytes(irkptrCnfptr.Value + KeySize + 9, 8);

            // Build the 13-byte NONCE per BLE spec:
            // Bytes 0-4: packetCounter (5 bytes, little-endian from pktCtr[0..4])
            // Byte 5: direction bit
            // Bytes 6-13: IV
            ccmNonce = new byte[13];
            Array.Copy(pktCtr, 0, ccmNonce, 0, 5);
            ccmNonce[4] = (byte)(directionByte[0] & 0x7F);
            Array.Copy(iv, 0, ccmNonce, 5, 8);

            ccmKey = new byte[KeySize];
            Array.Copy(key, 0, ccmKey, 0, KeySize);

            var maxLen = IsExtendedLength ? (int)maxPacketSize.Value : DefaultMaxPayloadLength;

            // Generate key-stream blocks: A_i for i = 0..ceil(maxLen/16)
            // A_i = Flags(1) || Nonce(13) || Counter(2)
            var numBlocks = (maxLen + MicSize + AesBlockSize - 1) / AesBlockSize + 1;
            ccmKeyStream = new byte[numBlocks * AesBlockSize];

            using(var aes = AesProvider.GetEcbProvider(ccmKey))
            {
                for(int i = 0; i <= numBlocks; i++)
                {
                    var aBlock = new byte[AesBlockSize];
                    aBlock[0] = 0x01; // Flags for counter mode
                    Array.Copy(ccmNonce, 0, aBlock, 1, 13);
                    aBlock[14] = (byte)((i >> 8) & 0xFF);
                    aBlock[15] = (byte)(i & 0xFF);

                    var block = Block.UsingBytes(aBlock);
                    aes.EncryptBlockInSitu(block);

                    if(i > 0 && (i - 1) * AesBlockSize < ccmKeyStream.Length)
                    {
                        var copyLen = Math.Min(AesBlockSize, ccmKeyStream.Length - (i - 1) * AesBlockSize);
                        Array.Copy(block.Buffer, 0, ccmKeyStream, (i - 1) * AesBlockSize, copyLen);
                    }

                    if(i == 0)
                    {
                        ccmA0 = new byte[AesBlockSize];
                        Array.Copy(block.Buffer, 0, ccmA0, 0, AesBlockSize);
                    }
                }
            }

            eventEndKsgen.Value = true;
            EventTriggered?.Invoke((uint)Registers.EventsEndKsgen);
            UpdateInterrupts();

            if(shortEndksgenCrypt.Value)
            {
                RunCrypt();
            }
        }

        private void RunCrypt()
        {
            if(ccmKey == null || ccmNonce == null)
            {
                this.Log(LogLevel.Warning, "CCM: CRYPT triggered without prior KSGEN");
                eventNotresolvedError.Value = true;
                EventTriggered?.Invoke((uint)Registers.EventsNotresolvedError);
                UpdateInterrupts();
                return;
            }

            var isEncrypt = IsEncryptMode;
            this.Log(LogLevel.Debug, "CCM: Starting {0}, INPTR=0x{1:X}, OUTPTR=0x{2:X}",
                isEncrypt ? "encryption" : "decryption", inptr.Value, addrptrOutptr.Value);

            // Read input packet: HEADER(1) + LENGTH(1) + RFU(1) + PAYLOAD(LENGTH)
            var header = sysbus.ReadByte(inptr.Value);
            var length = sysbus.ReadByte(inptr.Value + 1);

            if(length == 0)
            {
                // Empty packets pass through unmodified and always pass MIC check
                sysbus.WriteByte(addrptrOutptr.Value, header);
                sysbus.WriteByte(addrptrOutptr.Value + 1, length);
                sysbus.WriteByte(addrptrOutptr.Value + 2, 0); // RFU
                micValid = true;

                eventResolvedEndcrypt.Value = true;
                EventTriggered?.Invoke((uint)Registers.EventsResolvedEndcrypt);
                UpdateInterrupts();
                return;
            }

            if(isEncrypt)
            {
                var payload = sysbus.ReadBytes(inptr.Value + 3, length);

                // XOR payload with key-stream
                var encrypted = new byte[length];
                for(int i = 0; i < length; i++)
                {
                    encrypted[i] = (byte)(payload[i] ^ ccmKeyStream[i]);
                }

                // Compute MIC (CBC-MAC)
                var mic = ComputeMic(header, payload);

                // Write output: HEADER + (LENGTH+4) + RFU + encrypted payload + MIC
                sysbus.WriteByte(addrptrOutptr.Value, header);
                sysbus.WriteByte(addrptrOutptr.Value + 1, (byte)(length + MicSize));
                sysbus.WriteByte(addrptrOutptr.Value + 2, 0); // RFU
                sysbus.WriteBytes(encrypted, addrptrOutptr.Value + 3);
                sysbus.WriteBytes(mic, addrptrOutptr.Value + 3 + (ulong)length);

                micValid = true;
            }
            else
            {
                // Decryption: input length includes MIC (4 bytes)
                if(length < MicSize + 1)
                {
                    // Packets with payload < 1 byte + MIC fail MIC check
                    micValid = false;
                    sysbus.WriteByte(addrptrOutptr.Value, header);
                    sysbus.WriteByte(addrptrOutptr.Value + 1, 0);
                    sysbus.WriteByte(addrptrOutptr.Value + 2, 0);

                    eventResolvedEndcrypt.Value = true;
                    EventTriggered?.Invoke((uint)Registers.EventsResolvedEndcrypt);
                    UpdateInterrupts();
                    return;
                }

                var payloadLen = length - MicSize;
                var encPayload = sysbus.ReadBytes(inptr.Value + 3, payloadLen);
                var receivedMic = sysbus.ReadBytes(inptr.Value + 3 + (ulong)payloadLen, MicSize);

                // XOR encrypted payload with key-stream to get plaintext
                var decrypted = new byte[payloadLen];
                for(int i = 0; i < payloadLen; i++)
                {
                    decrypted[i] = (byte)(encPayload[i] ^ ccmKeyStream[i]);
                }

                // Verify MIC
                var computedMic = ComputeMic(header, decrypted);
                micValid = computedMic.SequenceEqual(receivedMic);

                if(!micValid)
                {
                    this.Log(LogLevel.Warning, "CCM: MIC check failed");
                }

                // Write output: HEADER + (LENGTH-4) + RFU + decrypted payload
                sysbus.WriteByte(addrptrOutptr.Value, header);
                sysbus.WriteByte(addrptrOutptr.Value + 1, (byte)payloadLen);
                sysbus.WriteByte(addrptrOutptr.Value + 2, 0); // RFU
                sysbus.WriteBytes(decrypted, addrptrOutptr.Value + 3);
            }

            eventResolvedEndcrypt.Value = true;
            EventTriggered?.Invoke((uint)Registers.EventsResolvedEndcrypt);
            UpdateInterrupts();
        }

        private byte[] ComputeMic(byte header, byte[] payload)
        {
            // CBC-MAC per BLE/RFC3610:
            // B_0 = Flags(1) || Nonce(13) || l(m)(2)
            // B_1 = a_len(2) || a_data (header + padding)
            // B_2..n = payload blocks
            var b0 = new byte[AesBlockSize];
            b0[0] = 0x49; // Flags: Adata=1, M=4bytes(t=3), L=2bytes(L=1)
            Array.Copy(ccmNonce, 0, b0, 1, 13);
            b0[14] = 0;
            b0[15] = (byte)payload.Length;

            // X_0 = E(K, B_0)
            var x = new byte[AesBlockSize];
            using(var aes = AesProvider.GetEcbProvider(ccmKey))
            {
                var block = Block.UsingBytes(b0);
                aes.EncryptBlockInSitu(block);
                Array.Copy(block.Buffer, 0, x, 0, AesBlockSize);

                // B_1 = 0x0001 || header || 0x00...
                var b1 = new byte[AesBlockSize];
                b1[0] = 0x00;
                b1[1] = 0x01; // a_len = 1
                b1[2] = header;

                // X_1 = E(K, X_0 ^ B_1)
                for(int i = 0; i < AesBlockSize; i++)
                {
                    x[i] ^= b1[i];
                }
                var xBlock = Block.UsingBytes(x);
                aes.EncryptBlockInSitu(xBlock);
                Array.Copy(xBlock.Buffer, 0, x, 0, AesBlockSize);

                // Process payload blocks
                var fullBlocks = payload.Length / AesBlockSize;
                var remainder = payload.Length % AesBlockSize;

                for(int i = 0; i < fullBlocks; i++)
                {
                    for(int j = 0; j < AesBlockSize; j++)
                    {
                        x[j] ^= payload[i * AesBlockSize + j];
                    }
                    xBlock = Block.UsingBytes(x);
                    aes.EncryptBlockInSitu(xBlock);
                    Array.Copy(xBlock.Buffer, 0, x, 0, AesBlockSize);
                }

                if(remainder > 0)
                {
                    for(int j = 0; j < remainder; j++)
                    {
                        x[j] ^= payload[fullBlocks * AesBlockSize + j];
                    }
                    xBlock = Block.UsingBytes(x);
                    aes.EncryptBlockInSitu(xBlock);
                    Array.Copy(xBlock.Buffer, 0, x, 0, AesBlockSize);
                }
            }

            // T = first 4 bytes of X_final XOR first 4 bytes of S_0 (ccmA0)
            var mic = new byte[MicSize];
            for(int i = 0; i < MicSize; i++)
            {
                mic[i] = (byte)(x[i] ^ ccmA0[i]);
            }

            return mic;
        }

        private void UpdateInterrupts()
        {
            var flag = false;
            flag |= interruptEndKsgenEnabled.Value && eventEndKsgen.Value;
            flag |= interruptResolvedEndcryptEnabled.Value && eventResolvedEndcrypt.Value;
            flag |= interruptNotresolvedErrorEnabled.Value && eventNotresolvedError.Value;
            IRQ.Set(flag);
        }

        // Events
        private IFlagRegisterField eventEndKsgen;
        private IFlagRegisterField eventResolvedEndcrypt;
        private IFlagRegisterField eventNotresolvedError;

        // Interrupts
        private IFlagRegisterField interruptEndKsgenEnabled;
        private IFlagRegisterField interruptResolvedEndcryptEnabled;
        private IFlagRegisterField interruptNotresolvedErrorEnabled;

        // Shortcuts
        private IFlagRegisterField shortEndksgenCrypt;

        // Configuration
        private IValueRegisterField enableField;
        private IValueRegisterField nirkModeField;
        private IValueRegisterField irkptrCnfptr;
        private IValueRegisterField inptr;
        private IValueRegisterField addrptrOutptr;
        private IValueRegisterField scratchptr;
        private IValueRegisterField maxPacketSize;
        private IValueRegisterField ccmDataRate;
        private IValueRegisterField ccmLengthMode;

        // AAR state
        private uint aarMatchIndex;

        // CCM state
        private byte[] ccmKey;
        private byte[] ccmNonce;
        private byte[] ccmKeyStream;
        private byte[] ccmA0;
        private bool micValid;

        private const ulong AarEnableValue = 3;
        private const ulong CcmEnableValue = 2;
        private const int KeySize = 16;
        private const int AesBlockSize = 16;
        private const int IrkSize = 16;
        private const int MicSize = 4;
        private const int DefaultMaxPayloadLength = 27;

        private enum Registers
        {
            TasksStartKsgen = 0x000,
            TasksCrypt = 0x004,
            TasksStop = 0x008,
            TasksRateOverride = 0x00C,
            EventsEndKsgen = 0x100,
            EventsResolvedEndcrypt = 0x104,
            EventsNotresolvedError = 0x108,
            Shorts = 0x200,
            InterruptEnableSet = 0x304,
            InterruptEnableClear = 0x308,
            StatusMicstatus = 0x400,
            Enable = 0x500,
            NirkMode = 0x504,
            IrkptrCnfptr = 0x508,
            Inptr = 0x50C,
            AddrptrOutptr = 0x510,
            Scratchptr = 0x514,
            MaxPacketSize = 0x518,
            RateOverride = 0x51C,
        }
    }
}
