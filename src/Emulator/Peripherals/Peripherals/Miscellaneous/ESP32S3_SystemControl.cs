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
    public class ESP32S3_SystemControl : BasicDoubleWordPeripheral, IKnownSize
    {
        public ESP32S3_SystemControl(IMachine machine) : base(machine)
        {
            DefineRegisters();
        }

        public long Size => 0x1000;

        private void DefineRegisters()
        {
            Registers.CoreClock.Define(this, 0x00000001)
                .WithValueField(0, 32, name: "SYSTEM_CORE_CLK_CONF")
            ;

            Registers.CpuPerConf.Define(this, 0x0000000C)
                .WithValueField(0, 2, name: "SYSTEM_CPUPERIOD_SEL")
                .WithTaggedFlag("SYSTEM_PLL_FREQ_SEL", 2)
                .WithTaggedFlag("SYSTEM_CPU_WAIT_MODE_FORCE_ON", 3)
                .WithValueField(4, 28, name: "SYSTEM_CPU_PER_CONF_RESERVED")
            ;

            Registers.PeripClkEn0.Define(this, 0xF9C1E06F)
                .WithValueField(0, 32, name: "SYSTEM_PERIP_CLK_EN0")
            ;

            Registers.PeripClkEn1.Define(this, 0x00000800)
                .WithValueField(0, 32, name: "SYSTEM_PERIP_CLK_EN1")
            ;

            Registers.PeripRstEn0.Define(this)
                .WithValueField(0, 32, name: "SYSTEM_PERIP_RST_EN0")
            ;

            Registers.PeripRstEn1.Define(this)
                .WithValueField(0, 32, name: "SYSTEM_PERIP_RST_EN1")
            ;

            Registers.BtLpClkConf.Define(this)
                .WithValueField(0, 32, name: "SYSTEM_BT_LP_CLK_CONF")
            ;

            Registers.CpuIntrFromCpu0.Define(this)
                .WithValueField(0, 32, name: "SYSTEM_CPU_INTR_FROM_CPU_0")
            ;

            Registers.CpuIntrFromCpu1.Define(this)
                .WithValueField(0, 32, name: "SYSTEM_CPU_INTR_FROM_CPU_1")
            ;

            Registers.CpuIntrFromCpu2.Define(this)
                .WithValueField(0, 32, name: "SYSTEM_CPU_INTR_FROM_CPU_2")
            ;

            Registers.CpuIntrFromCpu3.Define(this)
                .WithValueField(0, 32, name: "SYSTEM_CPU_INTR_FROM_CPU_3")
            ;

            Registers.RsaClkPd.Define(this, 0x00000001)
                .WithValueField(0, 32, name: "SYSTEM_RSA_MEM_PD")
            ;

            // System clock config: reports PLL clock selected
            // Bits [1:0] = SOC_CLK_SEL: 0=XTAL, 1=PLL, 2=RC_FAST
            Registers.SysclkConf.Define(this, 0x00000001)
                .WithValueField(0, 2, name: "SYSTEM_SOC_CLK_SEL")
                .WithValueField(2, 4, name: "SYSTEM_CLK_XTAL_FREQ")
                .WithTaggedFlag("SYSTEM_CLK_DIV_EN", 6)
                .WithValueField(7, 25, name: "SYSTEM_SYSCLK_CONF_RESERVED")
            ;

            Registers.MemPvt.Define(this)
                .WithValueField(0, 32, name: "SYSTEM_MEM_PVT")
            ;

            Registers.ClockGate.Define(this, 0x00000001)
                .WithTaggedFlag("SYSTEM_CLK_EN", 0)
                .WithReservedBits(1, 31)
            ;

            Registers.DateRegister.Define(this, 0x02101180)
                .WithValueField(0, 28, name: "SYSTEM_DATE")
                .WithReservedBits(28, 4)
            ;
        }

        private enum Registers : long
        {
            CoreClock        = 0x00,
            CpuPerConf       = 0x08,
            PeripClkEn0      = 0x18,
            PeripClkEn1      = 0x1C,
            PeripRstEn0      = 0x20,
            PeripRstEn1      = 0x24,
            BtLpClkConf      = 0x28,
            CpuIntrFromCpu0  = 0x2C,
            CpuIntrFromCpu1  = 0x30,
            CpuIntrFromCpu2  = 0x34,
            CpuIntrFromCpu3  = 0x38,
            RsaClkPd         = 0x3C,
            SysclkConf       = 0x58,
            MemPvt           = 0x5C,
            ClockGate        = 0x60,
            DateRegister     = 0xFFC,
        }
    }
}
