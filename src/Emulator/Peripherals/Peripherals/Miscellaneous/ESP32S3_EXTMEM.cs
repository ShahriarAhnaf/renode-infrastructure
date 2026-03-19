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
    public class ESP32S3_EXTMEM : BasicDoubleWordPeripheral, IKnownSize
    {
        public ESP32S3_EXTMEM(IMachine machine) : base(machine)
        {
            DefineRegisters();
        }

        public long Size => 0x1000;

        private void DefineRegisters()
        {
            Registers.DcacheCtrl.Define(this, 0x00000001)
                .WithTaggedFlag("EXTMEM_DCACHE_ENABLE", 0)
                .WithReservedBits(1, 31)
            ;

            Registers.DcacheCtrl1.Define(this)
                .WithTaggedFlag("EXTMEM_DCACHE_SHUT_CORE0_BUS", 0)
                .WithTaggedFlag("EXTMEM_DCACHE_SHUT_CORE1_BUS", 1)
                .WithReservedBits(2, 30)
            ;

            Registers.DcacheTagPower.Define(this, 0x00000001)
                .WithTaggedFlag("EXTMEM_DCACHE_TAG_MEM_FORCE_ON", 0)
                .WithTaggedFlag("EXTMEM_DCACHE_TAG_MEM_FORCE_PD", 1)
                .WithTaggedFlag("EXTMEM_DCACHE_TAG_MEM_FORCE_PU", 2)
                .WithReservedBits(3, 29)
            ;

            Registers.IcacheCtrl.Define(this, 0x00000001)
                .WithTaggedFlag("EXTMEM_ICACHE_ENABLE", 0)
                .WithReservedBits(1, 31)
            ;

            Registers.IcacheCtrl1.Define(this)
                .WithTaggedFlag("EXTMEM_ICACHE_SHUT_CORE0_BUS", 0)
                .WithTaggedFlag("EXTMEM_ICACHE_SHUT_CORE1_BUS", 1)
                .WithReservedBits(2, 30)
            ;

            Registers.IcacheTagPower.Define(this, 0x00000001)
                .WithTaggedFlag("EXTMEM_ICACHE_TAG_MEM_FORCE_ON", 0)
                .WithTaggedFlag("EXTMEM_ICACHE_TAG_MEM_FORCE_PD", 1)
                .WithTaggedFlag("EXTMEM_ICACHE_TAG_MEM_FORCE_PU", 2)
                .WithReservedBits(3, 29)
            ;

            // Cache sync/invalidate done flags: report done immediately
            Registers.DcacheSyncCtrl.Define(this, 0x00000001)
                .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => true, name: "EXTMEM_DCACHE_SYNC_DONE")
                .WithTaggedFlag("EXTMEM_DCACHE_INVALIDATE_ENA", 1)
                .WithTaggedFlag("EXTMEM_DCACHE_WRITEBACK_ENA", 2)
                .WithTaggedFlag("EXTMEM_DCACHE_CLEAN_ENA", 3)
                .WithReservedBits(4, 28)
            ;

            Registers.IcacheSyncCtrl.Define(this, 0x00000001)
                .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => true, name: "EXTMEM_ICACHE_SYNC_DONE")
                .WithTaggedFlag("EXTMEM_ICACHE_INVALIDATE_ENA", 1)
                .WithReservedBits(2, 30)
            ;

            Registers.DcacheAutoloadCtrl.Define(this, 0x00000008)
                .WithValueField(0, 32, name: "EXTMEM_DCACHE_AUTOLOAD_CTRL")
            ;

            Registers.IcacheAutoloadCtrl.Define(this, 0x00000008)
                .WithValueField(0, 32, name: "EXTMEM_ICACHE_AUTOLOAD_CTRL")
            ;

            Registers.CacheState.Define(this, 0x00000001)
                .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => true, name: "EXTMEM_ICACHE_STATE_IDLE")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => true, name: "EXTMEM_DCACHE_STATE_IDLE")
                .WithReservedBits(2, 30)
            ;

            Registers.CacheMmuOwner.Define(this)
                .WithValueField(0, 32, name: "EXTMEM_CACHE_MMU_OWNER")
            ;

            Registers.CacheConf.Define(this, 0x0000000C)
                .WithValueField(0, 32, name: "EXTMEM_CACHE_CONF_MISC")
            ;

            Registers.CacheEncryptDecrypt.Define(this)
                .WithValueField(0, 32, name: "EXTMEM_CACHE_ENCRYPT_DECRYPT")
            ;

            Registers.CachePreloadIntCtrl.Define(this, 0x00000003)
                .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => true, name: "EXTMEM_ICACHE_PRELOAD_DONE")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => true, name: "EXTMEM_DCACHE_PRELOAD_DONE")
                .WithReservedBits(2, 30)
            ;

            Registers.DateRegister.Define(this, 0x02101160)
                .WithValueField(0, 28, name: "EXTMEM_DATE")
                .WithReservedBits(28, 4)
            ;
        }

        private enum Registers : long
        {
            IcacheCtrl            = 0x00,
            IcacheCtrl1           = 0x04,
            IcacheTagPower        = 0x08,
            IcacheSyncCtrl        = 0x28,
            IcacheAutoloadCtrl    = 0x4C,
            DcacheCtrl            = 0x80,
            DcacheCtrl1           = 0x84,
            DcacheTagPower        = 0x88,
            DcacheSyncCtrl        = 0xA8,
            DcacheAutoloadCtrl    = 0xCC,
            CacheState            = 0x170,
            CacheMmuOwner         = 0x178,
            CacheConf             = 0x17C,
            CacheEncryptDecrypt   = 0x180,
            CachePreloadIntCtrl   = 0x184,
            DateRegister          = 0x3FC,
        }
    }
}
