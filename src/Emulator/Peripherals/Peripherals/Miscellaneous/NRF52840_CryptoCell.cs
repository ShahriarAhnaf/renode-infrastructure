//
// Copyright (c) 2010-2025 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities.Crypto;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // ARM CryptoCell-310 behavioral model for the nRF52840.
    //
    // Memory layout (relative to peripheral base 0x5002A000):
    //   0x000-0xFFF: nRF52840 CRYPTOCELL wrapper (ENABLE at +0x500)
    //   0x1000-0x1FFF: ARM CC310 core (RNG/AES/CHACHA/HASH/PKA/HOST_RGF/DIN/DOUT/SRAM/MISC/CTL)
    //
    // Register map from official Nordic CC310 documentation:
    //   https://docs.nordicsemi.com/bundle/ps_nrf52840/page/cryptocell.html
    //   CC310 core base in docs: 0x5002B000
    //   Peripheral base:         0x5002A000
    //   Therefore: all CC310 doc offsets get +0x1000 in the register enum.
    //
    // Implements:
    //   - ENABLE gate (R/W, bit 0) — SoftDevice/nrf_cc310 blob check before access
    //   - CC_RNG: TRNG with noise source, EHR data, DMA to internal SRAM
    //   - CC_AES: ECB/CBC/CTR modes via Renode's upstream AesProvider
    //   - CC_DIN/CC_DOUT: DMA engine (bypass and AES paths)
    //   - CC_HOST_RGF: interrupt request/mask/clear, fixed identification values
    //   - CC_RNG_SRAM: 4 KB internal SRAM with auto-increment address
    //   - CC_CTL: crypto control (selects bypass vs AES DMA)
    //   - CC_CHACHA / CC_HASH / CC_PKA / CC_MISC: stubbed register sets
    public class NRF52840_CryptoCell : BasicDoubleWordPeripheral, IKnownSize
    {
        public NRF52840_CryptoCell(IMachine machine) : base(machine)
        {
            IRQ = new GPIO();
            rng = EmulationManager.Instance.CurrentEmulation.RandomGenerator;

            cc310Sram = new byte[CC310SramSize];
            aesKey = new byte[AesBlockSize];
            aesIv = new byte[AesBlockSize];
            aesCtr = new byte[AesBlockSize];

            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            base.Reset();

            hostIrr = 0;
            hostImr = 0x01FFFFFF;
            rngIsr = 0;
            noiseOn = false;
            aesControl = 0;
            cryptoCtl = 0;
            sramAddr = 0;

            Array.Clear(aesKey, 0, AesBlockSize);
            Array.Clear(aesIv, 0, AesBlockSize);
            Array.Clear(aesCtr, 0, AesBlockSize);

            InitializeSramPattern();

            IRQ.Unset();
        }

        public long Size => 0x2000;

        public GPIO IRQ { get; }

        private void InitializeSramPattern()
        {
            for(var i = 0; i < CC310SramSize; i += 4)
            {
                cc310Sram[i] = 0x12;
                cc310Sram[i + 1] = 0xFA;
                cc310Sram[i + 2] = 0x12;
                cc310Sram[i + 3] = 0xFA;
            }
        }

        // ====================================================================
        // Register definitions
        // ====================================================================

        private void DefineRegisters()
        {
            // ── nRF52840 CRYPTOCELL wrapper ─────────────────────────────────
            Registers.Enable.Define(this)
                .WithFlag(0, out enabled, name: "ENABLE")
                .WithReservedBits(1, 31)
            ;

            // ── CC_PKA (stub registers) ───────────────────────────────────
            DefinePkaRegisters();

            // ── CC_RNG ──────────────────────────────────────────────────────
            Registers.RngImr.Define(this, 0x1F)
                .WithValueField(0, 5, out rngImrField, name: "RNG_IMR")
                .WithReservedBits(5, 27)
            ;

            Registers.RngIsr.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "RNG_ISR",
                    valueProviderCallback: _ => (uint)rngIsr)
            ;

            Registers.RngIcr.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "RNG_ICR",
                    writeCallback: (_, value) => { rngIsr &= ~(int)value; })
            ;

            Registers.TrngConfig.Define(this)
                .WithValueField(0, 32, name: "TRNG_CONFIG")
            ;

            Registers.TrngValid.Define(this)
                .WithFlag(0, FieldMode.Read, name: "TRNG_VALID",
                    valueProviderCallback: _ => noiseOn)
                .WithReservedBits(1, 31)
            ;

            DefineEhrDataRegisters();

            Registers.NoiseSource.Define(this)
                .WithFlag(0, name: "NOISE_SOURCE",
                    writeCallback: (_, value) =>
                    {
                        noiseOn = value;
                        if(value)
                        {
                            CompleteEhr();
                        }
                    },
                    valueProviderCallback: _ => noiseOn)
                .WithReservedBits(1, 31)
            ;

            Registers.SampleCnt.Define(this)
                .WithValueField(0, 32, name: "SAMPLE_CNT")
            ;

            Registers.AutocorrStatistic.Define(this)
                .WithValueField(0, 32, name: "AUTOCORR_STATISTIC")
            ;

            Registers.TrngDebug.Define(this)
                .WithValueField(0, 32, name: "TRNG_DEBUG")
            ;

            Registers.RngSwReset.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "RNG_SW_RESET",
                    writeCallback: (_, __) => ResetRng())
            ;

            Registers.RngBusy.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "RNG_BUSY",
                    valueProviderCallback: _ => 0)
            ;

            Registers.TrngReset.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "TRNG_RESET",
                    writeCallback: (_, __) => ResetRng())
            ;

            Registers.RngHwFlags.Define(this, 0x0000000F)
                .WithValueField(0, 32, FieldMode.Read, name: "RNG_HW_FLAGS")
            ;

            Registers.RngClk.Define(this)
                .WithValueField(0, 32, name: "RNG_CLK")
            ;

            Registers.RngDma.Define(this)
                .WithValueField(0, 32, name: "RNG_DMA",
                    writeCallback: (_, value) =>
                    {
                        if((value & 1) != 0)
                        {
                            RunRngDma();
                        }
                    })
            ;

            Registers.RngDmaRoscLen.Define(this)
                .WithValueField(0, 32, out rngDmaRoscLen, name: "RNG_DMA_ROSC_LEN")
            ;

            Registers.RngDmaSramAddr.Define(this)
                .WithValueField(0, 32, out rngDmaSramAddrField, name: "RNG_DMA_SRAM_ADDR")
            ;

            Registers.RngDmaSamples.Define(this)
                .WithValueField(0, 32, out rngDmaSamplesField, name: "RNG_DMA_SAMPLES")
            ;

            Registers.RngWatchdogVal.Define(this)
                .WithValueField(0, 32, name: "RNG_WATCHDOG_VAL")
            ;

            Registers.RngDmaBusy.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "RNG_DMA_BUSY",
                    valueProviderCallback: _ => 0)
            ;

            // ── CC_CHACHA (stubs) ─────────────────────────────────────────
            DefineChachaRegisters();

            // ── CC_AES ──────────────────────────────────────────────────────
            DefineAesKeyRegisters();
            DefineAesIvRegisters();
            DefineAesCtrRegisters();

            Registers.AesBusy.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "AES_BUSY",
                    valueProviderCallback: _ => 0)
            ;

            Registers.AesSk.Define(this)
                .WithValueField(0, 32, name: "AES_SK")
            ;

            Registers.AesCmacInit.Define(this)
                .WithValueField(0, 32, name: "AES_CMAC_INIT")
            ;

            Registers.AesRemaining.Define(this)
                .WithValueField(0, 32, name: "AES_REMAINING")
            ;

            Registers.AesControl.Define(this)
                .WithValueField(0, 32, name: "AES_CONTROL",
                    writeCallback: (_, value) => { aesControl = (int)value; },
                    valueProviderCallback: _ => (uint)aesControl)
            ;

            Registers.AesHwFlags.Define(this, 0x00000108)
                .WithValueField(0, 32, FieldMode.Read, name: "AES_HW_FLAGS")
            ;

            Registers.AesCtrNoInc.Define(this)
                .WithValueField(0, 32, name: "AES_CTR_NO_INC")
            ;

            Registers.AesSwReset.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "AES_SW_RESET",
                    writeCallback: (_, __) => ResetAes())
            ;

            Registers.AesCmacSize0Kick.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "AES_CMAC_SIZE0_KICK")
            ;

            // ── CC_HASH (stubs) ───────────────────────────────────────────
            DefineHashRegisters();

            // ── CC_MISC ───────────────────────────────────────────────────
            Registers.AesClk.Define(this)
                .WithValueField(0, 32, name: "AES_CLK")
            ;

            Registers.HashClk.Define(this)
                .WithValueField(0, 32, name: "HASH_CLK")
            ;

            Registers.PkaClk.Define(this)
                .WithValueField(0, 32, name: "PKA_CLK")
            ;

            Registers.DmaClk.Define(this)
                .WithValueField(0, 32, name: "DMA_CLK")
            ;

            Registers.ClkStatus.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "CLK_STATUS")
            ;

            Registers.ChachaClk.Define(this)
                .WithValueField(0, 32, name: "CHACHA_CLK")
            ;

            // ── CC_CTL ──────────────────────────────────────────────────────
            Registers.CryptoCtl.Define(this)
                .WithValueField(0, 32, name: "CRYPTO_CTL",
                    writeCallback: (_, value) => { cryptoCtl = (int)value; },
                    valueProviderCallback: _ => (uint)cryptoCtl)
            ;

            Registers.CryptoBusy.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "CRYPTO_BUSY",
                    valueProviderCallback: _ => 0)
            ;

            Registers.CtlHashBusy.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "HASH_BUSY",
                    valueProviderCallback: _ => 0)
            ;

            Registers.ContextId.Define(this)
                .WithValueField(0, 32, name: "CONTEXT_ID")
            ;

            // SaSi_LibInit reads this and checks (value >> 24) == 0xF0.
            Registers.PeripheralId.Define(this, 0xF0000000)
                .WithValueField(0, 32, FieldMode.Read, name: "PERIPHERAL_ID")
            ;

            // ── CC_HOST_RGF ─────────────────────────────────────────────────
            Registers.HostIrr.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "HOST_IRR",
                    valueProviderCallback: _ => (uint)hostIrr)
            ;

            Registers.HostImr.Define(this, 0x01FFFFFF)
                .WithValueField(0, 32, name: "HOST_IMR",
                    writeCallback: (_, value) => { hostImr = (int)value; },
                    valueProviderCallback: _ => (uint)hostImr)
            ;

            Registers.HostIcr.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "HOST_ICR",
                    writeCallback: (_, value) => {
                        hostIrr &= ~(int)value;
                        // Deassert IRQ when all pending bits are cleared
                        if(hostIrr == 0)
                        {
                            IRQ.Unset();
                        }
                    })
            ;

            Registers.HostEndianness.Define(this)
                .WithValueField(0, 32, name: "HOST_ENDIANNESS")
            ;

            Registers.HostSignature.Define(this, 0x20E00000)
                .WithValueField(0, 32, FieldMode.Read, name: "HOST_SIGNATURE")
            ;

            Registers.HostBoot.Define(this, 0x4622982C)
                .WithValueField(0, 32, FieldMode.Read, name: "HOST_BOOT")
            ;

            Registers.HostCryptokeySel.Define(this)
                .WithValueField(0, 32, name: "HOST_CRYPTOKEY_SEL")
            ;

            Registers.HostIotKprtlLck.Define(this)
                .WithValueField(0, 32, name: "HOST_IOT_KPRTL_LCK")
            ;

            Registers.HostIotKdr0.Define(this, 0x00000001)
                .WithValueField(0, 32, name: "HOST_IOT_KDR0")
            ;

            Registers.HostIotKdr1.Define(this)
                .WithValueField(0, 32, name: "HOST_IOT_KDR1")
            ;

            Registers.HostIotKdr2.Define(this)
                .WithValueField(0, 32, name: "HOST_IOT_KDR2")
            ;

            Registers.HostIotKdr3.Define(this)
                .WithValueField(0, 32, name: "HOST_IOT_KDR3")
            ;

            Registers.HostIotLcs.Define(this, 0x102)
                .WithValueField(0, 32, name: "HOST_IOT_LCS")
            ;

            Registers.HostPowerDownEn.Define(this)
                .WithValueField(0, 32, name: "HOST_POWER_DOWN_EN")
            ;

            // ── CC_DIN ──────────────────────────────────────────────────────
            // DIN_BUFFER and DOUT_BUFFER share offset 0xC00. In real HW DIN is W,
            // DOUT is R at the same address. We model as single R/W register.
            Registers.DinBuffer.Define(this)
                .WithValueField(0, 32, name: "DIN_BUFFER")
            ;

            Registers.DinDmaMemBusy.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "DIN_DMA_MEM_BUSY",
                    valueProviderCallback: _ => 0)
            ;

            Registers.SrcMemAddr.Define(this)
                .WithValueField(0, 32, out srcMemAddr, name: "SRC_MEM_ADDR")
            ;

            Registers.SrcMemSize.Define(this)
                .WithValueField(0, 32, name: "SRC_MEM_SIZE",
                    writeCallback: (_, value) =>
                    {
                        srcMemSize = (int)value;
                        if(value != 0)
                        {
                            if(cryptoCtl == 0)
                            {
                                DoBypassDma();
                                PendIrq(IrrBypassComplete);
                            }
                            else
                            {
                                DoAesDma();
                                PendIrq(IrrBypassComplete);
                            }
                        }
                    },
                    valueProviderCallback: _ => (uint)srcMemSize)
            ;

            Registers.SrcSramAddr.Define(this)
                .WithValueField(0, 32, out srcSramAddr, name: "SRC_SRAM_ADDR")
            ;

            Registers.SrcSramSize.Define(this)
                .WithValueField(0, 32, name: "SRC_SRAM_SIZE",
                    writeCallback: (_, value) =>
                    {
                        srcSramSize = (int)value;
                        if(value != 0)
                        {
                            if(cryptoCtl == 0)
                            {
                                DoBypassDma();
                                PendIrq(IrrSramComplete);
                            }
                            else
                            {
                                DoAesDma();
                                PendIrq(IrrSramComplete);
                            }
                        }
                    },
                    valueProviderCallback: _ => (uint)srcSramSize)
            ;

            Registers.DinDmaSramBusy.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "DIN_DMA_SRAM_BUSY",
                    valueProviderCallback: _ => 0)
            ;

            Registers.DinDmaSramEndianness.Define(this)
                .WithValueField(0, 32, name: "DIN_DMA_SRAM_ENDIANNESS")
            ;

            Registers.DinSwReset.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "DIN_SW_RESET")
            ;

            Registers.DinCpuData.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "DIN_CPU_DATA")
            ;

            Registers.DinWriteAlign.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "DIN_WRITE_ALIGN")
            ;

            Registers.DinFifoEmpty.Define(this, 1)
                .WithValueField(0, 32, FieldMode.Read, name: "DIN_FIFO_EMPTY")
            ;

            Registers.DinFifoReset.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "DIN_FIFO_RESET")
            ;

            // ── CC_DOUT ─────────────────────────────────────────────────────
            Registers.DoutDmaMemBusy.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "DOUT_DMA_MEM_BUSY",
                    valueProviderCallback: _ => 0)
            ;

            Registers.DstMemAddr.Define(this)
                .WithValueField(0, 32, out dstMemAddr, name: "DST_MEM_ADDR")
            ;

            Registers.DstMemSize.Define(this)
                .WithValueField(0, 32, out dstMemSize, name: "DST_MEM_SIZE")
            ;

            Registers.DstSramAddr.Define(this)
                .WithValueField(0, 32, out dstSramAddr, name: "DST_SRAM_ADDR")
            ;

            Registers.DstSramSize.Define(this)
                .WithValueField(0, 32, out dstSramSize, name: "DST_SRAM_SIZE")
            ;

            Registers.DoutDmaSramBusy.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "DOUT_DMA_SRAM_BUSY",
                    valueProviderCallback: _ => 0)
            ;

            Registers.DoutDmaSramEndianness.Define(this)
                .WithValueField(0, 32, name: "DOUT_DMA_SRAM_ENDIANNESS")
            ;

            Registers.DoutReadAlign.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "DOUT_READ_ALIGN")
            ;

            Registers.DoutFifoEmpty.Define(this, 1)
                .WithValueField(0, 32, FieldMode.Read, name: "DOUT_FIFO_EMPTY")
            ;

            Registers.DoutSwReset.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "DOUT_SW_RESET")
            ;

            // ── CC_RNG_SRAM ─────────────────────────────────────────────────
            Registers.SramData.Define(this)
                .WithValueField(0, 32, name: "SRAM_DATA",
                    writeCallback: (_, value) => WriteSramData((uint)value),
                    valueProviderCallback: _ => ReadSramData())
            ;

            Registers.SramAddrReg.Define(this)
                .WithValueField(0, 32, name: "SRAM_ADDR",
                    writeCallback: (_, value) => { sramAddr = (int)value; },
                    valueProviderCallback: _ => (uint)sramAddr)
            ;

            Registers.SramDataReady.Define(this, 1)
                .WithValueField(0, 32, FieldMode.Read, name: "SRAM_DATA_READY")
            ;

            // ── CC_NVM ──────────────────────────────────────────────────────
            Registers.NvmIsIdle.Define(this, 0x1)
                .WithFlag(0, FieldMode.Read, name: "NVM_IS_IDLE_VALUE")
                .WithReservedBits(1, 31)
            ;
        }

        // ── CC_PKA stub array registers ───────────────────────────────────
        private void DefinePkaRegisters()
        {
            for(var i = 0; i < PkaMemoryMapCount; i++)
            {
                var regOffset = (long)Registers.PkaMemoryMap0 + i * 4;
                var reg = new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: $"PKA_MEMORY_MAP_{i}");
                RegistersCollection.AddRegister(regOffset, reg);
            }

            Registers.PkaOpA.Define(this)
                .WithValueField(0, 32, name: "PKA_OP_A")
            ;

            Registers.PkaOpB.Define(this)
                .WithValueField(0, 32, name: "PKA_OP_B")
            ;

            Registers.PkaRes.Define(this)
                .WithValueField(0, 32, name: "PKA_RES")
            ;

            Registers.PkaShift.Define(this)
                .WithValueField(0, 32, name: "PKA_SHIFT")
            ;

            Registers.PkaCtrl.Define(this)
                .WithValueField(0, 32, name: "PKA_CTRL")
            ;

            Registers.PkaSramWaddr.Define(this)
                .WithValueField(0, 32, name: "PKA_SRAM_WADDR")
            ;

            Registers.PkaSramRaddr.Define(this)
                .WithValueField(0, 32, name: "PKA_SRAM_RADDR")
            ;

            Registers.PkaLLen.Define(this)
                .WithValueField(0, 32, name: "PKA_L_LEN")
            ;

            Registers.PkaPipeRdy.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "PKA_PIPE_RDY",
                    valueProviderCallback: _ => 1)
            ;

            Registers.PkaStatus.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "PKA_STATUS")
            ;

            Registers.PkaModT.Define(this)
                .WithValueField(0, 32, name: "PKA_MOD_T")
            ;

            Registers.PkaMsbAddr.Define(this)
                .WithValueField(0, 32, name: "PKA_MSB_ADDR")
            ;

            Registers.PkaOpcode.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "PKA_OPCODE")
            ;

            Registers.PkaNLen.Define(this)
                .WithValueField(0, 32, name: "PKA_N_LEN")
            ;

            Registers.PkaDone.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "PKA_DONE",
                    valueProviderCallback: _ => 1)
            ;

            Registers.PkaSramWdata.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "PKA_SRAM_WDATA")
            ;

            Registers.PkaSramRdata.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "PKA_SRAM_RDATA")
            ;

            Registers.PkaSramWclear.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "PKA_SRAM_WCLEAR")
            ;
        }

        private void DefineEhrDataRegisters()
        {
            for(var i = 0; i < EhrDataCount; i++)
            {
                var regOffset = (long)Registers.EhrData0 + i * 4;
                var isLast = (i == EhrDataCount - 1);
                var reg = new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Read, name: $"EHR_DATA_{i}",
                        valueProviderCallback: _ =>
                        {
                            var val = (uint)rng.Next();
                            if(isLast)
                            {
                                noiseOn = false;
                                rngIsr &= ~RngIsrEhrValid;
                            }
                            return val;
                        });
                RegistersCollection.AddRegister(regOffset, reg);
            }
        }

        // ── CC_CHACHA stub array registers ────────────────────────────────
        private void DefineChachaRegisters()
        {
            Registers.ChachaControl.Define(this)
                .WithValueField(0, 32, name: "CHACHA_CONTROL")
            ;

            Registers.ChachaVersion.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "CHACHA_VERSION")
            ;

            for(var i = 0; i < 8; i++)
            {
                var regOffset = (long)Registers.ChachaKey0 + i * 4;
                var reg = new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Write, name: $"CHACHA_KEY_{i}");
                RegistersCollection.AddRegister(regOffset, reg);
            }

            Registers.ChachaIv0.Define(this)
                .WithValueField(0, 32, name: "CHACHA_IV_0")
            ;

            Registers.ChachaIv1.Define(this)
                .WithValueField(0, 32, name: "CHACHA_IV_1")
            ;

            Registers.ChachaBusy.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "CHACHA_BUSY",
                    valueProviderCallback: _ => 0)
            ;

            Registers.ChachaHwFlags.Define(this, 0x00000001)
                .WithValueField(0, 32, FieldMode.Read, name: "CHACHA_HW_FLAGS")
            ;

            Registers.ChachaBlockCntLsb.Define(this)
                .WithValueField(0, 32, name: "CHACHA_BLOCK_CNT_LSB")
            ;

            Registers.ChachaBlockCntMsb.Define(this)
                .WithValueField(0, 32, name: "CHACHA_BLOCK_CNT_MSB")
            ;

            Registers.ChachaSwReset.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "CHACHA_SW_RESET")
            ;

            for(var i = 0; i < 8; i++)
            {
                var regOffset = (long)Registers.ChachaPoly1305Key0 + i * 4;
                var reg = new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Read, name: $"CHACHA_POLY1305_KEY_{i}");
                RegistersCollection.AddRegister(regOffset, reg);
            }

            Registers.ChachaEndianness.Define(this)
                .WithValueField(0, 32, name: "CHACHA_ENDIANNESS")
            ;

            Registers.ChachaDebug.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "CHACHA_DEBUG")
            ;
        }

        private void DefineAesKeyRegisters()
        {
            // 8 key registers: first 4 feed the 128-bit key, last 4 are write sinks
            for(var i = 0; i < AesKeyRegCount; i++)
            {
                var idx = i;
                var regOffset = (long)Registers.AesKey0_0 + i * 4;
                var reg = new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Write, name: $"AES_KEY_0_{idx}",
                        writeCallback: (_, value) =>
                        {
                            if(idx < AesWordCount)
                            {
                                StoreWord(aesKey, idx, (uint)value);
                            }
                        });
                RegistersCollection.AddRegister(regOffset, reg);
            }
        }

        private void DefineAesIvRegisters()
        {
            for(var i = 0; i < AesWordCount; i++)
            {
                var idx = i;
                var regOffset = (long)Registers.AesIv0_0 + i * 4;
                var reg = new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: $"AES_IV_0_{idx}",
                        writeCallback: (_, value) => StoreWord(aesIv, idx, (uint)value),
                        valueProviderCallback: _ => LoadWord(aesIv, idx));
                RegistersCollection.AddRegister(regOffset, reg);
            }
        }

        private void DefineAesCtrRegisters()
        {
            for(var i = 0; i < AesWordCount; i++)
            {
                var idx = i;
                var regOffset = (long)Registers.AesCtr0 + i * 4;
                var reg = new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: $"AES_CTR_{idx}",
                        writeCallback: (_, value) => StoreWord(aesCtr, idx, (uint)value),
                        valueProviderCallback: _ => LoadWord(aesCtr, idx));
                RegistersCollection.AddRegister(regOffset, reg);
            }
        }

        // ── CC_HASH stub registers ────────────────────────────────────────
        private void DefineHashRegisters()
        {
            for(var i = 0; i < HashHCount; i++)
            {
                var regOffset = (long)Registers.HashH0 + i * 4;
                var reg = new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: $"HASH_H_{i}");
                RegistersCollection.AddRegister(regOffset, reg);
            }

            Registers.HashPadAuto.Define(this)
                .WithValueField(0, 32, name: "HASH_PAD_AUTO")
            ;

            Registers.HashInitState.Define(this)
                .WithValueField(0, 32, name: "HASH_INIT_STATE")
            ;

            Registers.HashVersion.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "HASH_VERSION")
            ;

            Registers.HashControl.Define(this)
                .WithValueField(0, 32, name: "HASH_CONTROL")
            ;

            Registers.HashPad.Define(this)
                .WithValueField(0, 32, name: "HASH_PAD")
            ;

            Registers.HashPadForce.Define(this)
                .WithValueField(0, 32, name: "HASH_PAD_FORCE")
            ;

            Registers.HashCurLen0.Define(this)
                .WithValueField(0, 32, name: "HASH_CUR_LEN_0")
            ;

            Registers.HashCurLen1.Define(this)
                .WithValueField(0, 32, name: "HASH_CUR_LEN_1")
            ;

            Registers.HashHwFlags.Define(this, 0x00000000)
                .WithValueField(0, 32, FieldMode.Read, name: "HASH_HW_FLAGS")
            ;

            Registers.HashSwReset.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "HASH_SW_RESET")
            ;

            Registers.HashEndianness.Define(this)
                .WithValueField(0, 32, name: "HASH_ENDIANNESS")
            ;
        }

        // ====================================================================
        // AES key/IV/CTR byte array helpers
        // ====================================================================

        private static void StoreWord(byte[] buffer, int wordIndex, uint value)
        {
            var offset = wordIndex * 4;
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static uint LoadWord(byte[] buffer, int wordIndex)
        {
            var offset = wordIndex * 4;
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }

        // ====================================================================
        // RNG
        // ====================================================================

        private void CompleteEhr()
        {
            rngIsr |= RngIsrEhrValid;
            if((rngImrField.Value & RngIsrEhrValid) == 0)
            {
                PendIrq(IrrRngInt);
            }
        }

        private void ResetRng()
        {
            noiseOn = false;
            rngIsr = 0;
        }

        private void RunRngDma()
        {
            noiseOn = true;
            rngIsr |= RngIsrEhrValid;

            var dmaAddr = (int)rngDmaSramAddrField.Value;
            var nSamples = (int)rngDmaSamplesField.Value;
            if(nSamples == 0)
            {
                nSamples = 1;
            }
            var fillSize = Math.Min(nSamples * 24, CC310SramSize - dmaAddr);

            var buf = new byte[fillSize];
            rng.NextBytes(buf);
            Array.Copy(buf, 0, cc310Sram, dmaAddr, fillSize);

            PendIrq(IrrRngInt);
        }

        // ====================================================================
        // AES
        // ====================================================================

        private void ResetAes()
        {
            Array.Clear(aesKey, 0, AesBlockSize);
            Array.Clear(aesIv, 0, AesBlockSize);
            Array.Clear(aesCtr, 0, AesBlockSize);
            aesControl = 0;
        }

        private byte[] AesEcbEncryptBlock(byte[] key, byte[] plaintext)
        {
            var block = Block.UsingBytes(plaintext);
            using(var aes = AesProvider.GetEcbProvider(key))
            {
                aes.EncryptBlockInSitu(block);
            }
            var result = new byte[AesBlockSize];
            Array.Copy(block.Buffer, 0, result, 0, AesBlockSize);
            return result;
        }

        private byte[] AesEcbDecryptBlock(byte[] key, byte[] ciphertext)
        {
            var block = Block.UsingBytes(ciphertext);
            using(var aes = AesProvider.GetEcbProvider(key))
            {
                aes.DecryptBlockInSitu(block);
            }
            var result = new byte[AesBlockSize];
            Array.Copy(block.Buffer, 0, result, 0, AesBlockSize);
            return result;
        }

        private byte[] AesCtrProcess(byte[] key, byte[] nonce, byte[] data)
        {
            var ctr = new byte[AesBlockSize];
            Array.Copy(nonce, 0, ctr, 0, AesBlockSize);
            var result = new byte[data.Length];

            using(var aes = AesProvider.GetEcbProvider(key))
            {
                for(var i = 0; i < data.Length; i += AesBlockSize)
                {
                    var block = Block.UsingBytes((byte[])ctr.Clone());
                    aes.EncryptBlockInSitu(block);
                    var chunk = Math.Min(AesBlockSize, data.Length - i);
                    for(var j = 0; j < chunk; j++)
                    {
                        result[i + j] = (byte)(data[i + j] ^ block.Buffer[j]);
                    }
                    IncrementCounter(ctr);
                }
            }
            return result;
        }

        private byte[] AesCbcProcess(byte[] key, byte[] iv, byte[] data, bool encrypt)
        {
            var result = new byte[data.Length];
            using(var aes = AesProvider.GetCbcProvider(key, iv))
            {
                for(var i = 0; i < data.Length; i += AesBlockSize)
                {
                    var inputBlock = new byte[AesBlockSize];
                    var copyLen = Math.Min(AesBlockSize, data.Length - i);
                    Array.Copy(data, i, inputBlock, 0, copyLen);

                    var block = Block.UsingBytes(inputBlock);
                    if(encrypt)
                    {
                        aes.EncryptBlockInSitu(block);
                    }
                    else
                    {
                        aes.DecryptBlockInSitu(block);
                    }

                    var outLen = Math.Min(AesBlockSize, data.Length - i);
                    Array.Copy(block.Buffer, 0, result, i, outLen);
                }
            }
            return result;
        }

        private static void IncrementCounter(byte[] ctr)
        {
            var carry = 1;
            for(var k = ctr.Length - 1; k >= 0; k--)
            {
                carry += ctr[k];
                ctr[k] = (byte)(carry & 0xFF);
                carry >>= 8;
                if(carry == 0)
                {
                    break;
                }
            }
        }

        // ====================================================================
        // DMA — bypass (no crypto) and AES paths
        // ====================================================================
        // Register naming note: in the Python model (and the CC310 blob),
        // the DIN/DOUT naming is swapped from what you might expect.
        // What we call SRC_* (0x1C2x-0x1C3x) is actually DOUT (destination).
        // What we call DST_* (0x1D2x-0x1D3x) is actually DIN (source).

        private void DoBypassDma()
        {
            var dinAddrSramPath = (int)dstMemAddr.Value;
            var dinSzSramPath = (int)dstMemSize.Value;
            var dinAddrMemPath = (int)dstSramAddr.Value;
            var dinSzMemPath = (int)dstSramSize.Value;

            var doutAddrMem = (int)srcMemAddr.Value;
            var doutSzMem = srcMemSize;
            var doutAddrSram = (int)srcSramAddr.Value;
            var doutSzSram = srcSramSize;

            byte[] srcData = null;

            if(dinSzSramPath > 0)
            {
                srcData = ReadFromAddressOrSram(dinAddrSramPath, dinSzSramPath);
            }
            else if(dinSzMemPath > 0)
            {
                srcData = ReadFromAddressOrSram(dinAddrMemPath, dinSzMemPath);
            }

            if(srcData == null || srcData.Length == 0)
            {
                return;
            }

            if(doutSzSram > 0)
            {
                WriteToAddressOrSram(doutAddrSram, srcData, doutSzSram);
            }
            else if(doutSzMem > 0 && doutAddrMem >= RamBase)
            {
                WriteSysbusBytes((ulong)doutAddrMem, srcData, doutSzMem);
            }
        }

        private void DoAesDma()
        {
            var decrypt = (aesControl & 0x1) != 0;
            var mode = (aesControl >> 1) & 0x3;

            var sMem = (int)srcMemAddr.Value;
            var sMemSz = srcMemSize;
            var sSram = (int)srcSramAddr.Value;
            var sSramSz = srcSramSize;

            var dMem = (int)dstMemAddr.Value;
            var dMemSz = (int)dstMemSize.Value;
            var dSram = (int)dstSramAddr.Value;
            var dSramSz = (int)dstSramSize.Value;

            byte[] srcData = null;

            if(sMem >= RamBase && sMemSz > 0)
            {
                srcData = ReadSysbusBytes((ulong)sMem, sMemSz);
            }
            else if(sSramSz > 0 && sSram < CC310SramSize)
            {
                var end = Math.Min(sSram + sSramSz, CC310SramSize);
                var len = end - sSram;
                srcData = new byte[len];
                Array.Copy(cc310Sram, sSram, srcData, 0, len);
            }

            if(srcData == null || srcData.Length == 0)
            {
                return;
            }

            byte[] outData;
            switch(mode)
            {
            case 0: // ECB
                outData = ProcessEcb(srcData, decrypt);
                break;
            case 1: // CBC
                outData = AesCbcProcess(aesKey, aesIv, srcData, !decrypt);
                break;
            case 2: // CTR
                outData = AesCtrProcess(aesKey, aesCtr, srcData);
                break;
            default: // CMAC or other — treat as CBC-MAC with zero IV
                outData = AesCbcProcess(aesKey, new byte[AesBlockSize], srcData, true);
                break;
            }

            if(dMem >= RamBase && dMemSz > 0)
            {
                WriteSysbusBytes((ulong)dMem, outData, dMemSz);
            }
            if(dSramSz > 0 && dSram < CC310SramSize)
            {
                var end = Math.Min(dSram + dSramSz, CC310SramSize);
                var copyLen = Math.Min(outData.Length, end - dSram);
                Array.Copy(outData, 0, cc310Sram, dSram, copyLen);
            }
        }

        private byte[] ProcessEcb(byte[] srcData, bool decrypt)
        {
            var result = new byte[srcData.Length];
            for(var i = 0; i < srcData.Length; i += AesBlockSize)
            {
                var inputBlock = new byte[AesBlockSize];
                var copyLen = Math.Min(AesBlockSize, srcData.Length - i);
                Array.Copy(srcData, i, inputBlock, 0, copyLen);

                byte[] outputBlock;
                if(decrypt)
                {
                    outputBlock = AesEcbDecryptBlock(aesKey, inputBlock);
                }
                else
                {
                    outputBlock = AesEcbEncryptBlock(aesKey, inputBlock);
                }

                var outLen = Math.Min(AesBlockSize, srcData.Length - i);
                Array.Copy(outputBlock, 0, result, i, outLen);
            }
            return result;
        }

        // ====================================================================
        // SRAM read/write with auto-increment
        // ====================================================================

        private void WriteSramData(uint value)
        {
            if(sramAddr + 3 < CC310SramSize)
            {
                // When blob writes test pattern, fill adjacent SRAM for readback
                if(value == SramIntegrityPattern)
                {
                    var fillEnd = Math.Min(32, CC310SramSize);
                    for(var k = 0; k < fillEnd; k += 4)
                    {
                        cc310Sram[k] = 0x12;
                        cc310Sram[k + 1] = 0xFA;
                        cc310Sram[k + 2] = 0x12;
                        cc310Sram[k + 3] = 0xFA;
                    }
                }

                cc310Sram[sramAddr] = (byte)(value & 0xFF);
                cc310Sram[sramAddr + 1] = (byte)((value >> 8) & 0xFF);
                cc310Sram[sramAddr + 2] = (byte)((value >> 16) & 0xFF);
                cc310Sram[sramAddr + 3] = (byte)((value >> 24) & 0xFF);
                sramAddr += 4;
            }
        }

        private uint ReadSramData()
        {
            if(sramAddr + 3 < CC310SramSize)
            {
                var val = (uint)(cc310Sram[sramAddr]
                    | (cc310Sram[sramAddr + 1] << 8)
                    | (cc310Sram[sramAddr + 2] << 16)
                    | (cc310Sram[sramAddr + 3] << 24));
                sramAddr += 4;
                return val;
            }
            return (uint)rng.Next();
        }

        // ====================================================================
        // System bus helpers
        // ====================================================================

        private byte[] ReadSysbusBytes(ulong address, int size)
        {
            return sysbus.ReadBytes(address, size);
        }

        private void WriteSysbusBytes(ulong address, byte[] data, int maxSize)
        {
            var writeLen = Math.Min(data.Length, maxSize);
            var toWrite = new byte[writeLen];
            Array.Copy(data, 0, toWrite, 0, writeLen);
            sysbus.WriteBytes(toWrite, address);
        }

        private byte[] ReadFromAddressOrSram(int address, int size)
        {
            if(address >= RamBase)
            {
                return ReadSysbusBytes((ulong)address, size);
            }
            if(address < CC310SramSize)
            {
                var end = Math.Min(address + size, CC310SramSize);
                var len = end - address;
                var result = new byte[len];
                Array.Copy(cc310Sram, address, result, 0, len);
                return result;
            }
            return new byte[0];
        }

        private void WriteToAddressOrSram(int address, byte[] data, int maxSize)
        {
            if(address >= RamBase)
            {
                WriteSysbusBytes((ulong)address, data, maxSize);
            }
            else if(address < CC310SramSize)
            {
                var end = Math.Min(address + maxSize, CC310SramSize);
                var copyLen = Math.Min(data.Length, end - address);
                Array.Copy(data, 0, cc310Sram, address, copyLen);
            }
        }

        // ====================================================================
        // Interrupt signaling
        // ====================================================================

        private void PendIrq(int irrBits)
        {
            hostIrr |= irrBits;
            IRQ.Set(true);
        }

        // ====================================================================
        // Fields
        // ====================================================================

        private IFlagRegisterField enabled;
        private IValueRegisterField rngImrField;
        private IValueRegisterField rngDmaRoscLen;
        private IValueRegisterField rngDmaSramAddrField;
        private IValueRegisterField rngDmaSamplesField;

        private IValueRegisterField srcMemAddr;
        private IValueRegisterField srcSramAddr;
        private IValueRegisterField dstMemAddr;
        private IValueRegisterField dstMemSize;
        private IValueRegisterField dstSramAddr;
        private IValueRegisterField dstSramSize;

        private int srcMemSize;
        private int srcSramSize;

        private int hostIrr;
        private int hostImr;
        private int rngIsr;
        private bool noiseOn;
        private int aesControl;
        private int cryptoCtl;
        private int sramAddr;

        private readonly byte[] cc310Sram;
        private readonly byte[] aesKey;
        private readonly byte[] aesIv;
        private readonly byte[] aesCtr;
        private readonly PseudorandomNumberGenerator rng;

        // ====================================================================
        // Constants
        // ====================================================================

        private const int CC310SramSize = 4096;
        private const int AesBlockSize = 16;
        private const int AesWordCount = 4;
        private const int AesKeyRegCount = 8;
        private const int EhrDataCount = 6;
        private const int HashHCount = 8;
        private const int PkaMemoryMapCount = 28;
        private const int RamBase = 0x20000000;
        private const uint SramIntegrityPattern = 0xFA12FA12;

        // HOST_IRR bit patterns
        // bit0=SRAM_TO_DIN_INT, bit1=DOUT_TO_SRAM_INT, bit2=MEM_TO_DIN_INT,
        // bit3=DOUT_TO_MEM_INT, bit4=AHB_ERR_INT, bit5=PKA_INT, bit10=RNG_INT
        private const int IrrBypassComplete = 0x2C;
        private const int IrrSramComplete = 0xA3;
        private const int IrrRngInt = 0x400;

        // RNG_ISR bit fields
        private const int RngIsrEhrValid = 0x1;

        // ====================================================================
        // Register map
        // ====================================================================

        private enum Registers
        {
            // ── nRF52840 CRYPTOCELL wrapper (no +0x1000) ──────────────────
            Enable                = 0x0500,

            // ── CC_PKA (CC310 offset 0x0-0xE4, +0x1000) ──────────────────
            PkaMemoryMap0         = 0x1000,  // through PkaMemoryMap27 = 0x106C
            PkaOpA                = 0x1070,
            PkaOpB                = 0x1074,
            PkaRes                = 0x1078,
            PkaShift              = 0x107C,
            PkaCtrl               = 0x1080,
            PkaSramWaddr          = 0x1084,
            PkaSramRaddr          = 0x1088,
            PkaLLen               = 0x108C,
            PkaPipeRdy            = 0x1090,
            PkaStatus             = 0x1094,
            PkaModT               = 0x1098,
            PkaMsbAddr            = 0x109C,
            PkaOpcode             = 0x10C4,
            PkaNLen               = 0x10D0,
            PkaDone               = 0x10D4,
            PkaSramWdata          = 0x10D8,
            PkaSramRdata          = 0x10DC,
            PkaSramWclear         = 0x10E0,

            // ── CC_RNG (CC310 offset 0x100-0x1DC, +0x1000) ───────────────
            RngImr                = 0x1100,
            RngIsr                = 0x1104,
            RngIcr                = 0x1108,
            TrngConfig            = 0x110C,
            TrngValid             = 0x1110,
            EhrData0              = 0x1114,
            EhrData1              = 0x1118,
            EhrData2              = 0x111C,
            EhrData3              = 0x1120,
            EhrData4              = 0x1124,
            EhrData5              = 0x1128,
            NoiseSource           = 0x112C,
            SampleCnt             = 0x1130,
            AutocorrStatistic     = 0x1134,
            TrngDebug             = 0x1138,
            RngSwReset            = 0x1140,
            RngBusy               = 0x11B8,
            TrngReset             = 0x11BC,
            RngHwFlags            = 0x11C0,
            RngClk                = 0x11C4,
            RngDma                = 0x11C8,
            RngDmaRoscLen         = 0x11CC,
            RngDmaSramAddr        = 0x11D0,
            RngDmaSamples         = 0x11D4,
            RngWatchdogVal        = 0x11D8,
            RngDmaBusy            = 0x11DC,

            // ── CC_CHACHA (CC310 offset 0x380-0x3E8, +0x1000) ────────────
            ChachaControl         = 0x1380,
            ChachaVersion         = 0x1384,
            ChachaKey0            = 0x1388,
            ChachaKey1            = 0x138C,
            ChachaKey2            = 0x1390,
            ChachaKey3            = 0x1394,
            ChachaKey4            = 0x1398,
            ChachaKey5            = 0x139C,
            ChachaKey6            = 0x13A0,
            ChachaKey7            = 0x13A4,
            ChachaIv0             = 0x13A8,
            ChachaIv1             = 0x13AC,
            ChachaBusy            = 0x13B0,
            ChachaHwFlags         = 0x13B4,
            ChachaBlockCntLsb     = 0x13B8,
            ChachaBlockCntMsb     = 0x13BC,
            ChachaSwReset         = 0x13C0,
            ChachaPoly1305Key0    = 0x13C4,
            ChachaPoly1305Key1    = 0x13C8,
            ChachaPoly1305Key2    = 0x13CC,
            ChachaPoly1305Key3    = 0x13D0,
            ChachaPoly1305Key4    = 0x13D4,
            ChachaPoly1305Key5    = 0x13D8,
            ChachaPoly1305Key6    = 0x13DC,
            ChachaPoly1305Key7    = 0x13E0,
            ChachaEndianness      = 0x13E4,
            ChachaDebug           = 0x13E8,

            // ── CC_AES (CC310 offset 0x400-0x524, +0x1000) ───────────────
            AesKey0_0             = 0x1400,
            AesKey0_1             = 0x1404,
            AesKey0_2             = 0x1408,
            AesKey0_3             = 0x140C,
            AesKey0_4             = 0x1410,
            AesKey0_5             = 0x1414,
            AesKey0_6             = 0x1418,
            AesKey0_7             = 0x141C,
            AesIv0_0              = 0x1440,
            AesIv0_1              = 0x1444,
            AesIv0_2              = 0x1448,
            AesIv0_3              = 0x144C,
            AesCtr0               = 0x1460,
            AesCtr1               = 0x1464,
            AesCtr2               = 0x1468,
            AesCtr3               = 0x146C,
            AesBusy               = 0x1470,
            AesSk                 = 0x1478,
            AesCmacInit           = 0x147C,
            AesRemaining          = 0x14BC,
            AesControl            = 0x14C0,
            AesHwFlags            = 0x14C8,
            AesCtrNoInc           = 0x14D8,
            AesSwReset            = 0x14F4,
            AesCmacSize0Kick      = 0x1524,

            // ── CC_HASH (CC310 offset 0x640-0x7E8, +0x1000) ──────────────
            HashH0                = 0x1640,
            HashH1                = 0x1644,
            HashH2                = 0x1648,
            HashH3                = 0x164C,
            HashH4                = 0x1650,
            HashH5                = 0x1654,
            HashH6                = 0x1658,
            HashH7                = 0x165C,
            HashPadAuto           = 0x1684,
            HashInitState         = 0x1694,
            HashVersion           = 0x17B0,
            HashControl           = 0x17C0,
            HashPad               = 0x17C4,
            HashPadForce          = 0x17C8,
            HashCurLen0           = 0x17CC,
            HashCurLen1           = 0x17D0,
            HashHwFlags           = 0x17DC,
            HashSwReset           = 0x17E4,
            HashEndianness        = 0x17E8,

            // ── CC_MISC (CC310 offset 0x810-0x858, +0x1000) ──────────────
            AesClk                = 0x1810,
            HashClk               = 0x1818,
            PkaClk                = 0x181C,
            DmaClk                = 0x1820,
            ClkStatus             = 0x1824,
            ChachaClk             = 0x1858,

            // ── CC_CTL (CC310 offset 0x900-0x930, +0x1000) ───────────────
            CryptoCtl             = 0x1900,
            CryptoBusy            = 0x1910,
            CtlHashBusy           = 0x191C,
            PeripheralId          = 0x1928,
            ContextId             = 0x1930,

            // ── CC_HOST_RGF (CC310 offset 0xA00-0xA60, +0x1000) ──────────
            HostIrr               = 0x1A00,
            HostImr               = 0x1A04,
            HostIcr               = 0x1A08,
            HostEndianness        = 0x1A0C,
            HostSignature         = 0x1A24,
            HostBoot              = 0x1A28,
            HostCryptokeySel      = 0x1A38,
            HostIotKprtlLck       = 0x1A4C,
            HostIotKdr0           = 0x1A50,
            HostIotKdr1           = 0x1A54,
            HostIotKdr2           = 0x1A58,
            HostIotKdr3           = 0x1A5C,
            HostIotLcs            = 0x1A60,
            HostPowerDownEn       = 0x1A78,

            // ── CC_DIN (CC310 offset 0xC00-0xC58, +0x1000) ───────────────
            // DIN_BUFFER(W) and DOUT_BUFFER(R) share 0xC00; single R/W entry
            DinBuffer             = 0x1C00,
            DinDmaMemBusy         = 0x1C20,
            SrcMemAddr            = 0x1C28,
            SrcMemSize            = 0x1C2C,
            SrcSramAddr           = 0x1C30,
            SrcSramSize           = 0x1C34,
            DinDmaSramBusy        = 0x1C38,
            DinDmaSramEndianness  = 0x1C3C,
            DinSwReset            = 0x1C44,
            DinCpuData            = 0x1C48,
            DinWriteAlign         = 0x1C4C,
            DinFifoEmpty          = 0x1C50,
            DinFifoReset          = 0x1C58,

            // ── CC_DOUT (CC310 offset 0xD20-0xD58, +0x1000) ──────────────
            DoutDmaMemBusy        = 0x1D20,
            DstMemAddr            = 0x1D28,
            DstMemSize            = 0x1D2C,
            DstSramAddr           = 0x1D30,
            DstSramSize           = 0x1D34,
            DoutDmaSramBusy       = 0x1D38,
            DoutDmaSramEndianness = 0x1D3C,
            DoutReadAlign         = 0x1D44,
            DoutFifoEmpty         = 0x1D50,
            DoutSwReset           = 0x1D58,

            // ── CC_RNG_SRAM (CC310 offset 0xF00-0xF08, +0x1000) ──────────
            SramData              = 0x1F00,
            SramAddrReg           = 0x1F04,
            SramDataReady         = 0x1F08,

            // ── CC_NVM (CC310 offset 0xF10, +0x1000) ────────────────────
            NvmIsIdle             = 0x1F10,
        }
    }
}
