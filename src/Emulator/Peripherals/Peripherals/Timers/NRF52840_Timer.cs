//
// Copyright (c) 2010-2025 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Miscellaneous;

namespace Antmicro.Renode.Peripherals.Timers
{
    public class NRF52840_Timer : BasicDoubleWordPeripheral, IKnownSize, INRFEventProvider
    {
        public NRF52840_Timer(IMachine machine, int numberOfEvents) : base(machine)
        {
            IRQ = new GPIO();

            if(numberOfEvents > MaxNumberOfEvents)
            {
                throw new ConstructionException($"Cannot create {nameof(NRF52840_Timer)} with {numberOfEvents} events (must be less than {MaxNumberOfEvents})");
            }
            this.numberOfEvents = numberOfEvents;

            this.eventCompareEnabled = new IFlagRegisterField[numberOfEvents];
            this.shortClearEnabled = new bool[numberOfEvents];
            this.shortStopEnabled = new bool[numberOfEvents];
            innerTimers = new ComparingTimer[numberOfEvents];
            for(var i = 0u; i < innerTimers.Length; i++)
            {
                var j = i;
                innerTimers[j] = new ComparingTimer(machine.ClockSource, InitialFrequency, this, $"compare{j}", eventEnabled: true);
                innerTimers[j].CompareReached += () =>
                {
                    this.Log(LogLevel.Noisy, "Compare Reached on CC{0} is {1}", j, innerTimers[j].Compare);
                    eventCompareEnabled[j].Value = true;
                    EventTriggered?.Invoke((uint)Register.Compare0EventPending + 0x4u * j);

                    if(shortClearEnabled[j])
                    {
                        foreach(var timer in innerTimers)
                        {
                            timer.Value = 0;
                        }
                    }
                    if(shortStopEnabled[j])
                    {
                        timerRunning = false;
                        foreach(var timer in innerTimers)
                        {
                            timer.Enabled = false;
                        }
                    }

                    UpdateInterrupts();
                };
            }

            DefineRegisters();
        }

        public override void Reset()
        {
            base.Reset();

            foreach(var timer in innerTimers)
            {
                timer.Reset();
            }

            previousIrqState = false;
            IRQ.Unset();
        }

        public GPIO IRQ { get; }

        public long Size => 0x1000;

        public event Action<uint> EventTriggered;

        private void DefineRegisters()
        {
            Register.Start.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASKS_START", writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        timerRunning = true;
                        if(mode.Value == Mode.Timer)
                        {
                            foreach(var timer in innerTimers)
                            {
                                timer.Enabled = true;
                            }
                        }
                    }
                })
                .WithReservedBits(1, 31)
            ;

            Register.Stop.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASKS_STOP", writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        timerRunning = false;
                        foreach(var timer in innerTimers)
                        {
                            timer.Enabled = false;
                        }
                    }
                })
                .WithReservedBits(1, 31)
            ;

            Register.Count.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASK_COUNT", writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        if(!timerRunning)
                        {
                            this.Log(LogLevel.Warning, "Triggered TASK_COUNT before issuing TASK_START, ignoring...");
                            return;
                        }
                        if(mode.Value == Mode.Timer)
                        {
                            this.Log(LogLevel.Warning, "Triggered TASK_COUNT in TIMER mode, ignoring...");
                            return;
                        }
                        var i = 0;
                        foreach(var timer in innerTimers)
                        {
                            timer.Value++;
                            if(timer.Compare == timer.Value)
                            {
                                eventCompareEnabled[i].Value = true;
                                UpdateInterrupts();
                            }
                            i++;
                        }
                    }
                })
                .WithReservedBits(1, 31)
            ;

            Register.Clear.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASK_CLEAR", writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        foreach(var timer in innerTimers)
                        {
                            timer.Value = 0;
                        }
                    }
                })
                .WithReservedBits(1, 31)
            ;

            Register.Capture0.DefineMany(this, (uint)numberOfEvents, setup: (register, idx) =>
            {
                register
                    .WithFlag(0, FieldMode.Write, name: "TASKS_CAPTURE", writeCallback: (_, __) =>
                    {
                        innerTimers[idx].Compare = innerTimers[idx].Value & GetBitModeMask();
                    })
                    .WithReservedBits(1, 31);
            });

            Register.Compare0EventPending.DefineMany(this, (uint)numberOfEvents, setup: (register, idx) =>
            {
                register
                    .WithFlag(0, out eventCompareEnabled[idx], name: $"EVENTS_COMPARE[{idx}]", writeCallback: (_, __) =>
                    {
                        UpdateInterrupts();
                    })
                    .WithReservedBits(1, 31);
            });

            Register.Shortcuts.Define(this)
                .WithFlags(0, numberOfEvents, name: "COMPARE_CLEAR",
                    writeCallback: (i, _, value) => { shortClearEnabled[i] = value; },
                    valueProviderCallback: (i, _) => shortClearEnabled[i])
                .WithReservedBits(numberOfEvents, 8 - numberOfEvents)
                .WithFlags(8, numberOfEvents, name: "COMPARE_STOP",
                    writeCallback: (i, _, value) => { shortStopEnabled[i] = value; },
                    valueProviderCallback: (i, _) => shortStopEnabled[i])
                .WithReservedBits(8 + numberOfEvents, 32 - 8 - numberOfEvents)
            ;

            Register.InterruptEnableSet.Define(this)
                .WithReservedBits(0, 16)
                .WithFlags(16, numberOfEvents, out eventCompareInterruptEnabled, FieldMode.Set | FieldMode.Read, name: "COMPARE")
                .WithReservedBits(22 - MaxNumberOfEvents + numberOfEvents, 10 + MaxNumberOfEvents - numberOfEvents)
                .WithChangeCallback((_, __) =>
                {
                    UpdateInterrupts();
                })
            ;

            Register.InterruptEnableClear.Define(this)
                .WithReservedBits(0, 16)
                .WithFlags(16, numberOfEvents, name: "COMPARE", writeCallback: (i, _, value) => { if(value) eventCompareInterruptEnabled[i].Value = false; })
                .WithReservedBits(22 - MaxNumberOfEvents + numberOfEvents, 10 + MaxNumberOfEvents - numberOfEvents)
                .WithChangeCallback((_, __) =>
                {
                    UpdateInterrupts();
                })
            ;

            Register.Mode.Define(this)
                .WithEnumField(0, 2, out mode, name: "MODE", changeCallback: (_, value) =>
                {
                    if(value != Mode.Timer && innerTimers[0].Enabled)
                    {
                        this.Log(LogLevel.Error, "Switching timer to COUNTER mode while the timer is running");
                    }
                })
            ;

            Register.BitMode.Define(this)
                .WithEnumField(0, 2, out bitMode, name: "BITMODE")
                .WithReservedBits(2, 30)
            ;

            Register.Prescaler.Define(this)
                .WithValueField(0, 4, out prescaler, name: "PRESCALER", writeCallback: (_, value) =>
                {
                    foreach(var timer in innerTimers)
                    {
                        timer.Divider = (uint)(1 << (int)value);
                    }
                })
                .WithReservedBits(12, 20)
            ;

            Register.Compare0.DefineMany(this, (uint)numberOfEvents, setup: (register, idx) =>
            {
                register
                    .WithValueField(0, 32, name: "CAPTURE_COMPARE", writeCallback: (_, value) =>
                    {
                        SetCompare(idx, value);
                    },
                    valueProviderCallback: _ =>
                    {
                        return (uint)(innerTimers[idx].Compare & GetBitModeMask());
                    });
            });
        }

        private ulong GetBitModeMask()
        {
            switch(bitMode.Value)
            {
                case BitModeValue.Bit8: return 0xFF;
                case BitModeValue.Bit16: return 0xFFFF;
                case BitModeValue.Bit24: return 0xFFFFFF;
                default: return 0xFFFFFFFF;
            }
        }

        private void SetCompare(int idx, ulong value)
        {
            eventCompareEnabled[idx].Value = false;
            UpdateInterrupts();
            innerTimers[idx].Compare = value & GetBitModeMask();
        }

        private void UpdateInterrupts()
        {
            var flag = false;

            for(var i = 0; i < numberOfEvents; i++)
            {
                flag |= eventCompareInterruptEnabled[i].Value && eventCompareEnabled[i].Value;
            }

            if(flag && !previousIrqState)
            {
                IRQ.Blink();
            }
            else if(!flag && previousIrqState)
            {
                IRQ.Unset();
            }
            previousIrqState = flag;
        }

        private IFlagRegisterField[] eventCompareInterruptEnabled;
        private IValueRegisterField prescaler;
        private IEnumRegisterField<Mode> mode;
        private IEnumRegisterField<BitModeValue> bitMode;
        private bool previousIrqState;
        private bool timerRunning;

        private readonly IFlagRegisterField[] eventCompareEnabled;
        private readonly bool[] shortClearEnabled;
        private readonly bool[] shortStopEnabled;

        private readonly ComparingTimer[] innerTimers;

        private readonly int numberOfEvents;
        private const int InitialFrequency = 16000000;
        private const int MaxNumberOfEvents = 6;

        private enum Mode
        {
            Timer,
            Counter,
            LowPowerCounter
        }

        private enum BitModeValue
        {
            Bit16 = 0,
            Bit8 = 1,
            Bit24 = 2,
            Bit32 = 3
        }

        private enum Register : long
        {
            Start = 0x000,
            Stop = 0x004,
            Count = 0x008,
            Clear = 0x00C,
            Shutdown = 0x010,

            Capture0 = 0x040,
            Capture1 = 0x044,
            Capture2 = 0x048,
            Capture3 = 0x04C,
            Capture4 = 0x050,
            Capture5 = 0x054,

            Compare0EventPending = 0x140,
            Compare1EventPending = 0x144,
            Compare2EventPending = 0x148,
            Compare3EventPending = 0x14C,
            Compare4EventPending = 0x150,
            Compare5EventPending = 0x154,

            Shortcuts = 0x200,

            InterruptEnableSet = 0x304,
            InterruptEnableClear = 0x308,

            Mode = 0x504,
            BitMode = 0x508,
            Prescaler = 0x510,

            Compare0 = 0x540,
            Compare1 = 0x544,
            Compare2 = 0x548,
            Compare3 = 0x54C,
            Compare4 = 0x550,
            Compare5 = 0x554
        }
    }
}