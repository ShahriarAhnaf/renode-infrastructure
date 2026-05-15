//
// Copyright (c) 2010-2025 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class NRF52840_RNG : BasicDoubleWordPeripheral, IKnownSize, INRFEventProvider
    {
        public NRF52840_RNG(IMachine machine) : base(machine)
        {
            rng = EmulationManager.Instance.CurrentEmulation.RandomGenerator;
            IRQ = new GPIO();
            DefineRegisters();
        }

        public override void Reset()
        {
            base.Reset();
            started = false;
            previousIrqState = false;
            IRQ.Unset();
        }

        public long Size => 0x1000;

        public GPIO IRQ { get; }

        public event Action<uint> EventTriggered;

        private void DefineRegisters()
        {
            Registers.Start.Define(this)
                .WithFlag(0, FieldMode.Write, writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        started = true;
                        eventValrdy.Value = true;
                    }
                }, name: "TASKS_START")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => Update())
            ;

            Registers.Stop.Define(this)
                .WithFlag(0, FieldMode.Write, writeCallback: (_, value) => { if(value) started = false; }, name: "TASKS_STOP")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => Update())
            ;

            Registers.ValueReady.Define(this)
                .WithFlag(0, out eventValrdy, writeCallback: (_, value) =>
                {
                    if(!value)
                    {
                        // Firmware cleared the event after consuming a byte.
                        // Real hardware re-fires VALRDY automatically on the
                        // next byte; this model used to fire only once per
                        // TASKS_START, starving the Zephyr entropy pool. Re-arm
                        // immediately whenever still started.
                        if(started)
                        {
                            eventValrdy.Value = true;
                        }
                        Update();
                    }
                }, name: "EVENTS_VALRDY")
                .WithReservedBits(1, 31)
            ;

            Registers.Shorts.Define(this, name: "SHORTS")
                .WithFlag(0, out readyToStopShortEnabled, name: "VALRDY_STOP")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => Update())
            ;

            Registers.InterruptEnableSet.Define(this, name: "INTENSET")
                .WithFlag(0, out interruptEnabled, FieldMode.Set | FieldMode.Read, name: "VALRDY")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => Update())
            ;

            Registers.InterruptEnableClear.Define(this, name: "INTENCLR")
                .WithFlag(0, writeCallback: (_, value) => interruptEnabled.Value &= !value, valueProviderCallback: _ => interruptEnabled.Value, name: "VALRDY")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => Update())
            ;

            Registers.Config.Define(this, name: "CONFIG")
                .WithTaggedFlag("DERCEN", 0)
                .WithReservedBits(1, 31)
            ;

            Registers.Value.Define(this, name: "VALUE")
                .WithValueField(0, 8, FieldMode.Read, valueProviderCallback: _ => started ? (uint)rng.Next(0, byte.MaxValue) : 0u, name: "VALUE")
                .WithReservedBits(8, 24)
            ;
        }

        private void Update()
        {
            var irqCondition = eventValrdy.Value && interruptEnabled.Value;
            // Edge-trigger on rising; also re-pulse if we're still asserting
            // and previousIrqState is true — this matches the hardware
            // semantic where VALRDY auto-rearms after each consumed byte.
            if(irqCondition)
            {
                this.Log(LogLevel.Noisy, "Generated new interrupt for RNG");
                EventTriggered?.Invoke(0);
                IRQ.Blink();
            }
            else if(!irqCondition && previousIrqState)
            {
                IRQ.Unset();
            }
            previousIrqState = irqCondition;

            if(eventValrdy.Value && readyToStopShortEnabled.Value)
            {
                started = false;
            }
        }

        private bool previousIrqState;
        private bool started;
        private IFlagRegisterField eventValrdy;
        private IFlagRegisterField readyToStopShortEnabled;
        private IFlagRegisterField interruptEnabled;

        private readonly PseudorandomNumberGenerator rng;

        private enum Registers
        {
            Start = 0x0,
            Stop = 0x4,
            ValueReady = 0x100,
            Shorts = 0x200,
            InterruptEnableSet = 0x304,
            InterruptEnableClear = 0x308,
            Config = 0x504,
            Value = 0x508
        }
    }
}