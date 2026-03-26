//
// Copyright (c) 2010-2023 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.I2C
{
    public class NRF52840_I2C : SimpleContainer<II2CPeripheral>, IProvidesRegisterCollection<DoubleWordRegisterCollection>, IDoubleWordPeripheral, IKnownSize
    {
        public NRF52840_I2C(IMachine machine) : base(machine)
        {
            this.machine = machine;
            IRQ = new GPIO();

            slaveToMasterBuffer = new Queue<byte>();
            masterToSlaveBuffer = new Queue<byte>();

            RegistersCollection = new DoubleWordRegisterCollection(this);
            DefineRegisters();
        }

        public override void Reset()
        {
            slaveToMasterBuffer.Clear();
            masterToSlaveBuffer.Clear();

            selectedSlave = null;
            enabled = false;
            twimMode = false;
            transmissionInProgress = false;
            legacySuspended = false;
            rxAmount = 0;
            txAmount = 0;

            RegistersCollection.Reset();
            UpdateInterrupts();
        }

        public uint ReadDoubleWord(long offset)
        {
            return RegistersCollection.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            RegistersCollection.Write(offset, value);
        }

        public GPIO IRQ { get; }

        public long Size => 0x1000;

        public DoubleWordRegisterCollection RegistersCollection { get; }

        private void DefineRegisters()
        {
            Registers.StartReceiving.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASKS_STARTRX", writeCallback: (_, val) =>
                {
                    if(!val)
                    {
                        return;
                    }

                    transmissionInProgress = true;
                    legacySuspended = false;

                    if(twimMode)
                    {
                        PerformTwimReceive();
                    }
                    else
                    {
                        if(selectedSlave == null)
                        {
                            // No slave at address — fire ANACK error like real HW
                            addressNackError.Value = true;
                            errorInterruptPending.Value = true;
                            EventTriggered?.Invoke((uint)Registers.ErrorInterruptPending);
                            UpdateInterrupts();
                        }
                        else
                        {
                            TrySendDataToSlave();
                            slaveToMasterBuffer.Clear();
                            LegacyPrefetchFromSlave();
                            LegacyDeliverNextRxByte();
                        }
                    }
                })
                .WithReservedBits(1, 31)
            ;

            Registers.StartTransmitting.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASKS_STARTTX", writeCallback: (_, val) =>
                {
                    if(!val)
                    {
                        return;
                    }

                    transmissionInProgress = true;

                    if(twimMode)
                    {
                        PerformTwimTransmit();
                    }
                    else
                    {
                        if(selectedSlave == null)
                        {
                            // No slave at address — fire ANACK error like real HW
                            addressNackError.Value = true;
                            errorInterruptPending.Value = true;
                            EventTriggered?.Invoke((uint)Registers.ErrorInterruptPending);
                            UpdateInterrupts();
                            return;
                        }
                        TrySendDataToSlave();
                        slaveToMasterBuffer.Clear();
                    }
                })
                .WithReservedBits(1, 31)
            ;

            Registers.StopTransmitting.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASKS_STOP", writeCallback: (_, val) =>
                {
                    if(!val)
                    {
                        return;
                    }

                    StopTransmission();
                })
                .WithReservedBits(1, 31)
            ;

            Registers.SuspendTransmitting.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASKS_SUSPEND", writeCallback: (_, val) =>
                {
                    if(!val)
                    {
                        return;
                    }

                    if(!transmissionInProgress)
                    {
                        return;
                    }

                    legacySuspended = true;
                    this.Log(LogLevel.Noisy, "TWI legacy: suspended");
                })
                .WithReservedBits(1, 31)
            ;

            Registers.ResumeReceiving.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASKS_RESUME", writeCallback: (_, val) =>
                {
                    if(!val)
                    {
                        return;
                    }

                    if(!transmissionInProgress)
                    {
                        return;
                    }

                    legacySuspended = false;
                    this.Log(LogLevel.Noisy, "TWI legacy: resumed");

                    if(!twimMode)
                    {
                        LegacyDeliverNextRxByte();
                    }
                    else
                    {
                        TryFillReceivedBuffer(true);
                    }
                })
                .WithReservedBits(1, 31)
            ;

            Registers.StoppedInterruptPending.Define(this)
                .WithFlag(0, out stoppedInterruptPending, name: "EVENTS_STOPPED")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.RxInterruptPending.Define(this)
                .WithFlag(0, out rxInterruptPending, name: "EVENTS_RXREADY")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.TxInterruptPending.Define(this)
                .WithFlag(0, out txInterruptPending, name: "EVENTS_TXDSENT")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.ErrorInterruptPending.Define(this)
                .WithFlag(0, out errorInterruptPending, name: "EVENTS_ERROR")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.LastRxEventPending.Define(this)
                .WithFlag(0, out lastRxEventPending, name: "EVENTS_LASTRX")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.LastTxEventPending.Define(this)
                .WithFlag(0, out lastTxEventPending, name: "EVENTS_LASTTX")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.ByteBoundaryEventPending.Define(this)
                .WithFlag(0, out bbEventPending, name: "EVENTS_BB")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.SuspendedEventPending.Define(this)
                .WithFlag(0, out suspendedEventPending, name: "EVENTS_SUSPENDED")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.ErrorSource.Define(this)
                .WithTaggedFlag("OVERRUN", 0)
                .WithFlag(1, out addressNackError, name: "ANACK")
                .WithTaggedFlag("DNACK", 2)
                .WithReservedBits(3, 29)
            ;

            Registers.Shortcuts.Define(this)
                .WithFlag(0, out byteBoundarySuspendShortcut, name: "BB_SUSPEND")
                .WithFlag(1, out byteBoundaryStopShortcut, name: "BB_STOP")
                .WithReservedBits(2, 4)
                .WithTag("LASTTX_STARTRX", 7, 1)
                .WithTag("LASTTX_SUSPEND", 8, 1)
                .WithFlag(9, out lastTxStopShortcut, name: "LASTTX_STOP")
                .WithTag("LASTRX_STARTTX", 10, 1)
                .WithTag("LASTRX_SUSPEND", 11, 1)
                .WithFlag(12, out lastRxStopShortcut, name: "LASTRX_STOP")
                .WithReservedBits(13, 19)
            ;

            Registers.SetEnableInterrupts.Define(this)
                .WithReservedBits(0, 1)
                .WithFlag(1, out stoppedInterruptEnabled, FieldMode.Read | FieldMode.Set, name: "STOPPED")
                .WithFlag(2, out rxInterruptEnabled, FieldMode.Read | FieldMode.Set, name: "RXREADY")
                .WithReservedBits(3, 4)
                .WithFlag(7, out txInterruptEnabled, FieldMode.Read | FieldMode.Set, name: "TXDSENT")
                .WithReservedBits(8, 1)
                .WithFlag(9, out errorInterruptEnabled, FieldMode.Read | FieldMode.Set, name: "ERROR")
                .WithReservedBits(10, 4)
                .WithFlag(14, out bbInterruptEnabled, FieldMode.Read | FieldMode.Set, name: "BB")
                .WithReservedBits(15, 3)
                .WithFlag(18, out suspendedInterruptEnabled, FieldMode.Read | FieldMode.Set, name: "SUSPENDED")
                .WithReservedBits(19, 13)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.ClearEnableInterrupts.Define(this)
                .WithReservedBits(0, 1)
                .WithFlag(1, name: "STOPPED",
                    writeCallback: (_, val) => { if(val) stoppedInterruptEnabled.Value = false; },
                    valueProviderCallback: _ => stoppedInterruptEnabled.Value)
                .WithFlag(2, name: "RXREADY",
                    writeCallback: (_, val) => { if(val) rxInterruptEnabled.Value = false; },
                    valueProviderCallback: _ => rxInterruptEnabled.Value)
                .WithReservedBits(3, 4)
                .WithFlag(7, name: "TXDSENT",
                    writeCallback: (_, val) => { if(val) txInterruptEnabled.Value = false; },
                    valueProviderCallback: _ => txInterruptEnabled.Value)
                .WithReservedBits(8, 1)
                .WithFlag(9, name: "ERROR",
                    writeCallback: (_, val) => { if(val) errorInterruptEnabled.Value = false; },
                    valueProviderCallback: _ => errorInterruptEnabled.Value)
                .WithReservedBits(10, 4)
                .WithFlag(14, name: "BB",
                    writeCallback: (_, val) => { if(val) bbInterruptEnabled.Value = false; },
                    valueProviderCallback: _ => bbInterruptEnabled.Value)
                .WithReservedBits(15, 3)
                .WithFlag(18, name: "SUSPENDED",
                    writeCallback: (_, val) => { if(val) suspendedInterruptEnabled.Value = false; },
                    valueProviderCallback: _ => suspendedInterruptEnabled.Value)
                .WithReservedBits(19, 13)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.Enable.Define(this)
                .WithValueField(0, 4, writeCallback: (_, val) =>
                {
                    switch(val)
                    {
                    case 0:
                        enabled = false;
                        twimMode = false;
                        break;

                    case 5: // TWI (legacy byte-by-byte)
                        enabled = true;
                        twimMode = false;
                        break;

                    case 6: // TWIM (EasyDMA)
                        enabled = true;
                        twimMode = true;
                        break;

                    default:
                        this.Log(LogLevel.Warning, "Wrong enabled value");
                        break;
                    }
                })
                .WithReservedBits(4, 28)
            ;

            Registers.ReceiveBuffer.Define(this)
                .WithValueField(0, 8, FieldMode.Read, valueProviderCallback: _ =>
                {
                    if(!slaveToMasterBuffer.TryDequeue(out var result))
                    {
                        this.Log(LogLevel.Warning, "Trying to read from an empty fifo");
                        result = 0;
                    }
                    else
                    {
                        this.Log(LogLevel.Noisy, "TWI legacy RXD read: 0x{0:X2}, {1} bytes remain", result, slaveToMasterBuffer.Count);
                    }

                    if(!twimMode && transmissionInProgress)
                    {
                        if(byteBoundaryStopShortcut.Value)
                        {
                            StopTransmission();
                        }
                        else if(!legacySuspended)
                        {
                            LegacyDeliverNextRxByte();
                        }
                    }

                    return result;
                })
                .WithReservedBits(8, 24)
            ;

            Registers.TransferBuffer.Define(this)
                .WithValueField(0, 8, writeCallback: (_, val) =>
                {
                    if(selectedSlave == null)
                    {
                        this.Log(LogLevel.Warning, "No slave is currently attached at selected address 0x{0:X}", address.Value);
                        addressNackError.Value = true;
                        errorInterruptPending.Value = true;
                        UpdateInterrupts();
                        return;
                    }

                    this.Log(LogLevel.Noisy, "Enqueuing byte 0x{0:X}", val);
                    masterToSlaveBuffer.Enqueue((byte)val);

                    txInterruptPending.Value = true;
                    UpdateInterrupts();
                })
                .WithReservedBits(8, 24)
            ;

            Registers.PinSelectSCL.Define(this)
                .WithValueField(0, 32, name: "PSEL.SCL")
            ;

            Registers.PinSelectSDA.Define(this)
                .WithValueField(0, 32, name: "PSEL.SDA")
            ;

            Registers.Frequency.Define(this)
                .WithValueField(0, 32, name: "FREQUENCY")
            ;

            Registers.RxdPtr.Define(this)
                .WithValueField(0, 32, out rxdPtr, name: "RXD.PTR")
            ;

            Registers.RxdMaxCnt.Define(this)
                .WithValueField(0, 16, out rxdMaxCnt, name: "RXD.MAXCNT")
                .WithReservedBits(16, 16)
            ;

            Registers.RxdAmount.Define(this)
                .WithValueField(0, 16, FieldMode.Read, name: "RXD.AMOUNT",
                    valueProviderCallback: _ => (uint)rxAmount)
                .WithReservedBits(16, 16)
            ;

            Registers.RxdList.Define(this)
                .WithValueField(0, 3, name: "RXD.LIST")
                .WithReservedBits(3, 29)
            ;

            Registers.TxdPtr.Define(this)
                .WithValueField(0, 32, out txdPtr, name: "TXD.PTR")
            ;

            Registers.TxdMaxCnt.Define(this)
                .WithValueField(0, 16, out txdMaxCnt, name: "TXD.MAXCNT")
                .WithReservedBits(16, 16)
            ;

            Registers.TxdAmount.Define(this)
                .WithValueField(0, 16, FieldMode.Read, name: "TXD.AMOUNT",
                    valueProviderCallback: _ => (uint)txAmount)
                .WithReservedBits(16, 16)
            ;

            Registers.TxdList.Define(this)
                .WithValueField(0, 3, name: "TXD.LIST")
                .WithReservedBits(3, 29)
            ;

            Registers.Address.Define(this)
                .WithValueField(0, 7, out address, writeCallback: (_, val) =>
                {
                    if(!TryGetByAddress((int)val, out selectedSlave))
                    {
                        this.Log(LogLevel.Warning, "Tried to select a not-connected slave at address 0x{0:X}", val);
                    }
                })
                .WithReservedBits(8, 24)
            ;
        }

        private bool TryFillReceivedBuffer(bool generateInterrupt)
        {
            if(selectedSlave == null)
            {
                return false;
            }

            if(!slaveToMasterBuffer.Any())
            {
                var data = selectedSlave.Read();
                slaveToMasterBuffer.EnqueueRange(data);
            }

            if(slaveToMasterBuffer.Any())
            {
                if(generateInterrupt)
                {
                    rxInterruptPending.Value = true;
                    UpdateInterrupts();
                }
                return true;
            }

            return false;
        }

        private bool TryReadFromSlave(out byte b)
        {
            if(!enabled)
            {
                this.Log(LogLevel.Warning, "Tried to read data on a disabled controller");
                b = 0;
                return false;
            }

            if(!slaveToMasterBuffer.TryDequeue(out b))
            {
                TryFillReceivedBuffer(false);
                if(!slaveToMasterBuffer.TryDequeue(out b))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TrySendDataToSlave()
        {
            if(!enabled)
            {
                this.Log(LogLevel.Warning, "Tried to send data on a disabled controller");
                return false;
            }

            if(!masterToSlaveBuffer.Any())
            {
                return false;
            }

            if(selectedSlave == null)
            {
                this.Log(LogLevel.Warning, "No slave is currently attached at selected address 0x{0:X}", address.Value);
                return false;
            }

            var data = masterToSlaveBuffer.DequeueAll();
            this.Log(LogLevel.Noisy, "Sending {0} bytes to the device {1}", data.Length, address.Value);
            selectedSlave.Write(data);

            return true;
        }

        private void StopTransmission()
        {
            transmissionInProgress = false;
            legacySuspended = false;

            TrySendDataToSlave();

            selectedSlave?.FinishTransmission();

            stoppedInterruptPending.Value = true;
            UpdateInterrupts();
        }

        private void LegacyPrefetchFromSlave()
        {
            if(selectedSlave == null)
            {
                return;
            }

            if(!slaveToMasterBuffer.Any())
            {
                var data = selectedSlave.Read();
                slaveToMasterBuffer.EnqueueRange(data);
                this.Log(LogLevel.Noisy, "TWI legacy: prefetched {0} bytes from slave 0x{1:X}", data.Length, address.Value);
            }
        }

        private void LegacyDeliverNextRxByte()
        {
            if(!transmissionInProgress || legacySuspended)
            {
                return;
            }

            if(!slaveToMasterBuffer.Any())
            {
                LegacyPrefetchFromSlave();
            }

            if(slaveToMasterBuffer.Any())
            {
                rxInterruptPending.Value = true;
                EventTriggered?.Invoke((uint)Registers.RxInterruptPending);

                bbEventPending.Value = true;
                EventTriggered?.Invoke((uint)Registers.ByteBoundaryEventPending);

                if(byteBoundarySuspendShortcut.Value)
                {
                    legacySuspended = true;
                    suspendedEventPending.Value = true;
                    EventTriggered?.Invoke((uint)Registers.SuspendedEventPending);
                }

                UpdateInterrupts();
            }
            else
            {
                this.Log(LogLevel.Warning, "TWI legacy RX: no data available from slave 0x{0:X}", address.Value);
            }
        }

        private void PerformTwimTransmit()
        {
            var count = (int)txdMaxCnt.Value;
            var ptr = (ulong)txdPtr.Value;

            if(selectedSlave == null)
            {
                this.Log(LogLevel.Warning, "TWIM TX: no slave at address 0x{0:X}", address.Value);
                addressNackError.Value = true;
                errorInterruptPending.Value = true;
                EventTriggered?.Invoke((uint)Registers.ErrorInterruptPending);
                UpdateInterrupts();
                return;
            }

            if(count > 0 && ptr >= 0x20000000)
            {
                var data = machine.SystemBus.ReadBytes(ptr, count);
                this.Log(LogLevel.Noisy, "TWIM TX: sending {0} bytes from 0x{1:X} to slave 0x{2:X}", count, ptr, address.Value);
                selectedSlave.Write(data);
                txAmount = count;
            }
            else
            {
                txAmount = 0;
            }

            txInterruptPending.Value = true;
            EventTriggered?.Invoke((uint)Registers.TxInterruptPending);

            lastTxEventPending.Value = true;
            EventTriggered?.Invoke((uint)Registers.LastTxEventPending);

            if(lastTxStopShortcut != null && lastTxStopShortcut.Value)
            {
                StopTransmission();
            }
            else
            {
                stoppedInterruptPending.Value = true;
                EventTriggered?.Invoke((uint)Registers.StoppedInterruptPending);
            }
            UpdateInterrupts();
        }

        private void PerformTwimReceive()
        {
            var count = (int)rxdMaxCnt.Value;
            var ptr = (ulong)rxdPtr.Value;

            if(selectedSlave == null)
            {
                this.Log(LogLevel.Warning, "TWIM RX: no slave at address 0x{0:X}", address.Value);
                addressNackError.Value = true;
                errorInterruptPending.Value = true;
                EventTriggered?.Invoke((uint)Registers.ErrorInterruptPending);
                UpdateInterrupts();
                return;
            }

            var data = selectedSlave.Read(count);
            if(data.Length > 0 && ptr >= 0x20000000)
            {
                var toWrite = Math.Min(data.Length, count);
                this.Log(LogLevel.Noisy, "TWIM RX: received {0} bytes to 0x{1:X} from slave 0x{2:X}", toWrite, ptr, address.Value);
                machine.SystemBus.WriteBytes(data, ptr, 0, toWrite);
                rxAmount = toWrite;
            }
            else
            {
                rxAmount = 0;
            }

            rxInterruptPending.Value = true;
            EventTriggered?.Invoke((uint)Registers.RxInterruptPending);

            lastRxEventPending.Value = true;
            EventTriggered?.Invoke((uint)Registers.LastRxEventPending);

            if(lastRxStopShortcut != null && lastRxStopShortcut.Value)
            {
                StopTransmission();
            }
            else
            {
                stoppedInterruptPending.Value = true;
                EventTriggered?.Invoke((uint)Registers.StoppedInterruptPending);
            }
            UpdateInterrupts();
        }

        private void UpdateInterrupts()
        {
            var flag = false;

            flag |= txInterruptEnabled.Value && txInterruptPending.Value;
            flag |= rxInterruptEnabled.Value && rxInterruptPending.Value;
            flag |= stoppedInterruptEnabled.Value && stoppedInterruptPending.Value;
            flag |= errorInterruptEnabled.Value && errorInterruptPending.Value;
            flag |= bbInterruptEnabled.Value && bbEventPending.Value;
            flag |= suspendedInterruptEnabled.Value && suspendedEventPending.Value;

            this.Log(LogLevel.Noisy, "Setting IRQ to {0}", flag);
            IRQ.Set(flag);
        }

        private II2CPeripheral selectedSlave;
        private bool enabled;
        private bool twimMode;
        private bool transmissionInProgress;
        private bool legacySuspended;
        private int rxAmount;
        private int txAmount;

        private IValueRegisterField address;
        private IFlagRegisterField txInterruptPending;
        private IFlagRegisterField txInterruptEnabled;

        private IFlagRegisterField rxInterruptPending;
        private IFlagRegisterField rxInterruptEnabled;

        private IFlagRegisterField errorInterruptPending;
        private IFlagRegisterField errorInterruptEnabled;

        private IFlagRegisterField stoppedInterruptPending;
        private IFlagRegisterField stoppedInterruptEnabled;

        private IFlagRegisterField byteBoundarySuspendShortcut;
        private IFlagRegisterField byteBoundaryStopShortcut;
        private IFlagRegisterField lastTxStopShortcut;
        private IFlagRegisterField lastRxStopShortcut;

        private IFlagRegisterField lastRxEventPending;
        private IFlagRegisterField lastTxEventPending;
        private IFlagRegisterField bbEventPending;
        private IFlagRegisterField suspendedEventPending;

        private IFlagRegisterField bbInterruptEnabled;
        private IFlagRegisterField suspendedInterruptEnabled;

        private IFlagRegisterField addressNackError;

        private IValueRegisterField rxdPtr;
        private IValueRegisterField rxdMaxCnt;
        private IValueRegisterField txdPtr;
        private IValueRegisterField txdMaxCnt;

        private new readonly IMachine machine;
        private readonly Queue<byte> slaveToMasterBuffer;
        private readonly Queue<byte> masterToSlaveBuffer;

        private enum Registers
        {
            StartReceiving = 0x000,
            StartTransmitting = 0x008,
            StopTransmitting = 0x014,
            SuspendTransmitting = 0x01C,
            ResumeReceiving = 0x020,
            StoppedInterruptPending = 0x104,
            RxInterruptPending = 0x108,
            TxInterruptPending = 0x11C,
            ErrorInterruptPending = 0x124,
            ByteBoundaryEventPending = 0x138,
            LastRxEventPending = 0x148,
            LastTxEventPending = 0x15C,
            SuspendedEventPending = 0x160,
            Shortcuts = 0x200,
            SetEnableInterrupts = 0x304,
            ClearEnableInterrupts = 0x308,
            ErrorSource = 0x4C4,
            Enable = 0x500,
            PinSelectSCL = 0x508,
            PinSelectSDA = 0x50C,
            ReceiveBuffer = 0x518,
            TransferBuffer = 0x51C,
            Frequency = 0x524,
            RxdPtr = 0x534,
            RxdMaxCnt = 0x538,
            RxdAmount = 0x53C,
            RxdList = 0x540,
            TxdPtr = 0x544,
            TxdMaxCnt = 0x548,
            TxdAmount = 0x54C,
            TxdList = 0x550,
            Address = 0x588
        }
    }
}