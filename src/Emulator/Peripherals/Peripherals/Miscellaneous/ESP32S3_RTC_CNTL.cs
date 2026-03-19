//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class ESP32S3_RTC_CNTL : BasicDoubleWordPeripheral, IKnownSize
    {
        public ESP32S3_RTC_CNTL(IMachine machine) : base(machine)
        {
            DefineRegisters();
        }

        public long Size => 0x200;

        private void DefineRegisters()
        {
            Registers.Options0.Define(this, 0x1C492000)
                .WithValueField(0, 32, name: "RTC_CNTL_OPTIONS0")
            ;

            Registers.SleepTimer0.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_SLP_TIMER0")
            ;

            Registers.SleepTimer1.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_SLP_TIMER1")
            ;

            Registers.TimeUpdate.Define(this)
                .WithValueField(0, 27, name: "RTC_CNTL_TIMER_VALUE_UNUSED")
                .WithTaggedFlag("RTC_CNTL_TIMER_XTL_OFF", 27)
                .WithTaggedFlag("RTC_CNTL_TIMER_SYS_STALL", 28)
                .WithTaggedFlag("RTC_CNTL_TIMER_SYS_RST", 29)
                .WithFlag(30, FieldMode.Read, valueProviderCallback: _ => true, name: "RTC_CNTL_TIMER_VALUE_VALID")
                .WithFlag(31, name: "RTC_CNTL_TIME_UPDATE")
            ;

            Registers.TimeHigh0.Define(this)
                .WithValueField(0, 16, FieldMode.Read, name: "RTC_CNTL_TIMER_VALUE0_HIGH")
                .WithReservedBits(16, 16)
            ;

            Registers.TimeLow0.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "RTC_CNTL_TIMER_VALUE0_LOW")
            ;

            Registers.State0.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_STATE0")
            ;

            Registers.TimerControl.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_TIMER_CTRL")
            ;

            Registers.InterruptRaw.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "RTC_CNTL_INT_RAW")
            ;

            Registers.InterruptStatus.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "RTC_CNTL_INT_ST")
            ;

            Registers.InterruptEnable.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_INT_ENA")
            ;

            Registers.InterruptClear.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "RTC_CNTL_INT_CLR")
            ;

            Registers.Store0.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_STORE0")
            ;

            Registers.Store1.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_STORE1")
            ;

            Registers.Store2.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_STORE2")
            ;

            Registers.Store3.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_STORE3")
            ;

            // Reset state register: reports POWERON_RESET for both CPUs
            // Bits [5:0] = reset reason CPU0, Bits [11:6] = reset reason CPU1
            // POWERON_RESET = 0x01
            Registers.ResetState.Define(this, 0x00000041)
                .WithValueField(0, 6, name: "RTC_CNTL_RESET_CAUSE_PROCPU")
                .WithValueField(6, 6, name: "RTC_CNTL_RESET_CAUSE_APPCPU")
                .WithTaggedFlag("RTC_CNTL_STAT_VECTOR_SEL_PROCPU", 12)
                .WithTaggedFlag("RTC_CNTL_STAT_VECTOR_SEL_APPCPU", 13)
                .WithValueField(14, 18, name: "RTC_CNTL_RESET_STATE_RESERVED")
            ;

            Registers.WdtConfig0.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_WDT_CONFIG0")
            ;

            Registers.WdtConfig1.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_WDT_CONFIG1")
            ;

            Registers.WdtFeed.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "RTC_CNTL_WDT_FEED")
            ;

            Registers.WdtWriteProtect.Define(this, 0x50D83AA1)
                .WithValueField(0, 32, name: "RTC_CNTL_WDT_WPROTECT")
            ;

            Registers.SwdConf.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_SWD_CONF")
            ;

            Registers.SwdWriteProtect.Define(this, 0x8F1D312A)
                .WithValueField(0, 32, name: "RTC_CNTL_SWD_WPROTECT")
            ;

            Registers.ClkConf.Define(this, 0x11583218)
                .WithValueField(0, 32, name: "RTC_CNTL_CLK_CONF")
            ;

            Registers.SlowClkConf.Define(this, 0x00400000)
                .WithValueField(0, 32, name: "RTC_CNTL_SLOW_CLK_CONF")
            ;

            Registers.Store4.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_STORE4")
            ;

            Registers.Store5.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_STORE5")
            ;

            Registers.Store6.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_STORE6")
            ;

            Registers.Store7.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_STORE7")
            ;

            Registers.DigPadHoldForce.Define(this)
                .WithValueField(0, 32, name: "RTC_CNTL_DIG_PAD_HOLD_FORCE")
            ;

            Registers.DateRegister.Define(this, 0x02101181)
                .WithValueField(0, 28, name: "RTC_CNTL_DATE")
                .WithReservedBits(28, 4)
            ;
        }

        private enum Registers : long
        {
            Options0         = 0x00,
            SleepTimer0      = 0x04,
            SleepTimer1      = 0x08,
            TimeUpdate       = 0x0C,
            TimeHigh0        = 0x10,
            TimeLow0         = 0x14,
            State0           = 0x18,
            TimerControl     = 0x1C,
            InterruptRaw     = 0x20,
            InterruptStatus  = 0x24,
            InterruptEnable  = 0x28,
            InterruptClear   = 0x2C,
            Store0           = 0x30,
            Store1           = 0x34,
            ResetState       = 0x38,
            Store2           = 0x3C,
            Store3           = 0x40,
            WdtConfig0       = 0x90,
            WdtConfig1       = 0x94,
            WdtFeed          = 0xA4,
            WdtWriteProtect  = 0xA8,
            SwdConf          = 0xAC,
            SwdWriteProtect  = 0xB0,
            ClkConf          = 0x70,
            SlowClkConf      = 0x74,
            Store4           = 0xBC,
            Store5           = 0xC0,
            Store6           = 0xC4,
            Store7           = 0xC8,
            DigPadHoldForce  = 0x1C4,
            DateRegister     = 0x1FC,
        }
    }
}
